using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// The Power Wheel's Shortcuts (see <see cref="ShortcutsFileData"/>): shortcuts.json, every edit
/// Settings → Shortcuts makes to it, the icons the wheel draws, and starting what a shortcut
/// names. Edits come from the UI thread only; the pad thread never reads this class, it reads the
/// bindings <see cref="ActionService"/> was handed after the last change.
///
/// Every edit is checked here before it is stored, because the page is not the only way a value
/// arrives: shortcuts.json can be edited by hand. An address is http or https or nothing, a path
/// is absolute, a kind is one this file knows, and anything that fails is dropped rather than
/// kept as something that would do the unexpected when pressed.
/// </summary>
internal class ShortcutService
{
    public const int MaxItems = 8;
    public const int MaxTabs = 12;
    private const int MaxName = 60, MaxTabName = 24, MaxText = 2048;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly Regex IdShape = new("^[a-z0-9][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    private readonly string _path;
    private ShortcutsFileData _file = new();

    /// <summary>An icon and whether the thing is still there, per shortcut, found off the UI
    /// thread. Keyed by what the shortcut points at, so an edit that changes the name keeps it.</summary>
    private readonly Dictionary<string, (string? Icon, bool Missing)> _looks = new();
    private readonly object _gate = new();
    private bool _resolving;

    /// <summary>Raised on the UI thread after any edit, so the bindings and the page follow.</summary>
    public event Action? Changed;
    /// <summary>Raised from a worker when icons have been found: the page wants them re-sent.</summary>
    public event Action? LooksChanged;

    public ShortcutService(string? path = null)
    {
        _path = path ?? Path.Combine(Paths.DataDir, "shortcuts.json");
        ShortcutIcons.Dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_path))!, "shortcut-icons");
    }

    // ---- the file ----

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                _file = JsonSerializer.Deserialize<ShortcutsFileData>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            // Kept beside the file rather than overwritten, so a hand edit with a typo is not lost.
            Log.Info($"Shortcuts load failed, starting from the defaults: {ex.Message}");
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch { }
            _file = new();
        }
        _file.Tabs = CleanTabs(_file.Tabs);
        if (_file.Tabs.Count == 0)
        {
            _file.Tabs = ShortcutCatalog.Defaults();
            Save();
        }
        PruneIcons();
    }

    /// <summary>Written beside the file and moved over it, so a kill mid-write leaves the old file
    /// whole (LibraryStore's plain write is how library.json has been truncated before).</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_file, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Info($"Shortcuts save failed: {ex.Message}"); }
    }

    private void Commit()
    {
        Save();
        PruneIcons();
        Changed?.Invoke();
    }

    /// <summary>The chosen pictures and site icons that no shortcut uses any more go: a picture
    /// replaced, a shortcut removed, the last shortcut to a site gone.</summary>
    private void PruneIcons()
    {
        var items = _file.Tabs.SelectMany(t => t.Items).ToList();
        ShortcutIcons.Prune(items.Where(i => i.IconFile is not null).Select(i => i.IconFile!)
            .Concat(items.Where(i => i.Kind == ShortcutKinds.Url).Select(i => UrlHost(i.Target)).Where(h => h is not null).Select(h => ShortcutIcons.FaviconName(h!))));
    }

    private static string? UrlHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;

    /// <summary>A picture chosen for a shortcut (already made into ShortcutIcons' PNG). The one it
    /// replaces is deleted with the next tidy. False when the shortcut is gone.</summary>
    public bool SetIcon(string id, byte[] png)
    {
        if (Locate(id) is not { } at) return false;
        var name = ShortcutIcons.NewCustomName(id);
        ShortcutIcons.Save(name, png);
        at.Tab.Items[at.Index].IconFile = name;
        Commit();
        return true;
    }

    public IReadOnlyList<ShortcutTab> Tabs => _file.Tabs;

    public ShortcutDef? Find(string? id) => _file.Tabs.SelectMany(t => t.Items).FirstOrDefault(i => i.Id == id);

    private (ShortcutTab Tab, int Index)? Locate(string? id)
    {
        foreach (var t in _file.Tabs)
        {
            int i = t.Items.FindIndex(x => x.Id == id);
            if (i >= 0) return (t, i);
        }
        return null;
    }

    private ShortcutTab? Tab(string? id) => _file.Tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>The buttons, for <see cref="ActionService.SetShortcutBindings"/>.</summary>
    public IEnumerable<(string Combo, string Id, string Name)> Bindings() =>
        _file.Tabs.SelectMany(t => t.Items)
            .Where(i => !string.IsNullOrEmpty(i.Button))
            .Select(i => (i.Button!, i.Id, i.Name));

    // ---- edits (the bridge's shortcut* commands) ----

    /// <summary>Add a shortcut to a tab, or replace the one with its id wherever it is. A new one
    /// lands at <paramref name="index"/> (the end when null). Null when it could not be stored:
    /// nothing usable in it, or the tab is full.</summary>
    public ShortcutDef? Save(string tabId, ShortcutDef incoming, int? index)
    {
        var clean = Clean(incoming);
        if (clean is null) return null;
        if (Locate(clean.Id) is { } at)
        {
            at.Tab.Items[at.Index] = clean;
        }
        else
        {
            var tab = Tab(tabId);
            if (tab is null || tab.Items.Count >= MaxItems) return null;
            if (!IdShape.IsMatch(clean.Id) || Find(clean.Id) is not null) clean.Id = NewId();
            tab.Items.Insert(Math.Clamp(index ?? tab.Items.Count, 0, tab.Items.Count), clean);
        }
        Commit();
        return clean;
    }

    public bool Remove(string id)
    {
        if (Locate(id) is not { } at) return false;
        at.Tab.Items.RemoveAt(at.Index);
        Commit();
        return true;
    }

    /// <summary>Put a shortcut at <paramref name="index"/> of a tab: the same tab reorders it,
    /// another carries it across (refused when that one is full).</summary>
    public bool Move(string id, string tabId, int index)
    {
        if (Locate(id) is not { } at || Tab(tabId) is not { } to) return false;
        if (to != at.Tab && to.Items.Count >= MaxItems) return false;
        var item = at.Tab.Items[at.Index];
        at.Tab.Items.RemoveAt(at.Index);
        to.Items.Insert(Math.Clamp(index, 0, to.Items.Count), item);
        Commit();
        return true;
    }

    public string? AddTab(string? id, string name, int? index)
    {
        if (_file.Tabs.Count >= MaxTabs) return null;
        name = CleanText(name, MaxTabName);
        if (name.Length == 0) name = "Tab " + (_file.Tabs.Count + 1);
        if (id is null || !IdShape.IsMatch(id) || Tab(id) is not null) id = NewId();
        _file.Tabs.Insert(Math.Clamp(index ?? _file.Tabs.Count, 0, _file.Tabs.Count), new ShortcutTab { Id = id, Name = name });
        Commit();
        return id;
    }

    public bool RenameTab(string id, string name)
    {
        name = CleanText(name, MaxTabName);
        if (Tab(id) is not { } tab || name.Length == 0) return false;
        tab.Name = name;
        Commit();
        return true;
    }

    public bool MoveTab(string id, int index)
    {
        if (Tab(id) is not { } tab) return false;
        _file.Tabs.Remove(tab);
        _file.Tabs.Insert(Math.Clamp(index, 0, _file.Tabs.Count), tab);
        Commit();
        return true;
    }

    /// <summary>A tab and every shortcut on it. The last tab stays: a wheel needs a tab to add to.</summary>
    public bool RemoveTab(string id)
    {
        if (_file.Tabs.Count <= 1 || Tab(id) is not { } tab) return false;
        _file.Tabs.Remove(tab);
        Commit();
        return true;
    }

    public void Reset()
    {
        _file.Tabs = ShortcutCatalog.Defaults();
        Commit();
    }

    private static string NewId() => "s" + Guid.NewGuid().ToString("N")[..10];

    // ---- checking what arrives ----

    private List<ShortcutTab> CleanTabs(List<ShortcutTab>? tabs)
    {
        var ids = new HashSet<string>();
        var tabIds = new HashSet<string>();
        var list = new List<ShortcutTab>();
        foreach (var t in tabs ?? new())
        {
            if (t is null || list.Count >= MaxTabs) continue;
            var tab = new ShortcutTab { Id = t.Id ?? "", Name = CleanText(t.Name, MaxTabName) };
            if (!IdShape.IsMatch(tab.Id) || !tabIds.Add(tab.Id)) { tab.Id = NewId(); tabIds.Add(tab.Id); }
            if (tab.Name.Length == 0) tab.Name = "Tab " + (list.Count + 1);
            foreach (var raw in t.Items ?? new())
            {
                if (tab.Items.Count >= MaxItems) break;
                if (raw is null || Clean(raw) is not { } item) continue;
                if (!IdShape.IsMatch(item.Id) || !ids.Add(item.Id)) { item.Id = NewId(); ids.Add(item.Id); }
                tab.Items.Add(item);
            }
            list.Add(tab);
        }
        return list;
    }

    /// <summary>A shortcut as it may be stored, or null when nothing about it can be opened.</summary>
    internal static ShortcutDef? Clean(ShortcutDef d)
    {
        var kind = ShortcutKinds.All.FirstOrDefault(k => k == d.Kind);
        if (kind is null) return null;
        var target = (d.Target ?? "").Trim();
        if (target.Length == 0 || target.Length > MaxText || target.Any(char.IsControl)) return null;
        switch (kind)
        {
            case ShortcutKinds.Builtin:
                if (ShortcutCatalog.Find(target) is null) return null;
                break;
            case ShortcutKinds.Url:
                if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
                target = uri.AbsoluteUri;
                break;
            case ShortcutKinds.Program:
            case ShortcutKinds.Folder:
            case ShortcutKinds.File:
                if (!Path.IsPathFullyQualified(target)) return null;
                break;
        }
        var startIn = (d.StartIn ?? "").Trim();
        var exe = (d.Exe ?? "").Trim();
        return new ShortcutDef
        {
            Id = (d.Id ?? "").Trim(),
            Name = CleanText(d.Name, MaxName) is { Length: > 0 } n ? n : DefaultName(kind, target),
            Kind = kind,
            Target = target,
            // Only the kinds that take them: arguments to a website would never be read, and a
            // stale value is something a later change of kind could start to act on.
            Args = kind is ShortcutKinds.Program or ShortcutKinds.Command ? CleanText(d.Args, MaxText) is { Length: > 0 } a ? a : null : null,
            StartIn = kind is ShortcutKinds.Program or ShortcutKinds.Command && startIn.Length > 0 && Path.IsPathFullyQualified(startIn) ? startIn : null,
            Exe = kind == ShortcutKinds.App && exe.Length > 0 && Path.IsPathFullyQualified(exe) ? exe : null,
            Console = kind == ShortcutKinds.Command && d.Console == "hide" ? "hide" : null,
            Confirm = d.Confirm,
            IconFile = ShortcutIcons.IsCustomName(d.IconFile) ? d.IconFile : null,
            Button = ActionService.CleanButton(d.Button) is { } b && !ActionService.IsReservedButton(b) ? b : null,
        };
    }

    private static string CleanText(string? s, int max)
    {
        var t = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return t.Length <= max ? t : t[..max].TrimEnd();
    }

    private static string DefaultName(string kind, string target) => kind switch
    {
        ShortcutKinds.Builtin => ShortcutCatalog.Find(target)?.Name ?? target,
        ShortcutKinds.Url => Uri.TryCreate(target, UriKind.Absolute, out var u) ? u.Host : target,
        ShortcutKinds.Program or ShortcutKinds.File or ShortcutKinds.Folder => Path.GetFileNameWithoutExtension(target.TrimEnd('\\')) is { Length: > 0 } f ? f : target,
        _ => kind == ShortcutKinds.Command ? "Command" : target,
    };

    /// <summary>Asks before it runs: the power actions always, anything else when its switch says so.</summary>
    public static bool NeedsConfirm(ShortcutDef d) =>
        d.Confirm || (d.Kind == ShortcutKinds.Builtin && ShortcutCatalog.Find(d.Target)?.Confirm == true);

    // ---- what the page gets ----

    public object Payload() => new
    {
        tabs = _file.Tabs.Select(t => new
        {
            id = t.Id, name = t.Name,
            items = t.Items.Select(i =>
            {
                var look = Look(i);
                return new
                {
                    id = i.Id, name = i.Name, kind = i.Kind, target = i.Target, args = i.Args, startIn = i.StartIn,
                    exe = i.Exe, console = i.Console, confirm = i.Confirm, button = i.Button,
                    iconFile = i.IconFile, customIcon = i.IconFile is null ? null : ShortcutIcons.DataUri(i.IconFile),
                    autoIcon = look?.Icon, missing = look?.Missing ?? false,
                };
            }),
        }),
        catalog = ShortcutCatalog.All.Select(b => new { id = b.Id, name = b.Name, icon = b.Icon, group = b.Group, desc = b.Desc, confirm = b.Confirm, danger = b.Danger }),
        knownFolders = KnownFolders(),
        maxItems = MaxItems,
        maxTabs = MaxTabs,
    };

    private (string? Icon, bool Missing)? Look(ShortcutDef d)
    {
        var key = LookKey(d);
        if (key is null) return null;
        lock (_gate) return _looks.TryGetValue(key, out var l) ? l : null;
    }

    /// <summary>What an icon is found for: null for the kinds the page draws itself (a built-in
    /// has a glyph, a game has its art). A website's is its site's, so one fetch serves every
    /// shortcut to the same site.</summary>
    private static string? LookKey(ShortcutDef d) => d.Kind switch
    {
        ShortcutKinds.App => "app|" + d.Target + "|" + d.Exe,
        ShortcutKinds.Program or ShortcutKinds.Folder or ShortcutKinds.File => d.Kind + "|" + d.Target,
        ShortcutKinds.Command when ScriptPath(d.Target) is not null => "command|" + d.Target,
        ShortcutKinds.Url when UrlHost(d.Target) is { } host => "url|" + host,
        _ => null,
    };

    /// <summary>Find the icons not looked up yet, on a shell thread, then say so. One pass at a time.</summary>
    public void ResolveLooks()
    {
        List<(string Key, ShortcutDef Def)> todo;
        lock (_gate)
        {
            if (_resolving) return;
            // Websites last: theirs are fetched over the network, everything else is read off
            // the disk, and a slow site should not hold up the rest.
            todo = _file.Tabs.SelectMany(t => t.Items)
                .Select(d => (Key: LookKey(d), Def: d.Clone()))
                .Where(x => x.Key is not null && !_looks.ContainsKey(x.Key))
                .Select(x => (x.Key!, x.Def)).DistinctBy(x => x.Item1)
                .OrderBy(x => x.Def.Kind == ShortcutKinds.Url).ToList();
            if (todo.Count == 0) return;
            _resolving = true;
        }
        _ = ShellApps.Sta(() =>
        {
            foreach (var (key, d) in todo)
            {
                var look = LookUp(d);
                lock (_gate) _looks[key] = look;
            }
            return true;
        }).ContinueWith(t =>
        {
            lock (_gate) _resolving = false;
            if (t.IsFaulted) Log.Info($"Shortcuts: icons failed: {t.Exception?.GetBaseException().Message}");
            LooksChanged?.Invoke();
        });
    }

    private static (string? Icon, bool Missing) LookUp(ShortcutDef d)
    {
        try
        {
            switch (d.Kind)
            {
                case ShortcutKinds.App:
                    if (d.Exe is not null && !File.Exists(d.Exe)) return (null, true);
                    return (ShellApps.Icon(@"shell:AppsFolder\" + d.Target) ?? (d.Exe is null ? null : ShellApps.Icon(d.Exe)), false);
                case ShortcutKinds.Folder:
                    return Directory.Exists(d.Target) ? (ShellApps.Icon(d.Target), false) : (null, true);
                case ShortcutKinds.Program:
                case ShortcutKinds.File:
                    return File.Exists(d.Target) ? (ShellApps.Icon(d.Target), false) : (null, true);
                case ShortcutKinds.Command:
                    var script = ScriptPath(d.Target);
                    return script is not null && File.Exists(script) ? (ShellApps.Icon(script), false) : (null, script is not null);
                case ShortcutKinds.Url:
                {
                    // Kept on disk once fetched, so a site is asked once, not on every start. One
                    // that has nothing usable is asked again next time Loungepad starts.
                    var name = ShortcutIcons.FaviconName(UrlHost(d.Target)!);
                    if (!ShortcutIcons.Exists(name))
                    {
                        if (ShortcutIcons.FetchFavicon(d.Target) is not { } png) return (null, false);
                        ShortcutIcons.Save(name, png);
                    }
                    return (ShortcutIcons.DataUri(name), false);
                }
            }
        }
        catch (Exception ex) { Log.Info($"Shortcuts: icon for {d.Name} failed: {ex.Message}"); }
        return (null, false);
    }

    /// <summary>The Start menu's apps for the page's picker, then their icons in a second message
    /// (a few hundred icons are a few hundred milliseconds and most of a megabyte).</summary>
    public static Task<List<ShellApps.StartApp>> ListAppsAsync() => ShellApps.Sta(ShellApps.List);

    public static Task<Dictionary<string, string>> AppIconsAsync(IEnumerable<string> ids) => ShellApps.Sta(() =>
    {
        var icons = new Dictionary<string, string>();
        foreach (var id in ids)
            if (ShellApps.Icon(@"shell:AppsFolder\" + id, 48) is { } icon) icons[id] = icon;
        return icons;
    });

    // ---- starting one ----

    public sealed class Started
    {
        public bool Ok { get; init; }
        public string? Error { get; init; }
        /// <summary>The process that was started, when Windows said which (0 through the shell).</summary>
        public uint Pid { get; init; }
        /// <summary>How to find the window it opens; null when it opens none (lock, a hidden command).</summary>
        public WindowService.ShortcutWindow? Window { get; init; }
    }

    private static Started Fail(string why) => new() { Ok = false, Error = why };

    /// <summary>
    /// Start what a shortcut names. Games, rest and sleep are not here: those are the library's and
    /// rest mode's, and the caller hands them over before it gets this far. Never asks a question;
    /// a shortcut that needs one has been confirmed by the time it reaches this.
    /// </summary>
    public async Task<Started> StartAsync(ShortcutDef d)
    {
        var since = DateTime.UtcNow.AddSeconds(-1);
        try
        {
            switch (d.Kind)
            {
                case ShortcutKinds.Builtin: return StartBuiltin(d.Target);

                case ShortcutKinds.App:
                {
                    if (ShellApps.IsPackagedId(d.Target))
                    {
                        var pid = await ShellApps.Sta(() => ShellApps.Activate(d.Target));
                        if (pid != 0)
                        {
                            var w = new WindowService.ShortcutWindow { Pid = pid, FreshFrame = true, StartedAfter = since };
                            if (ExeOfPid(pid) is { } exe) w.Exes.Add(exe);
                            return new Started { Ok = true, Pid = pid, Window = w };
                        }
                    }
                    if (d.Exe is not null && !File.Exists(d.Exe)) return Fail($"{d.Name} is no longer on this PC");
                    using (Process.Start(new ProcessStartInfo(@"shell:AppsFolder\" + d.Target) { UseShellExecute = true })) { }
                    var win = new WindowService.ShortcutWindow { FreshFrame = true, StartedAfter = since };
                    if (d.Exe is not null) win.Exes.Add(ActionService.ExeName(d.Exe));
                    return new Started { Ok = true, Window = win };
                }

                case ShortcutKinds.Program:
                {
                    if (!File.Exists(d.Target)) return Fail($"{Path.GetFileName(d.Target)} is not there any more");
                    var p = Process.Start(new ProcessStartInfo(d.Target)
                    {
                        UseShellExecute = true,
                        Arguments = d.Args ?? "",
                        WorkingDirectory = d.StartIn ?? Path.GetDirectoryName(d.Target) ?? "",
                    });
                    using (p) return Opened(p, since, ActionService.ExeName(d.Target));
                }

                case ShortcutKinds.Folder:
                {
                    if (!Directory.Exists(d.Target)) return Fail("That folder is not there any more");
                    using (Process.Start(new ProcessStartInfo("explorer.exe", Quote(d.Target)) { UseShellExecute = true })) { }
                    // Windows 11 may open it as a tab of a window already up, so an existing
                    // Explorer window is the answer after a moment when no new one appears.
                    var w = new WindowService.ShortcutWindow { Class = "CabinetWClass" };
                    w.Exes.Add("explorer");
                    return new Started { Ok = true, Window = w };
                }

                case ShortcutKinds.File:
                {
                    if (!File.Exists(d.Target)) return Fail($"{Path.GetFileName(d.Target)} is not there any more");
                    var p = Process.Start(new ProcessStartInfo(d.Target) { UseShellExecute = true });
                    var app = ShellApps.AssociatedExe(Path.GetExtension(d.Target));
                    using (p) return Opened(p, since, app is null ? null : ActionService.ExeName(app));
                }

                case ShortcutKinds.Url:
                {
                    using (Process.Start(new ProcessStartInfo(d.Target) { UseShellExecute = true })) { }
                    var w = new WindowService.ShortcutWindow { StartedAfter = since };
                    if (ShellApps.AssociatedExe("http") is { } browser) w.Exes.Add(ActionService.ExeName(browser));
                    return new Started { Ok = true, Window = w };
                }

                case ShortcutKinds.Command: return StartCommand(d, since);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Fail("Windows asked for permission and it was declined");
        }
        catch (Exception ex)
        {
            Log.Info($"Shortcut {d.Name} ({d.Kind}) failed: {ex.Message}");
            return Fail($"{d.Name} could not be opened");
        }
        return Fail($"{d.Name} could not be opened");
    }

    private static Started Opened(Process? p, DateTime since, string? exe)
    {
        var w = new WindowService.ShortcutWindow { Pid = p is null ? 0 : (uint)SafeId(p), StartedAfter = since };
        if (exe is not null) w.Exes.Add(exe);
        return new Started { Ok = true, Pid = w.Pid, Window = w };
    }

    private static int SafeId(Process p) { try { return p.Id; } catch { return 0; } }

    private static string? ExeOfPid(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant(); }
        catch { return null; }
    }

    private static Started StartBuiltin(string id)
    {
        var b = ShortcutCatalog.Find(id);
        if (b is null) return Fail("That shortcut is not one Loungepad knows");
        switch (id)
        {
            case "lock":
                using (Process.Start("rundll32.exe", "user32.dll,LockWorkStation")) { }
                return new Started { Ok = true };
            // Windows' own command for each, which asks every program to close the way the Start
            // menu does. No /f: a program with unsaved work is given the chance to say so.
            case "signOut": return Shutdown("/l");
            case "restart": return Shutdown("/r /t 0");
            case "shutdown": return Shutdown("/s /t 0");
        }
        if (b.Uri is null) return Fail("That shortcut is not one Loungepad knows");
        var p = Process.Start(new ProcessStartInfo(b.Uri) { UseShellExecute = true });
        using (p)
        {
            // Settings' frame belongs to ApplicationFrameHost until the app inside it has attached,
            // so a frame that has only just appeared counts as Settings. Explorer opens a new
            // window every time, and its desktop and taskbar are explorer.exe as well.
            var w = id == "explorer"
                ? new WindowService.ShortcutWindow { AllowExisting = false, Class = "CabinetWClass" }
                : new WindowService.ShortcutWindow { FreshFrame = b.Match.Contains("systemsettings") };
            foreach (var m in b.Match) w.Exes.Add(m);
            return new Started { Ok = true, Pid = p is null ? 0 : (uint)SafeId(p), Window = w };
        }
    }

    private static Started Shutdown(string args)
    {
        using (Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true })) { }
        return new Started { Ok = true };
    }

    /// <summary>A script file the command names on its own: .bat, .cmd or .ps1, quoted or not.</summary>
    private static string? ScriptPath(string target)
    {
        var t = target.Trim().Trim('"');
        var ext = Path.GetExtension(t).ToLowerInvariant();
        return ext is ".bat" or ".cmd" or ".ps1" or ".exe" && Path.IsPathFullyQualified(t) ? t : null;
    }

    /// <summary>
    /// A command or script. A .ps1 goes through PowerShell with the execution policy set aside for
    /// that one run -- Windows' default policy refuses every script, and this one was picked by
    /// hand, by the person whose PC it is -- a .bat or .cmd through cmd, an .exe as itself, and
    /// anything else is a command line for cmd. "Hide" gives it no console at all.
    /// </summary>
    private static Started StartCommand(ShortcutDef d, DateTime since)
    {
        var script = ScriptPath(d.Target);
        if (script is not null && !File.Exists(script)) return Fail($"{Path.GetFileName(script)} is not there any more");
        var args = d.Args ?? "";
        string exe, line;
        var ext = script is null ? "" : Path.GetExtension(script).ToLowerInvariant();
        switch (ext)
        {
            case ".ps1": exe = "powershell.exe"; line = $"-NoProfile -ExecutionPolicy Bypass -File {Quote(script!)} {args}".TrimEnd(); break;
            // cmd strips one pair of outer quotes from what follows /c, so the quoted path and its
            // arguments go inside another pair.
            case ".bat": case ".cmd": exe = "cmd.exe"; line = "/c \"" + Quote(script!) + (args.Length > 0 ? " " + args : "") + "\""; break;
            case ".exe": exe = script!; line = args; break;
            default: exe = "cmd.exe"; line = $"/c {d.Target} {args}".TrimEnd(); break;
        }
        bool hide = d.Console == "hide";
        var dir = d.StartIn ?? (script is not null ? Path.GetDirectoryName(script) : null)
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var p = Process.Start(new ProcessStartInfo(exe, line)
        {
            UseShellExecute = !hide,
            CreateNoWindow = hide,
            WorkingDirectory = dir,
        });
        Log.Info($"Shortcut {d.Name}: started {Path.GetFileName(exe)}{(hide ? " with no console" : "")}");
        using (p)
        {
            uint pid = p is null ? 0 : (uint)SafeId(p);
            if (hide) return new Started { Ok = true, Pid = pid };
            // A console is conhost's or Windows Terminal's window rather than the script's own, and
            // always a new one; an exe is whatever it opens.
            var w = new WindowService.ShortcutWindow { Pid = pid, StartedAfter = since, AllowExisting = ext == ".exe" };
            if (ext == ".exe") w.Exes.Add(ActionService.ExeName(exe));
            else foreach (var host in ConsoleHosts) w.Exes.Add(host);
            return new Started { Ok = true, Pid = pid, Window = w };
        }
    }

    private static readonly string[] ConsoleHosts = { "cmd", "powershell", "pwsh", "conhost", "openconsole", "windowsterminal" };

    private static string Quote(string path) => "\"" + path.Trim('"') + "\"";

    // ---- the folders worth a shortcut without browsing ----

    private static object[]? _known;
    private static object[] KnownFolders() => _known ??= new (string Id, string Name, Func<string?> Path)[]
    {
        ("downloads", "Downloads", () => KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B"))),
        ("documents", "Documents", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        ("desktop", "Desktop", () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        ("pictures", "Pictures", () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
        ("videos", "Videos", () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        ("music", "Music", () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
    }.Select(f => (f.Id, f.Name, Path: f.Path()))
     .Where(f => !string.IsNullOrEmpty(f.Path))
     .Select(f => (object)new { id = f.Id, name = f.Name, path = f.Path })
     .ToArray();

    private static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
