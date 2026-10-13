using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>
/// Which processes are a game's, for one session, and which of those is its launcher.
///
/// A process is the game's when its exe is in the game's folder (the only rule there used to be),
/// when it is the process Loungepad started, or when one of those started it, at any depth. The
/// last is what follows a launcher to a game that runs from somewhere else: Prism Launcher starts
/// Minecraft as javaw.exe out of a Java runtime folder, and by the folder alone the launcher was
/// the game and Minecraft was not -- the stick was a mouse over Minecraft, on top of its own pad
/// support, and dead over the launcher where it was wanted (the user's report, Oct 13 2026).
///
/// A launcher is a program on <see cref="KnownLaunchers"/>, or one whose child has a window the
/// size of a game open right now. Only the joystick mouse asks (<see cref="IsGame"/>): over a
/// launcher the stick stays a mouse, because a launcher is a window somebody clicks Play in.
/// Closing, pausing, the TV and the stats take the whole tree.
///
/// Process ids are reused, so every link is checked against start times: a child must have
/// started after its parent, and before that parent was seen to exit.
/// </summary>
internal sealed class SessionProcesses
{
    /// <summary>
    /// Programs that start games and stay up beside them. A launcher found by the window rule is
    /// only one while its game's window is open; these are one from the moment they appear, which
    /// is what gives the stick a pointer over Prism's instance list before anything has started.
    /// Lower case, without ".exe".
    /// </summary>
    private static readonly HashSet<string> KnownLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        // Minecraft
        "prismlauncher", "multimc", "polymc", "gdlauncher", "curseforge", "modrinth app", "minecraftlauncher",
        // Per-game launchers that put up a window of their own before the game
        "redprelauncher", "redlauncher", "paradox launcher", "dowser",
        "skyrimselauncher", "skyrimlauncher", "fallout4launcher", "falloutlauncher", "falloutnvlauncher", "oblivionlauncher",
        "ffxivboot", "ffxivlauncher", "ffxivlauncher64", "xivlauncher",
    };

    /// <summary>
    /// Never part of a game by having been started from it. A game started directly can start
    /// Steam (SteamAPI_RestartAppIfNecessary), and one that opens a web page with no browser
    /// running starts the browser: adopted, either would keep the session open for as long as it
    /// ran and turn the stick's pointer off over it. The same for the store clients' helpers and
    /// the overlays some games spawn. In the game's own folder the folder rule still applies.
    /// </summary>
    private static readonly HashSet<string> NeverAdopted = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "gameoverlayui", "gameoverlayui64",
        "epicgameslauncher", "epicwebhelper", "eosoverlayrenderer-win64-shipping", "eosoverlayrenderer-win32-shipping",
        "galaxyclient", "galaxyclient helper", "gog galaxy notifications renderer",
        "eadesktop", "eabackgroundservice", "origin", "upc", "uplaywebcore", "battle.net", "riotclientservices",
        "socialclubhelper", "rockstarservice", "discord",
        "msedge", "msedgewebview2", "chrome", "firefox", "brave", "opera", "vivaldi", "iexplore",
        "explorer", "conhost", "openconsole", "werfault", "werfaultsecure", "openwith",
    };

    /// <summary>A child started this long after its parent was last seen running still counts as
    /// its child: the parent is only noticed gone at the next refresh.</summary>
    private static readonly long ExitSlack = TimeSpan.FromSeconds(1).Ticks;
    /// <summary>How long an exited member is remembered, for a child of it not yet seen.</summary>
    private const long GoneKeptMs = 60_000;
    /// <summary>Joins logged per session; a launcher that runs a helper every few seconds would
    /// otherwise write a line for each.</summary>
    private const int JoinsLogged = 20;

    private static readonly uint Self = (uint)Environment.ProcessId;

    /// <summary>
    /// Programs in the Windows folder are never the game's, whoever started them: a game that opens
    /// its readme starts Notepad, and adopted it would hold the session open and lose the pointer
    /// over it. They pass the link on instead, because a launcher may run the game through one --
    /// cmd running a script, rundll32 opening something. Explorer is on NeverAdopted and passes
    /// nothing on: what somebody opens from its window is theirs.
    /// </summary>
    private static readonly string WindowsDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;

    private enum Joined { Folder, Started, Child }

    private sealed class Proc
    {
        public uint Pid, ParentPid;
        /// <summary>FILETIME ticks; 0 when the process could not be opened.</summary>
        public long Created;
        /// <summary>Lower case, without ".exe".</summary>
        public string Name = "";
        public string? Path;
        /// <summary>Learned on demand from a process that could not be opened: no parent, no name.
        /// The next snapshot fills it in.</summary>
        public bool Partial;
        public bool Member;
        /// <summary>A Windows program started by the game (cmd, Notepad): not the game's, but what it
        /// starts can be. See WindowsDir.</summary>
        public bool Relay;
        public Joined How;
        /// <summary>The member that started it, directly or through relays, when one did.</summary>
        public Proc? Up;
        public bool KnownLauncher;
        /// <summary>A child of it has a game-sized window open now (recomputed every refresh).</summary>
        public bool Hosting;
        /// <summary>A child of it has had one at some point: it is a launcher, and a launcher that
        /// was started from the game does not keep the session open (see KeepsAlive).</summary>
        public bool LaunchedGame;
        public long SeenAt;
        public long GoneAt;
    }

    private readonly string? _prefix;
    private readonly object _gate = new();
    /// <summary>Only one refresh at a time: two applied out of order would retire processes the
    /// older snapshot had not seen yet.</summary>
    private readonly object _refreshGate = new();
    private readonly Dictionary<uint, Proc> _procs = new();
    private readonly List<Proc> _gone = new();
    private uint _startedPid;
    private long _startedAt;
    private int _joinsLogged;
    private long _lastJoinAt = Environment.TickCount64;

    /// <summary>How long since a process last became the game's (or since the session began).
    /// A launch chain is busy then, and worth watching closely.</summary>
    public long MsSinceJoin => Environment.TickCount64 - Interlocked.Read(ref _lastJoinAt);

    public SessionProcesses(string? folder)
    {
        _prefix = string.IsNullOrEmpty(folder) ? null : folder.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
    }

    /// <summary>The process Loungepad started: the game's however its exe is named or wherever it is.</summary>
    public void Seed(Process process)
    {
        uint pid;
        try { pid = (uint)process.Id; } catch (InvalidOperationException) { return; }
        var (_, created, _) = Query(pid);
        lock (_gate)
        {
            _startedPid = pid;
            _startedAt = created;
            // Anything judged before the seed was judged without it.
            foreach (var outsider in _procs.Values.Where(x => !x.Member).ToList()) _procs.Remove(outsider.Pid);
            Learn(pid, 0);
        }
    }

    /// <summary>Is this one of the game's processes, launcher or game?</summary>
    public bool Contains(uint pid) => Lookup(pid)?.Member == true;

    /// <summary>Is this the game itself rather than its launcher? What the joystick mouse asks.</summary>
    public bool IsGame(uint pid) => Lookup(pid) is { Member: true } p && !IsLauncher(p);

    /// <summary>Every process of the game running now.</summary>
    public List<int> LivePids()
    {
        Refresh();
        lock (_gate) return _procs.Values.Where(p => p.Member).Select(p => (int)p.Pid).ToList();
    }

    /// <summary>
    /// A process that means the game is still going, or null: anything in its folder, the process
    /// Loungepad started, or a child that is not a launcher. A launcher the game started -- the
    /// Rockstar Games Launcher from GTA V's own launcher -- stays up in the tray after the game,
    /// and the session would never end. The game first, so the log names it.
    /// </summary>
    public Process? FindRunning()
    {
        Refresh();
        List<Proc> candidates;
        lock (_gate)
            candidates = _procs.Values.Where(KeepsAlive).OrderBy(p => IsLauncher(p) ? 1 : 0).ToList();
        foreach (var p in candidates)
        {
            // The id may have been handed to another process since the snapshot.
            var (_, created, _) = Query(p.Pid);
            if (created != 0 && p.Created != 0 && created != p.Created) continue;
            try { return Process.GetProcessById((int)p.Pid); }
            catch (ArgumentException) { /* gone since the snapshot */ }
        }
        return null;
    }

    private static bool KeepsAlive(Proc p) => p.Member && (p.How != Joined.Child || !(p.KnownLauncher || p.LaunchedGame));
    private static bool IsLauncher(Proc p) => p.KnownLauncher || p.Hosting;

    /// <summary>
    /// Brings the picture up to date: who has started, who has gone, and which of the game's
    /// processes is hosting another one's window. A process snapshot, the details of only the
    /// processes not seen before, and -- only when one of the game's processes was started by
    /// another -- a pass over the top-level windows.
    /// </summary>
    public void Refresh()
    {
        lock (_refreshGate)
        {
            long takenAt = Environment.TickCount64;
            var snapshot = Snapshot();

            List<(uint Pid, uint Parent, string Name)> fresh;
            lock (_gate)
                fresh = snapshot.Where(e => !_procs.TryGetValue(e.Pid, out var p) || p.Partial || !Same(p, e)).ToList();
            // Opened outside the lock: the pad thread asks about the foreground 125 times a second.
            var details = fresh.ToDictionary(e => e.Pid, e => Query(e.Pid));
            HashSet<uint> children;

            lock (_gate)
            {
                var byPid = new Dictionary<uint, (uint Pid, uint Parent, string Name)>(snapshot.Count);
                foreach (var e in snapshot) byPid[e.Pid] = e;

                // Gone, or the id now belongs to some other process.
                foreach (var p in _procs.Values.ToList())
                {
                    if (p.SeenAt > takenAt) continue;   // learned after the snapshot was taken
                    if (byPid.TryGetValue(p.Pid, out var e) && (p.Partial || Same(p, e))) continue;
                    Retire(p);
                }

                var judging = new HashSet<uint>();
                Proc? Resolve(uint pid)
                {
                    if (pid == 0) return null;
                    if (_procs.TryGetValue(pid, out var known) && !known.Partial) { known.SeenAt = Math.Max(known.SeenAt, takenAt); return known; }
                    if (!byPid.TryGetValue(pid, out var e) || !judging.Add(pid)) return known;
                    var (path, created, _) = details.TryGetValue(pid, out var d) ? d : Query(pid);
                    var p = known ?? new Proc { Pid = pid };
                    p.ParentPid = e.Parent; p.Name = e.Name; p.Path ??= path;
                    if (p.Created == 0) p.Created = created;
                    p.Partial = false; p.SeenAt = takenAt;
                    _procs[pid] = p;
                    Judge(p, Resolve);
                    return p;
                }
                foreach (var e in snapshot) Resolve(e.Pid);
                children = _procs.Values.Where(p => p.Member && p.Up is not null).Select(p => p.Pid).ToHashSet();
            }

            // Only a window of a process that some other process of the game started can make
            // that other one a launcher. Most sessions have none, and pay nothing here.
            var windowOwners = children.Count == 0 ? children : GameWindowOwners(children);

            lock (_gate)
            {
                foreach (var p in _procs.Values) p.Hosting = false;
                foreach (var owner in windowOwners)
                {
                    if (!_procs.TryGetValue(owner, out var w) || !w.Member) continue;
                    for (var a = w.Up; a is not null; a = a.Up)
                    {
                        a.Hosting = true;
                        if (a.LaunchedGame) continue;
                        a.LaunchedGame = true;
                        Log.Info($"Session: {a.Name} (pid {a.Pid}) is the game's launcher: {w.Name} (pid {w.Pid}) opened the game's window. The stick stays a mouse over it");
                    }
                }

                long now = Environment.TickCount64;
                _gone.RemoveAll(g => now - g.SeenAt > GoneKeptMs);
            }
        }
    }

    private static bool Same(Proc p, (uint Pid, uint Parent, string Name) e) => p.ParentPid == e.Parent && p.Name == e.Name;

    private void Retire(Proc p)
    {
        _procs.Remove(p.Pid);
        if (!p.Member && !p.Relay) return;
        p.GoneAt = DateTime.UtcNow.ToFileTimeUtc();
        _gone.Add(p);
    }

    /// <summary>A process asked about before any snapshot has seen it: the foreground window's,
    /// from the pad thread. Its parent is read from the process itself.</summary>
    private Proc? Lookup(uint pid)
    {
        if (pid == 0) return null;
        lock (_gate) return _procs.TryGetValue(pid, out var known) ? known : Learn(pid, 0);
    }

    private Proc? Learn(uint pid, int depth)
    {
        if (_procs.TryGetValue(pid, out var known)) return known;
        if (depth > 16) return null;
        var (path, created, parent) = Query(pid);
        var p = new Proc
        {
            Pid = pid, Path = path, Created = created, Name = NameOf(path), SeenAt = Environment.TickCount64,
            ParentPid = parent ?? 0, Partial = parent is null,
        };
        _procs[pid] = p;
        Judge(p, ppid => Learn(ppid, depth + 1));
        return p;
    }

    /// <summary>Decides membership once, from the folder, the seed and the parent.</summary>
    private void Judge(Proc p, Func<uint, Proc?> resolve)
    {
        p.Member = false;
        p.Relay = false;
        var parent = Parent(p, resolve);
        p.Up = parent is null ? null : parent.Member ? parent : parent.Up;
        if (p.Pid == Self) return;
        if (_prefix is not null && p.Path is not null && p.Path.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
        { p.Member = true; p.How = Joined.Folder; }
        else if (p.Pid == _startedPid && (_startedAt == 0 || p.Created == 0 || p.Created == _startedAt))
        { p.Member = true; p.How = Joined.Started; }
        else if (p.Up is not null && p.Name.Length > 0 && !NeverAdopted.Contains(p.Name))
        {
            if (p.Path is not null && p.Path.StartsWith(WindowsDir, StringComparison.OrdinalIgnoreCase)) { p.Relay = true; return; }
            p.Member = true; p.How = Joined.Child;
            if (_joinsLogged++ < JoinsLogged)
                Log.Info($"Session: {p.Name} (pid {p.Pid}) is part of the game, started by {p.Up.Name} (pid {p.Up.Pid})");
        }
        if (!p.Member) return;
        Interlocked.Exchange(ref _lastJoinAt, Environment.TickCount64);
        p.KnownLauncher = KnownLaunchers.Contains(p.Name);
        if (p.KnownLauncher) Log.Info($"Session: {p.Name} (pid {p.Pid}) is a known launcher. The stick stays a mouse over it");
    }

    /// <summary>The member (or relay) that started this process, if one did and the times agree.</summary>
    private Proc? Parent(Proc child, Func<uint, Proc?> resolve)
    {
        if (child.ParentPid == 0 || child.Partial) return null;
        if (resolve(child.ParentPid) is { } live && (live.Member || live.Relay) && Before(live, child)) return live;
        for (int i = _gone.Count - 1; i >= 0; i--)
        {
            var g = _gone[i];
            if (g.Pid == child.ParentPid && Before(g, child) && (child.Created == 0 || child.Created <= g.GoneAt + ExitSlack))
                return g;
        }
        return null;
    }

    private static bool Before(Proc parent, Proc child) =>
        parent.Created == 0 || child.Created == 0 || parent.Created <= child.Created;

    private static string NameOf(string? pathOrFile) =>
        string.IsNullOrEmpty(pathOrFile) ? "" : System.IO.Path.GetFileNameWithoutExtension(pathOrFile).ToLowerInvariant();

    private static List<(uint Pid, uint Parent, string Name)> Snapshot()
    {
        var list = new List<(uint, uint, string)>(512);
        var h = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return list;
        try
        {
            var e = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
            for (bool ok = NativeMethods.Process32FirstW(h, ref e); ok; ok = NativeMethods.Process32NextW(h, ref e))
                if (e.th32ProcessID != 0) list.Add((e.th32ProcessID, e.th32ParentProcessID, NameOf(e.szExeFile)));
        }
        finally { NativeMethods.CloseHandle(h); }
        return list;
    }

    /// <summary>The image path, the start time and the parent's id, from one handle. Whatever the
    /// process refuses comes back empty.</summary>
    private static (string? Path, long Created, uint? Parent) Query(uint pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (int)pid);
        if (h == IntPtr.Zero) return (null, 0, null);
        try
        {
            string? path = null;
            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref len)) path = sb.ToString(0, (int)len);
            long created = NativeMethods.GetProcessTimes(h, out var c, out _, out _, out _) ? c : 0;
            uint? parent = NativeMethods.NtQueryInformationProcess(h, 0, out var info, Marshal.SizeOf<NativeMethods.PROCESS_BASIC_INFORMATION>(), out _) == 0
                ? (uint)info.InheritedFromUniqueProcessId.ToInt64() : null;
            return (path, created, parent);
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private const long WS_EX_TRANSPARENT = 0x00000020, WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// Which of these processes own a window that could be a game's: shown, not cloaked, not a
    /// tool window, at least 200x150 (FindGameWindow's test), and one that can be clicked and
    /// activated -- an overlay a game spawns is neither. Consoles and dialogs never count: a
    /// launcher running a script in a console, or a game's child putting up an error box, is not
    /// a game starting.
    /// </summary>
    private static HashSet<uint> GameWindowOwners(HashSet<uint> among)
    {
        var owners = new HashSet<uint>();
        var cls = new StringBuilder(64);
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!among.Contains(pid) || owners.Contains(pid)) return true;
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & (NativeMethods.WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE)) != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r) || r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            try
            {
                if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return true;
            }
            catch { /* no DWM answer: judge on the rest */ }
            cls.Clear();
            NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
            var name = cls.ToString();
            if (name is "ConsoleWindowClass" or "PseudoConsoleWindow" or "#32770") return true;
            owners.Add(pid);
            return true;
        }, IntPtr.Zero);
        return owners;
    }
}
