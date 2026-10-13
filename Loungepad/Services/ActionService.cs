using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Loungepad.Interop;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// The actions: what actions.json holds merged over the packs, which of the packs' programs are
/// on this PC, the window in front resolved to one of them, the button bindings the gamepad
/// loop asks about on every poll, and the sending of a shortcut.
///
/// Two threads touch it. The UI thread edits (the bridge's action* commands) and the pad thread
/// reads (<see cref="Evaluate"/>, 125 times a second), so every edit rebuilds an immutable
/// <see cref="Snapshot"/> and swaps it in; the pad thread never sees a list mid-edit.
/// </summary>
internal class ActionService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private const int MaxTabName = 24;
    private readonly string _path;

    /// <summary>The file: only what differs from the packs, plus the user's own apps.</summary>
    private ActionsFileData _file = new();
    /// <summary>Which pack programs detection found, by app id, with the exe it found.</summary>
    private Dictionary<string, string> _found = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private volatile Snapshot _snapshot = new(Array.Empty<ActionApp>());
    private readonly Func<IReadOnlyList<EmulatorDef>> _emulators;

    /// <summary>What sends the keys. Replaceable so a harness can record instead of typing into
    /// whatever is in front.</summary>
    public Action<string> SendKeys = keys => ShortcutKeys.Send(keys);

    /// <param name="path">actions.json; another file for a harness, so it never touches the real one.</param>
    public ActionService(Func<IReadOnlyList<EmulatorDef>> emulators, string? path = null)
    {
        _emulators = emulators;
        _path = path ?? Path.Combine(Paths.DataDir, "actions.json");
    }

    /// <summary>The Power Wheel shortcuts that have a button (ShortcutService.Bindings). They answer
    /// in every app, as Everywhere's actions do, and rank just under them.</summary>
    private IReadOnlyList<(string Combo, string Id, string Name)> _shortcutBindings = Array.Empty<(string, string, string)>();

    /// <summary>A shortcut's button was pressed, with its id. Raised on the pad thread.</summary>
    public event Action<string>? ShortcutFired;

    public void SetShortcutBindings(IEnumerable<(string Combo, string Id, string Name)> bindings)
    {
        _shortcutBindings = bindings.ToList();
        Rebuild();
    }

    // ---- the file ----

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                _file = JsonSerializer.Deserialize<ActionsFileData>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Info($"Actions load failed, starting from the packs: {ex.Message}");
            _file = new();
        }
        _file.Apps ??= new();
        _file.Apps.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Id));
        bool migrated = false;
        foreach (var app in _file.Apps)
        {
            app.Exes ??= new();
            app.Actions ??= new();
            app.Removed ??= new();
            app.Actions.RemoveAll(a => a is null || string.IsNullOrWhiteSpace(a.Id));
            migrated |= Migrate(app);
        }
        if (_file.Version < 2) { _file.Version = 2; migrated = true; }
        Rebuild();
        if (migrated) Save();
    }

    /// <summary>
    /// Version 1 could keep an action off the wheel with a flag. There is no such thing any more
    /// (the user's call, Oct 13 2026: tabs do the job): a pack's action that was kept off is taken
    /// off instead, so it is offered when an action is added, unless it has a button, which has to
    /// go on working and so stays on the wheel. The user's own actions all stay on.
    /// </summary>
    private static bool Migrate(ActionAppFile app)
    {
        bool changed = false;
        var pack = ActionPacks.Find(app.Id);
        foreach (var a in app.Actions.ToList())
        {
            if (a.Hidden is null) continue;
            changed = true;
            bool hidden = a.Hidden == true;
            a.Hidden = null;
            if (!hidden || !string.IsNullOrEmpty(a.Button) || pack?.Actions.Any(d => d.Id == a.Id) != true) continue;
            app.Actions.Remove(a);
            if (!app.Removed.Contains(a.Id)) app.Removed.Add(a.Id);
        }
        return changed;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_file, JsonOpts));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) { Log.Info($"Actions save failed: {ex.Message}"); }
    }

    // ---- the resolved list ----

    /// <summary>Every app as the page and the pad see it: packs merged with the file, custom apps
    /// after. <see cref="Installed"/> says whether it belongs in the Settings grid.</summary>
    public IReadOnlyList<ActionApp> Apps => _snapshot.Apps;

    public ActionApp? Find(string id) => _snapshot.Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>A pack the user has not touched has nothing in the file but, at most, its pin.</summary>
    public bool Modified(string id) => Entry(id) is { Untouched: false };

    /// <summary>Shown in Settings: Everywhere always, a pack when its program was found or the user
    /// added it by hand, and every custom app.</summary>
    public bool Installed(ActionApp app) =>
        app.Id == ActionPacks.EverywhereId || app.Custom || app.Pinned || _found.ContainsKey(app.Id);

    public string? Icon(ActionApp app)
    {
        lock (_gate) return _icons.TryGetValue(app.Id, out var i) ? i : null;
    }

    /// <summary>The app an exe name belongs to, or null. Everywhere is never the answer here.</summary>
    public ActionApp? ForExe(string? exe) => _snapshot.ForExe(exe);

    private ActionAppFile? Entry(string id) =>
        _file.Apps.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private void Rebuild()
    {
        var list = new List<ActionApp>();
        foreach (var pack in ActionPacks.All) list.Add(Resolve(pack, Entry(pack.Id)));
        foreach (var mine in _file.Apps)
            if (ActionPacks.Find(mine.Id) is null) list.Add(Resolve(null, mine));
        _snapshot = new Snapshot(list, _shortcutBindings);
    }

    /// <summary>
    /// A pack with the file's difference applied, or a custom app from the file alone. The actions
    /// are the pack's, each replaced by the file's copy when there is one, then the user's own; the
    /// tabs are the user's arrangement when there is one, else the pack's. An action on no tab and
    /// not taken off -- a default newer than the arrangement, or the user's own from a file older
    /// than tabs -- goes on the tab the pack has it on, else the last tab with room, else a new one.
    /// </summary>
    internal static ActionApp Resolve(ActionApp? pack, ActionAppFile? mine)
    {
        var app = pack is not null
            ? new ActionApp { Id = pack.Id, Name = pack.Name, Exes = new List<string>(pack.Exes), Pinned = mine?.Pinned == true }
            : new ActionApp { Id = mine!.Id, Name = mine.Name, Exes = new List<string>(mine.Exes), Custom = true };
        if (!string.IsNullOrEmpty(mine?.Path)) app.Path = mine.Path;

        var order = new List<string>();
        var actions = new Dictionary<string, ActionDef>();
        var packTab = new Dictionary<string, string>();
        foreach (var tab in pack?.Tabs ?? new())
            foreach (var d in tab.Actions)
            {
                var m = mine?.Actions.FirstOrDefault(x => x.Id == d.Id);
                var c = (m ?? d).Clone();
                c.Custom = false;
                c.Danger = d.Danger;
                if (string.IsNullOrEmpty(c.Desc)) c.Desc = d.Desc;
                c.Edited = m is not null && !SameAs(m, d);
                packTab[d.Id] = tab.Id;
                actions[d.Id] = c;
                order.Add(d.Id);
            }
        foreach (var m in mine?.Actions ?? new())
        {
            if (actions.ContainsKey(m.Id)) continue;
            var c = m.Clone();
            c.Custom = true;
            c.Edited = false;
            actions[m.Id] = c;
            order.Add(m.Id);
        }
        var removed = new HashSet<string>((mine?.Removed ?? new()).Where(packTab.ContainsKey));

        var layout = mine?.Tabs ?? Layout(pack?.Tabs ?? new());
        var placed = new HashSet<string>();
        var tabIds = new HashSet<string>();
        foreach (var lt in layout.Take(ActionPacks.MaxTabs))
        {
            if (lt is null || !tabIds.Add(lt.Id ?? "")) continue;
            var tab = new ActionTab { Id = lt.Id ?? "", Name = string.IsNullOrWhiteSpace(lt.Name) ? "Tab " + (app.Tabs.Count + 1) : lt.Name };
            foreach (var id in lt.Items ?? new())
                if (tab.Actions.Count < ActionPacks.MaxItems && !removed.Contains(id) && actions.TryGetValue(id, out var a) && placed.Add(id))
                    tab.Actions.Add(a);
            app.Tabs.Add(tab);
        }
        foreach (var id in order)
        {
            if (placed.Contains(id) || removed.Contains(id)) continue;
            var home = packTab.TryGetValue(id, out var pt) ? app.Tabs.FirstOrDefault(t => t.Id == pt && t.Actions.Count < ActionPacks.MaxItems) : null;
            home ??= app.Tabs.LastOrDefault(t => t.Actions.Count < ActionPacks.MaxItems);
            if (home is null)
            {
                home = new ActionTab { Id = NewTabId(app.Tabs.Select(t => t.Id)), Name = app.Tabs.Count == 0 ? "Actions" : "More" };
                app.Tabs.Add(home);
            }
            home.Actions.Add(actions[id]);
            placed.Add(id);
        }
        // Always a tab to add to: a custom app starts with nothing on it.
        if (app.Tabs.Count == 0) app.Tabs.Add(new ActionTab { Id = "main", Name = "Actions" });
        app.Spare = order.Where(removed.Contains).Select(id => actions[id]).ToList();
        return app;
    }

    private static List<ActionTabLayout> Layout(IEnumerable<ActionTab> tabs) =>
        tabs.Select(t => new ActionTabLayout { Id = t.Id, Name = t.Name, Items = t.Actions.Select(a => a.Id).ToList() }).ToList();

    /// <summary>Whether a copy of a pack's action (a) says what the pack's (b) does. A copy with no
    /// description has the pack's.</summary>
    private static bool SameAs(ActionDef a, ActionDef b) =>
        a.Name == b.Name && a.Keys == b.Keys && (a.Button ?? "") == (b.Button ?? "")
        && (string.IsNullOrEmpty(a.Desc) || a.Desc == b.Desc);

    // ---- edits, from the bridge ----

    private ActionAppFile FileEntry(string id)
    {
        var e = Entry(id);
        if (e is not null) return e;
        var pack = ActionPacks.Find(id) ?? throw new InvalidOperationException($"No such app: {id}");
        e = new ActionAppFile { Id = pack.Id, Name = pack.Name, Exes = new List<string>(pack.Exes) };
        _file.Apps.Add(e);
        return e;
    }

    /// <summary>The app's entry with its tabs written out exactly as they are drawn now: an edit
    /// that arranges anything starts from what the user is looking at, new defaults included.</summary>
    private ActionAppFile Arrange(ActionApp app)
    {
        var e = FileEntry(app.Id);
        e.Tabs = Layout(app.Tabs);
        return e;
    }

    /// <summary>A pack's entry that has gone back to the pack forgets its arrangement (so a better
    /// one in a later build reaches it), and one with nothing left in it goes.</summary>
    private void Tidy(ActionAppFile e)
    {
        var pack = ActionPacks.Find(e.Id);
        if (pack is null) return;
        if (e.Tabs is not null && e.Removed.Count == 0 && !e.Actions.Any(a => pack.Actions.All(d => d.Id != a.Id))
            && SameLayout(e.Tabs, Layout(pack.Tabs)))
            e.Tabs = null;
        if (e.Untouched && !e.Pinned && string.IsNullOrEmpty(e.Path)) _file.Apps.Remove(e);
    }

    private static bool SameLayout(List<ActionTabLayout> a, List<ActionTabLayout> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && p.First.Items.SequenceEqual(p.Second.Items));

    private void Commit(ActionAppFile? e = null)
    {
        if (e is not null) Tidy(e);
        Save();
        Rebuild();
    }

    private static bool ValidId(string id) =>
        id.Length is > 0 and <= 40 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static string NewTabId(IEnumerable<string> taken)
    {
        var have = new HashSet<string>(taken);
        for (int n = 1; ; n++)
        {
            var id = n == 1 ? "more" : "more-" + n;
            if (!have.Contains(id)) return id;
        }
    }

    /// <summary>
    /// Add or change one action. One not on the wheel -- the user's own, new, or a pack's taken off
    /// earlier and put back -- goes on <paramref name="tabId"/> at <paramref name="index"/> (the
    /// last tab, the end, when null). Returns what was stored, or null when refused: no such app, a
    /// malformed id, or a full tab.
    /// </summary>
    public ActionDef? Update(string appId, ActionDef incoming, string? tabId = null, int? index = null)
    {
        var app = Find(appId);
        if (app is null) return null;
        var id = (incoming.Id ?? "").Trim();
        if (!ValidId(id)) return null;
        var clean = new ActionDef
        {
            Id = id,
            Name = Truncate((incoming.Name ?? "").Trim(), 48),
            Keys = ShortcutKeys.IsValid(incoming.Keys ?? "") ? Normalize(incoming.Keys!) : "",
            Button = CleanButton(incoming.Button),
            Desc = CleanDesc(incoming.Desc),
        };
        if (clean.Name.Length == 0) clean.Name = clean.Keys.Length > 0 ? clean.Keys : "Action";

        var packDef = ActionPacks.Find(app.Id)?.Actions.FirstOrDefault(a => a.Id == id);
        // The pack's own words are not kept as a change, so a better sentence in a later build reaches it.
        if (packDef is not null && clean.Desc == packDef.Desc) clean.Desc = null;
        ActionAppFile entry;
        if (app.Actions.Any(a => a.Id == id)) entry = FileEntry(app.Id);
        else
        {
            entry = Arrange(app);
            var tab = entry.Tabs!.FirstOrDefault(t => t.Id == tabId) ?? entry.Tabs!.LastOrDefault();
            if (tab is null || tab.Items.Count >= ActionPacks.MaxItems) return null;
            tab.Items.Insert(Math.Clamp(index ?? tab.Items.Count, 0, tab.Items.Count), id);
            entry.Removed.Remove(id);
        }
        // In place, so the order of a file older than tabs (which places the user's own in this
        // order) is kept; a pack's action changed back to exactly the pack's is no change at all.
        var i = entry.Actions.FindIndex(a => a.Id == id);
        bool keep = packDef is null || !SameAs(clean, packDef);
        if (i >= 0 && keep) entry.Actions[i] = clean;
        else if (i >= 0) entry.Actions.RemoveAt(i);
        else if (keep) entry.Actions.Add(clean);
        Commit(entry);
        return clean;
    }

    /// <summary>A pack's action back to the pack's own name, shortcut and button.</summary>
    public bool Restore(string appId, string actionId)
    {
        if (Entry(appId) is not { } e || ActionPacks.Find(e.Id)?.Actions.Any(a => a.Id == actionId) != true) return false;
        if (e.Actions.RemoveAll(a => a.Id == actionId) == 0) return false;
        Commit(e);
        return true;
    }

    /// <summary>Off the wheel. The user's own action is deleted; a pack's is only taken off, and
    /// is offered again when one is added (its changes are not kept, so it comes back as shipped).</summary>
    public bool Remove(string appId, string actionId)
    {
        var app = Find(appId);
        if (app is null || !app.Actions.Any(a => a.Id == actionId)) return false;
        var entry = FileEntry(app.Id);
        entry.Actions.RemoveAll(a => a.Id == actionId);
        if (ActionPacks.Find(app.Id)?.Actions.Any(a => a.Id == actionId) == true && !entry.Removed.Contains(actionId))
            entry.Removed.Add(actionId);
        if (entry.Tabs is not null) foreach (var t in entry.Tabs) t.Items.Remove(actionId);
        Commit(entry);
        return true;
    }

    /// <summary>Put an action at <paramref name="index"/> of a tab: the same tab reorders it,
    /// another carries it across (refused when that one is full).</summary>
    public bool Move(string appId, string actionId, string tabId, int index)
    {
        var app = Find(appId);
        if (app is null) return false;
        var entry = Arrange(app);
        var from = entry.Tabs!.FirstOrDefault(t => t.Items.Contains(actionId));
        var to = entry.Tabs!.FirstOrDefault(t => t.Id == tabId);
        if (from is null || to is null) return false;
        if (to != from && to.Items.Count >= ActionPacks.MaxItems) return false;
        from.Items.Remove(actionId);
        to.Items.Insert(Math.Clamp(index, 0, to.Items.Count), actionId);
        Commit(entry);
        return true;
    }

    public string? AddTab(string appId, string? id, string name, int? index)
    {
        var app = Find(appId);
        if (app is null || app.Tabs.Count >= ActionPacks.MaxTabs) return null;
        var entry = Arrange(app);
        name = Truncate(name.Trim(), MaxTabName);
        if (name.Length == 0) name = "Tab " + (entry.Tabs!.Count + 1);
        if (id is null || !ValidId(id) || entry.Tabs!.Any(t => t.Id == id)) id = NewTabId(entry.Tabs!.Select(t => t.Id));
        entry.Tabs!.Insert(Math.Clamp(index ?? entry.Tabs.Count, 0, entry.Tabs.Count), new ActionTabLayout { Id = id, Name = name });
        Commit(entry);
        return id;
    }

    public bool RenameTab(string appId, string tabId, string name)
    {
        var app = Find(appId);
        name = Truncate((name ?? "").Trim(), MaxTabName);
        if (app is null || name.Length == 0) return false;
        var entry = Arrange(app);
        if (entry.Tabs!.FirstOrDefault(t => t.Id == tabId) is not { } tab) return false;
        tab.Name = name;
        Commit(entry);
        return true;
    }

    public bool MoveTab(string appId, string tabId, int index)
    {
        var app = Find(appId);
        if (app is null) return false;
        var entry = Arrange(app);
        if (entry.Tabs!.FirstOrDefault(t => t.Id == tabId) is not { } tab) return false;
        entry.Tabs!.Remove(tab);
        entry.Tabs!.Insert(Math.Clamp(index, 0, entry.Tabs.Count), tab);
        Commit(entry);
        return true;
    }

    /// <summary>A tab and everything on it, as if each were removed. The last tab stays.</summary>
    public bool RemoveTab(string appId, string tabId)
    {
        var app = Find(appId);
        if (app is null || app.Tabs.Count <= 1) return false;
        var entry = Arrange(app);
        if (entry.Tabs!.FirstOrDefault(t => t.Id == tabId) is not { } tab) return false;
        var pack = ActionPacks.Find(app.Id);
        foreach (var id in tab.Items)
        {
            entry.Actions.RemoveAll(a => a.Id == id);
            if (pack?.Actions.Any(a => a.Id == id) == true && !entry.Removed.Contains(id)) entry.Removed.Add(id);
        }
        entry.Tabs!.Remove(tab);
        Commit(entry);
        return true;
    }

    /// <summary>Back to the pack: every change, arrangement and action of the user's goes. A pack
    /// added by hand stays in the grid. Only for packs; a custom app has nothing to go back to.</summary>
    public bool Reset(string appId)
    {
        if (ActionPacks.Find(appId) is null || Entry(appId) is not { } e || e.Untouched) return false;
        e.Actions.Clear();
        e.Tabs = null;
        e.Removed.Clear();
        Commit(e);
        return true;
    }

    /// <summary>
    /// Add a program: by its exe (from an open window) or its path (from the file dialog). An exe
    /// we have a pack for pins the pack; anything else becomes an empty custom app. Returns the
    /// app's id, and whether it was already there.
    /// </summary>
    public (string Id, bool Existed) AddApp(string exeOrPath, string? name)
    {
        var path = exeOrPath.Contains('\\') || exeOrPath.Contains('/') ? exeOrPath : null;
        var exe = ExeName(exeOrPath);
        if (exe.Length == 0) throw new ArgumentException("No program named");

        if (ActionPacks.ForExe(exe) is { } pack)
        {
            var existed = Installed(Find(pack.Id)!);
            var entry = FileEntry(pack.Id);
            entry.Pinned = true;
            if (path is not null) entry.Path = path;
            Commit();
            return (pack.Id, existed);
        }

        if (_snapshot.ForExe(exe) is { } have) return (have.Id, true);

        _file.Apps.Add(new ActionAppFile
        {
            Id = exe,
            Name = Truncate((name ?? "").Trim().Length > 0 ? name!.Trim() : PrettyName(path, exe), 48),
            Exes = new List<string> { exe },
            Path = path,
        });
        Commit();
        return (exe, false);
    }

    public bool RemoveApp(string appId)
    {
        var app = Find(appId);
        if (app is null || app.Id == ActionPacks.EverywhereId) return false;
        // A pack: only one added by hand, which goes back to the pack as it leaves the grid. One
        // found on the PC cannot be removed, only reset; the grid would put it straight back.
        if (!app.Custom && Entry(app.Id) is not { Pinned: true }) return false;
        var n = _file.Apps.RemoveAll(a => a.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase));
        if (n == 0) return false;
        if (app.Custom) lock (_gate) _icons.Remove(app.Id);
        Commit();
        return true;
    }

    public bool RenameApp(string appId, string name)
    {
        var app = Find(appId);
        if (app is null || !app.Custom || Entry(app.Id) is not { } entry) return false;
        name = Truncate(name.Trim(), 48);
        if (name.Length == 0) return false;
        entry.Name = name;
        Commit();
        return true;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>A description as it may be stored: one line, no control characters, at most 120
    /// characters (the wheel's centre shows two lines of about 40); null for none.</summary>
    private static string? CleanDesc(string? desc)
    {
        var s = new string((desc ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return s.Length == 0 ? null : Truncate(s, 120);
    }

    /// <summary>"Ctrl+Shift+T" spelt the way the page spells it, whatever was typed.</summary>
    private static string Normalize(string keys)
    {
        var parts = ShortcutKeys.Parts(keys);
        var order = new[] { "Ctrl", "Shift", "Alt", "Win" };
        var mods = order.Where(m => parts.Take(parts.Count - 1).Contains(m, StringComparer.OrdinalIgnoreCase));
        return string.Join("+", mods.Append(ShortcutKeys.CanonicalKey(parts[^1])));
    }

    /// <summary>A button combo in the launcher's own names and order, or null. Unknown names
    /// are dropped rather than kept as text that would never match a press.</summary>
    public static string? CleanButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button)) return null;
        var parts = button.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CanonButton).Where(p => p is not null).Select(p => p!).Distinct().ToList();
        if (parts.Count == 0) return null;
        return string.Join(" + ", ButtonOrder.Where(parts.Contains));
    }

    /// <summary>The order a combo is written in, so "Y + LB" and "LB + Y" are one binding.</summary>
    public static readonly string[] ButtonOrder =
        { "LT", "RT", "LB", "RB", "LS", "RS", "View", "Menu", "Guide", "A", "B", "X", "Y", "Up", "Down", "Left", "Right" };

    private static string? CanonButton(string p) => p.ToUpperInvariant() switch
    {
        "A" => "A", "B" => "B", "X" => "X", "Y" => "Y",
        "LB" => "LB", "RB" => "RB", "LT" => "LT", "RT" => "RT", "LS" => "LS", "RS" => "RS",
        "VIEW" or "BACK" => "View", "MENU" or "START" => "Menu", "GUIDE" or "XBOX" or "PS" => "Guide",
        "UP" => "Up", "DOWN" => "Down", "LEFT" => "Left", "RIGHT" => "Right",
        _ => null,
    };

    /// <summary>Bindings that can never fire: the click buttons on their own, and the Guide button,
    /// which Windows keeps. The page refuses them too; this is for a hand-edited file.</summary>
    public static bool IsReservedButton(string? combo) => combo is "A" or "B" or "Guide";

    // ---- what is on this PC ----

    /// <summary>
    /// Look for each pack's program: App Paths, the uninstall entries, the places the odd one
    /// installs to, the Store's execution aliases, the library's own emulators, and whatever is
    /// running right now. Then an icon for everything found or added. Off the UI thread: the
    /// registry walk is quick, the icons are a few milliseconds each.
    /// </summary>
    public void Detect()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Consider(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var exe = ExeName(path);
            if (ActionPacks.ForExe(exe) is not { } pack || found.ContainsKey(pack.Id)) return;
            if (!SafeExists(path)) return;
            found[pack.Id] = path;
        }

        foreach (var pack in ActionPacks.All)
        {
            foreach (var exe in pack.Exes)
            {
                Consider(AppPath(exe));
                Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe + ".exe"));
            }
        }
        Consider(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        foreach (var candidate in KnownPaths()) Consider(candidate);
        try { foreach (var exe in EmulatorDetection.RegistryExes()) Consider(exe); }
        catch (Exception ex) { Log.Info($"Actions: uninstall entries unreadable: {ex.Message}"); }
        try { foreach (var emu in _emulators()) Consider(emu.ExePath); }
        catch { /* the library is the UI thread's; a torn read here only costs a detection */ }
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ActionPacks.ForExe(p.ProcessName) is { } pack && !found.ContainsKey(pack.Id))
                        found[pack.Id] = ProcessPath((uint)p.Id) ?? "";
                }
                finally { p.Dispose(); }
            }
        }
        catch { /* process list unavailable */ }

        _found = found;

        // Icons: for every app that will be in the grid and has a program we can point at.
        foreach (var app in _snapshot.Apps)
        {
            if (app.Id == ActionPacks.EverywhereId) continue;
            var path = app.Path;
            if (string.IsNullOrEmpty(path) && found.TryGetValue(app.Id, out var f)) path = f;
            if (string.IsNullOrEmpty(path) && app.Custom) path = RunningPath(app.Exes);
            bool have;
            lock (_gate) have = _icons.ContainsKey(app.Id);
            if (have || string.IsNullOrEmpty(path)) continue;
            var icon = ExtractIcon(path);
            lock (_gate) _icons[app.Id] = icon;
        }
        Log.Info($"Actions: {found.Count} of {ActionPacks.All.Count - 1} pack programs found");
    }

    /// <summary>HKLM/HKCU App Paths: where most installers register their exe by name.</summary>
    private static string? AppPath(string exe)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" })
            {
                try
                {
                    using var key = hive.OpenSubKey(sub + exe + ".exe");
                    if (key?.GetValue(null) is string path && path.Length > 0)
                        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
                }
                catch { /* one unreadable key */ }
            }
        }
        return null;
    }

    /// <summary>The installs that register nothing by exe name.</summary>
    private static IEnumerable<string> KnownPaths()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(roaming, "Spotify", "Spotify.exe");
        yield return Path.Combine(pf, "VideoLAN", "VLC", "vlc.exe");
        yield return Path.Combine(pf86, "VideoLAN", "VLC", "vlc.exe");
        yield return Path.Combine(pf, "Mozilla Firefox", "firefox.exe");
        yield return Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
        yield return Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
        yield return Path.Combine(local, "Programs", "Opera", "opera.exe");
        yield return Path.Combine(local, "Programs", "Opera GX", "opera.exe");
        yield return Path.Combine(local, "Vivaldi", "Application", "vivaldi.exe");
        // Discord keeps its exe under a versioned folder; the newest is the one that runs.
        string? discord = null;
        try
        {
            var root = Path.Combine(local, "Discord");
            if (Directory.Exists(root))
                discord = Directory.GetDirectories(root, "app-*").OrderByDescending(d => d).Select(d => Path.Combine(d, "Discord.exe")).FirstOrDefault(File.Exists);
        }
        catch { /* not there */ }
        if (discord is not null) yield return discord;
    }

    private static string? RunningPath(IEnumerable<string> exes)
    {
        try
        {
            foreach (var exe in exes)
            {
                foreach (var p in Process.GetProcessesByName(exe))
                {
                    try { return ProcessPath((uint)p.Id); }
                    finally { p.Dispose(); }
                }
            }
        }
        catch { /* nothing running */ }
        return null;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    /// <summary>"C:\...\firefox.exe" or "FIREFOX.EXE" or "firefox" → "firefox".</summary>
    public static string ExeName(string exeOrPath)
    {
        var name = exeOrPath.Trim().Trim('"');
        try { name = Path.GetFileName(name); } catch { /* keep as is */ }
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }

    private static string PrettyName(string? path, string exe)
    {
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var d = (info.FileDescription ?? "").Trim();
                if (d.Length > 0 && d.Length <= 48) return d;
            }
            catch { /* no version resource */ }
        }
        return exe.Length > 0 ? char.ToUpperInvariant(exe[0]) + exe[1..] : exe;
    }

    private static string? ExtractIcon(string path)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var n = NativeMethods.PrivateExtractIcons(path, 0, 96, 96, icons, ids, 1, 0);
            if (n == 0 || icons[0] == IntPtr.Zero)
            {
                n = NativeMethods.PrivateExtractIcons(path, 0, 48, 48, icons, ids, 1, 0);
                if (n == 0 || icons[0] == IntPtr.Zero) return null;
            }
            return WindowService.EncodeIcon(icons[0]);
        }
        catch (Exception ex)
        {
            Log.Info($"Actions: icon for {path} failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (icons[0] != IntPtr.Zero) NativeMethods.DestroyIcon(icons[0]);
        }
    }

    // ---- the window in front ----

    /// <summary>
    /// The exe name behind a window, as the packs name them. A Store app's top-level window is
    /// the frame host's; the app itself owns the CoreWindow inside it, and that is the process
    /// that matters.
    /// </summary>
    public static string ExeOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return "";
        var name = ProcessName(pid);
        if (name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            uint inner = 0;
            NativeMethods.EnumChildWindows(hwnd, (child, _) =>
            {
                var cls = new StringBuilder(64);
                NativeMethods.GetClassName(child, cls, cls.Capacity);
                if (cls.ToString() != "Windows.UI.Core.CoreWindow") return true;
                NativeMethods.GetWindowThreadProcessId(child, out inner);
                return inner == 0;
            }, IntPtr.Zero);
            if (inner != 0 && inner != pid) name = ProcessName(inner);
        }
        return name.ToLowerInvariant();
    }

    private static string ProcessName(uint pid)
    {
        var path = ProcessPath(pid);
        if (path is not null) return ExeName(path);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; }
    }

    private static string? ProcessPath(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString(0, (int)len) : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    // ---- bindings, on the pad thread ----

    /// <summary>
    /// How long a button waits when it is also the start of a longer binding -- LB alone when
    /// "LB + RB" exists too -- for the rest of the chord to land. Two thumbs on two shoulder
    /// buttons arrive a few polls apart. Let go sooner, the short binding fires on the release;
    /// a button that starts no longer binding fires on its press, as it always has.
    /// </summary>
    private const int ChordWindowMs = 100;

    /// <summary>The window in front and the program behind a window. Replaceable so a harness can
    /// fake the foreground changing under a held chord, which is what Win+D and Alt+Tab do.</summary>
    public Func<IntPtr> Foreground = NativeMethods.GetForegroundWindow;
    public Func<IntPtr, string> ExeOfWindow = ExeOf;

    private IntPtr _fgHwnd;
    private Snapshot? _fgSnapshot;
    private ActionApp? _fgApp;
    /// <summary>What was held at the last poll, as <see cref="GamepadService.HeldMask"/> writes it.</summary>
    private uint _prevHeld;
    /// <summary>The buttons of the last binding that fired, or of a press that was swallowed. No
    /// binding using any of them fires again until every one of them is let go.</summary>
    private uint _latched;
    /// <summary>A binding held back for <see cref="ChordWindowMs"/> because more buttons would make
    /// it a longer one.</summary>
    private Binding? _pending;
    private long _pendingSince;
    /// <summary>Set when the pad stops being a desktop device: whatever is still held when it comes
    /// back was pressed for something else, and is latched rather than fired.</summary>
    private bool _resync = true;

    /// <summary>
    /// Called once per poll while the pad is a desktop device: the launcher not in front, no
    /// focused game, the on-screen keyboard not driving. Fires the binding that the pad state has
    /// just completed and returns the mask of buttons it used (or is holding back), so the same
    /// press is not also a mouse click. <paramref name="spent"/> is what the loop already spent
    /// this poll (the keyboard toggle); <paramref name="reserved"/> the menu and screenshot combos,
    /// which always win; <paramref name="now"/> the loop's clock in milliseconds.
    ///
    /// A press fires one binding, once. The rules are all about buttons, never about which
    /// binding was satisfied: they used to be, keyed per app, and a foreground change forgot
    /// them -- so a chord whose shortcut moves the foreground (Win+D, Alt+Tab) fired again in
    /// the window it brought up, and again on the way back, for as long as it was held.
    /// </summary>
    public ushort Evaluate(in NativeMethods.XINPUT_GAMEPAD pad, ushort spent, IReadOnlyList<uint> reserved, long now)
    {
        var snap = _snapshot;
        uint held = GamepadService.HeldMask(pad);
        if (_resync)
        {
            _resync = false;
            _prevHeld = held;
            _latched = held;
            _pending = null;
        }

        var fg = Foreground();
        if (fg != _fgHwnd || !ReferenceEquals(snap, _fgSnapshot))
        {
            _fgHwnd = fg;
            _fgSnapshot = snap;
            _fgApp = snap.ForExe(ExeOfWindow(fg));
            // What fired stays fired across the change: it is still latched. A press still
            // waiting for the rest of its chord has not done anything yet, so it is judged
            // again, as if it had just landed, against the new window's bindings.
            if (_pending is not null) { _prevHeld &= ~_pending.Mask; _pending = null; }
        }

        uint pressed = held & ~_prevHeld;
        _prevHeld = held;
        if ((held & _latched) == 0) _latched = 0;

        var bindings = snap.BindingsFor(_fgApp);
        // A menu or screenshot combo held right now: whatever its press completed belongs to it.
        uint reservedHeld = 0;
        foreach (var r in reserved)
            if ((held & r) == r) reservedHeld |= r;

        // The binding this poll's presses completed: all of its buttons down, one of them new.
        // The longest one wins, so "LB + Y" beats the "Y" that completed it.
        Binding? best = null;
        foreach (var b in bindings)
        {
            if ((held & b.Mask) != b.Mask || (b.Mask & pressed) == 0) continue;
            if ((b.Mask & spent) != 0) continue;                    // the loop spent this press
            if (reserved.Any(r => r == b.Mask)) continue;
            if (best is null || b.Parts > best.Parts) best = b;
        }

        uint taken = 0;
        bool growing = false;
        if (_pending is { } p)
        {
            if ((p.Mask & reservedHeld) != 0)
            {
                // It was the start of the menu or screenshot combo.
                _latched |= p.Mask | reservedHeld;
                _pending = null;
                taken |= p.Mask;
            }
            else if (best is not null && (best.Mask & p.Mask) == p.Mask)
            {
                // The rest of the chord landed: the longer binding replaces the waiting one, and
                // waits on in turn (from the same start) if it is the start of a longer one still.
                _pending = null;
                growing = true;
            }
            else if ((held & p.Mask) != p.Mask || now - _pendingSince >= ChordWindowMs || best is not null)
            {
                // Let go (a tap), held past the window, or something else was pressed: no chord.
                _pending = null;
                taken |= Fire(p);
            }
            else taken |= p.Mask;   // still waiting
        }

        if (best is not null)
        {
            if ((best.Mask & (_latched | reservedHeld)) != 0)
            {
                // Completed while a chord that already fired is still held, or as part of the menu
                // or screenshot combo: swallowed, and its buttons wait to be let go like the rest.
                _latched |= best.Mask;
                taken |= best.Mask;
            }
            else if (_pending is null && CanGrow(best, bindings, reserved))
            {
                _pending = best;
                if (!growing) _pendingSince = now;
                taken |= best.Mask;
            }
            else taken |= Fire(best);
        }
        return (ushort)(taken & 0xFFFF);
    }

    /// <summary>Whether another binding, or the menu or screenshot combo, holds every button this
    /// one does and more, so pressing on could still turn it into that.</summary>
    private static bool CanGrow(Binding b, IEnumerable<Binding> bindings, IReadOnlyList<uint> reserved)
    {
        foreach (var o in bindings)
            if (o.Mask != b.Mask && (o.Mask & b.Mask) == b.Mask && !reserved.Any(r => r == o.Mask)) return true;
        foreach (var r in reserved)
            if (r != b.Mask && (r & b.Mask) == b.Mask) return true;
        return false;
    }

    private uint Fire(Binding b)
    {
        _latched |= b.Mask;
        try
        {
            if (b.Shortcut is { } id)
            {
                Log.Info($"Shortcut: {b.Combo} → {b.Name} in {(_fgApp?.Name ?? "no app")}");
                ShortcutFired?.Invoke(id);
            }
            else
            {
                SendKeys(b.Keys);
                Log.Info($"Action: {b.Combo} → {b.Keys} ({b.Name}) in {(_fgApp?.Name ?? "no app")}");
            }
        }
        catch (Exception ex) { Log.Info($"Action {b.Name} failed: {ex.Message}"); }
        return b.Mask;
    }

    /// <summary>Forget the held chords: called when the pad stops being a desktop device.</summary>
    public void ResetBindings()
    {
        _resync = true;
        _pending = null;
    }

    /// <param name="Shortcut">Set for a Power Wheel shortcut's button: it opens that shortcut
    /// rather than sending <paramref name="Keys"/>.</param>
    private sealed record Binding(string Combo, uint Mask, int Parts, string Keys, string Name, string? Shortcut = null);

    /// <summary>The resolved apps plus their bindings, built once per edit and read without locks.</summary>
    private sealed class Snapshot
    {
        public IReadOnlyList<ActionApp> Apps { get; }
        private readonly Dictionary<string, ActionApp> _byExe = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Binding[]> _bindings = new(StringComparer.OrdinalIgnoreCase);
        private readonly Binding[] _everywhere;

        public Snapshot(IReadOnlyList<ActionApp> apps, IReadOnlyList<(string Combo, string Id, string Name)>? shortcuts = null)
        {
            Apps = apps;
            foreach (var app in apps)
            {
                foreach (var exe in app.Exes) _byExe.TryAdd(exe, app);
                _bindings[app.Id] = app.Actions
                    .Where(a => !string.IsNullOrEmpty(a.Button) && !IsReservedButton(a.Button) && ShortcutKeys.IsValid(a.Keys))
                    .Select(a => (a, mask: GamepadService.ChordMask(a.Button)))
                    .Where(x => x.mask != 0)
                    .Select(x => new Binding(x.a.Button!, x.mask, BitOperations.PopCount(x.mask), x.a.Keys, x.a.Name))
                    .ToArray();
            }
            _everywhere = _bindings.TryGetValue(ActionPacks.EverywhereId, out var e) ? e : Array.Empty<Binding>();
            // The shortcuts' buttons ride with Everywhere's, under them: both pages refuse a button
            // the other already has, so a clash only comes from a hand-edited file, and then the
            // action keeps it.
            var mine = (shortcuts ?? Array.Empty<(string, string, string)>())
                .Where(s => !IsReservedButton(s.Combo))
                .Select(s => (s, mask: GamepadService.ChordMask(s.Combo)))
                .Where(x => x.mask != 0 && !_everywhere.Any(a => a.Combo == x.s.Combo))
                .Select(x => new Binding(x.s.Combo, x.mask, BitOperations.PopCount(x.mask), "", x.s.Name, x.s.Id))
                .GroupBy(b => b.Combo).Select(g => g.First());
            _everywhere = _everywhere.Concat(mine).ToArray();
        }

        public ActionApp? ForExe(string? exe) =>
            !string.IsNullOrEmpty(exe) && _byExe.TryGetValue(exe, out var app) ? app : null;

        /// <summary>The app's own bindings, then Everywhere's minus any combo the app already
        /// uses: the app's binding for a button wins over the general one.</summary>
        public IEnumerable<Binding> BindingsFor(ActionApp? app)
        {
            if (app is null || !_bindings.TryGetValue(app.Id, out var own)) return _everywhere;
            return own.Concat(_everywhere.Where(e => !own.Any(o => o.Combo == e.Combo)));
        }
    }
}
