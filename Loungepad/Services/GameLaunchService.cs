using System.Diagnostics;
using System.IO;
using Loungepad.Interop;
using Loungepad.Models;

namespace Loungepad.Services;

/// <summary>
/// Orchestrates a game session:
///  1. optionally switch the TV to be the Windows primary display,
///  2. start the game (direct exe, a store's link -- steam://, Epic's, uplay://, origin2:// -- or
///     Battle.net's or Rockstar's launcher),
///  3. find the actual game process (URI launches go through the store client),
///  4. nudge the game window onto the TV if it opened elsewhere,
///  5. wait for exit, restore the primary display, record playtime, hand focus back.
/// </summary>
public class GameLaunchService
{
    private readonly DisplayService _displays;
    private readonly SettingsStore _settings;
    private readonly LibraryStore _library;

    public bool GameRunning { get; private set; }
    public string? RunningGameId { get; private set; }

    /// <summary>The running game's processes: its folder's, the one Loungepad started, and what
    /// those started, with its launcher told apart. Null between sessions.</summary>
    private volatile SessionProcesses? _session;
    /// <summary>Set when the close came from Loungepad itself (the in-game menu, the game menu, a
    /// swap). Nothing is going to take over from the game then, so nothing is waited for.</summary>
    private volatile bool _closeRequested;
    /// <summary>Cancelled by Abandon: every wait the session makes for a process that has not
    /// appeared ends at once, and the session with it.</summary>
    private volatile CancellationTokenSource? _sessionCts;

    /// <summary>
    /// The session has started but no process of the game has been seen yet: a store client is
    /// still launching it, or it never will (an update, a crash on start, a game whose exe lives
    /// outside its folder). The page shows it as starting rather than playing, and a close ends
    /// the session straight away rather than finding nothing to close.
    /// </summary>
    public bool Starting { get; private set; }
    /// <summary>The first process of the game was found: Starting went false. Off the UI thread.</summary>
    public event Action? ProcessTracked;

    public event Action<Game>? GameStarted;
    public event Action<Game>? GameExited;
    /// <summary>The moment the game was started, for the activity log: raised right after GameStarted.</summary>
    public event Action<Game, DateTime>? SessionStarted;
    /// <summary>The session is over: the game, when it began and ended, and whether it was long
    /// enough to count (MinSessionMinutes). Raised before GameExited, off the UI thread.</summary>
    public event Action<Game, DateTime, DateTime, bool>? SessionEnded;

    /// <summary>A sitting shorter than this is not a session: the game crashed on start, or was
    /// opened by mistake. It counts for nothing -- not playtime, not the session count, not the
    /// activity log -- so the three can never disagree.</summary>
    public const double MinSessionMinutes = 0.5;

    public GameLaunchService(DisplayService displays, SettingsStore settings, LibraryStore library)
    {
        _displays = displays;
        _settings = settings;
        _library = library;
    }

    public void Launch(Game game)
    {
        if (GameRunning) return;
        GameRunning = true;
        RunningGameId = game.Id;
        _ = Task.Run(() => RunSession(game));
    }

    private async Task RunSession(Game game)
    {
        var s = _settings.Settings;
        bool switchedPrimary = false;
        var started = DateTime.Now;
        var monitorCts = new CancellationTokenSource();

        // Where the game's processes live. For a ROM that is the EMULATOR's folder, not the
        // ROM's: the emulator is the process that runs, the one whose window goes on the TV and
        // the one the in-game menu closes. Nothing in a ROM folder is ever a running process.
        var sessionDir = SessionDir(game);
        var session = new SessionProcesses(sessionDir);
        _session = session;
        _closeRequested = false;
        var cts = new CancellationTokenSource();
        _sessionCts = cts;
        Starting = true;

        try
        {
            if (s.SwitchPrimaryOnLaunch && s.TvDeviceName is not null)
            {
                var primary = _displays.CurrentPrimaryDevice();
                if (!string.Equals(primary, s.TvDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    switchedPrimary = _displays.SetPrimary(s.TvDeviceName);
                    if (switchedPrimary) await Task.Delay(1200); // let the desktop settle before the game probes displays
                }
            }

            Process? tracked = StartGame(game, cts.Token);

            // A process we started ourselves is granted the right to take the foreground NOW,
            // while this window still is the foreground. GameStarted parks the launcher a moment
            // later, and Windows only ever grants that right to a process started by the CURRENT
            // foreground process -- so an emulator whose window opened two seconds after the
            // launcher hid came up behind whatever the launcher had been covering, unfocused.
            // Steam games never showed it: exclusive fullscreen takes the foreground by force.
            if (tracked is not null)
            {
                AllowForeground(tracked);
                session.Seed(tracked);
            }
            GameStarted?.Invoke(game);
            SessionStarted?.Invoke(game, started);

            // Who has started since, who has gone, and which of them is the launcher.
            _ = Task.Run(() => FollowAsync(session, monitorCts.Token));

            // And, in case the grant was refused or the emulator never asks, the first real window
            // the game opens is brought to the front once.
            _ = Task.Run(() => FocusGameWindowAsync(game, monitorCts.Token));

            // One monitor for the whole session, covering every process the game spawns.
            if (s.RepositionGameWindow && s.TvDeviceName is not null)
                _ = Task.Run(() => MonitorGameWindows(s.TvDeviceName, monitorCts.Token));


            // URI launches (Steam/Epic) return the store client, not the game — find the real
            // process. Two minutes, for a store client that updates the game first; Abandon ends
            // it early when the user gives up on it from the library.
            if (tracked is null && sessionDir is not null)
                tracked = await WaitForGameProcess(session, TimeSpan.FromSeconds(120), cts.Token);

            if (tracked is not null)
            {
                // Many games chain through their own pre-launcher (e.g. REDprelauncher ->
                // REDlauncher -> Cyberpunk2077.exe), and some launchers run the game from another
                // folder (Prism Launcher -> javaw.exe). Keep the session alive as long as ANY
                // process of the game is running (SessionProcesses.FindRunning), re-attaching to
                // each successor.
                while (tracked is not null)
                {
                    Log.Info($"Tracking game process {tracked.ProcessName} (pid {tracked.Id}) for {game.Title}");
                    if (Starting)
                    {
                        Starting = false;
                        ProcessTracked?.Invoke();
                    }
                    var trackedSince = DateTime.UtcNow;

                    await tracked.WaitForExitAsync();

                    // Anything of the game's still running right now carries the session on: a
                    // launcher hands over to the game before it exits (REDprelauncher starts
                    // REDlauncher, which starts Cyberpunk2077.exe), so the successor is already
                    // there the moment its parent goes.
                    tracked = session.FindRunning();
                    if (tracked is not null || _closeRequested) continue;
                    // With no folder nothing new can join: only a child of a process still running.
                    if (sessionDir is null) break;

                    // Nothing running. This used to wait a flat 15 seconds for a successor after
                    // every exit, which is the pause between a game closing and the launcher
                    // coming back. The long wait is only earned by a process that lived a few
                    // seconds -- a pre-launcher that exits before the game has started. Something
                    // that ran for minutes was the game, and gets a second, for a game that
                    // restarts itself after a settings change.
                    var grace = DateTime.UtcNow - trackedSince < TimeSpan.FromSeconds(90)
                        ? TimeSpan.FromSeconds(15)
                        : TimeSpan.FromSeconds(1);
                    tracked = await WaitForGameProcess(session, grace, cts.Token);
                }
                Log.Info($"{game.Title} has exited");
            }
            else if (cts.IsCancellationRequested)
            {
                Log.Info($"{game.Title} never showed a process; the session was ended from the library");
            }
            else
            {
                Log.Info($"Could not find a process for {game.Title}; assuming it exited after grace period");
                try { await Task.Delay(TimeSpan.FromSeconds(20), cts.Token); }
                catch (OperationCanceledException) { /* abandoned meanwhile: end now */ }
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Launch session for {game.Title} failed: {ex}");
        }
        finally
        {
            monitorCts.Cancel();
            monitorCts.Dispose();
            _sessionCts = null;
            cts.Dispose();
            Starting = false;
            // A game killed while frozen leaves handles behind and a Paused that would carry
            // into the next session.
            Resume();
            _session = null;

            if (switchedPrimary) _displays.RestorePrimary();

            var ended = DateTime.Now;
            var minutes = (ended - started).TotalMinutes;
            var counted = minutes > MinSessionMinutes;
            var g = _library.Find(game.Id);
            if (g is not null)
            {
                if (counted) { g.PlaytimeMinutes += minutes; g.Sessions++; }
                g.LastPlayed = ended;
                _library.Save();
            }
            try { SessionEnded?.Invoke(game, started, ended, counted); }
            catch (Exception ex) { Log.Info($"Session record failed: {ex.Message}"); }

            GameRunning = false;
            RunningGameId = null;
            GameExited?.Invoke(game);
        }
    }

    /// <summary>The folder whose processes are the game's for this session. See RunSession.</summary>
    private string? SessionDir(Game game)
    {
        if (!game.Emulated) return game.InstallDir;
        var emulator = _library.EmulatorFor(game);
        return emulator is null ? null : Path.GetDirectoryName(emulator.ExePath);
    }

    /// <summary>Everything the bridge checks before it lets an emulated game launch, in one place,
    /// so the toast it shows and the exception below can never disagree about what is wrong.</summary>
    public EmulatorCommand? ResolveEmulated(Game game, out string? problem) =>
        EmulatorLaunch.Resolve(game, _library.EmulatorFor(game), _library.FindRomFolder(game.RomFolderId), out problem);

    private Process? StartGame(Game game, CancellationToken ct)
    {
        if (game.Emulated)
        {
            var cmd = ResolveEmulated(game, out var problem)
                      ?? throw new InvalidOperationException(problem ?? "The emulator could not be resolved");
            Log.Info($"Emulated launch: \"{cmd.Exe}\" {cmd.Args}");
            if (EmulatorPresets.Detect(cmd.Exe) is { HideConsole: true } preset)
                return StartWithoutConsole(cmd, preset.Key);
            return Process.Start(new ProcessStartInfo(cmd.Exe)
            {
                UseShellExecute = true,
                Arguments = cmd.Args,
                WorkingDirectory = cmd.WorkingDir,
            });
        }

        // "Prefer direct launch" (user picked an exe in Manage) bypasses the store client. A classic
        // Battle.net game (Diablo II, Warcraft III) has no client to go through.
        bool direct = game.ExePath is not null && File.Exists(game.ExePath)
                      && (game.Platform is "GOG" or "Manual" || game.PreferDirectLaunch
                          || (game.Platform == BattleNet.Platform && !BattleNet.LaunchesThroughClient(game)));

        if (!direct && BattleNet.LaunchesThroughClient(game))
        {
            // The client may need starting and signing in before it takes the launch, which can be
            // most of a minute; that waits beside the session, which is already looking for the
            // game's process in its folder. Abandon (a close from the library) cancels it.
            _ = Task.Run(async () =>
            {
                try { await BattleNet.StartAsync(game, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Info($"Battle.net launch of {game.Title} failed: {ex.Message}"); }
            });
            return null;
        }

        if (!direct && game.Platform == Rockstar.Platform)
        {
            // Through the Rockstar Games Launcher, which stays in the tray: not the session's process.
            Rockstar.Start(game);
            return null;
        }

        if (!direct && game.LaunchUri is not null)
        {
            if (game.LaunchUri.StartsWith("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            {
                // Packaged (Xbox / Microsoft Store) apps are activated through the shell.
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{game.LaunchUri}\"") { UseShellExecute = true });
                return null;
            }
            // The stores' own links: steam://rungameid, Epic's apps/…?action=launch, uplay://launch,
            // origin2://game/launch, and battlenet://game/… for a Battle.net game newer than the
            // product table. Each opens the store's client, which starts the game.
            if (game.Platform is "Steam" or "Epic" or Ubisoft.Platform or EaApp.Platform or BattleNet.Platform)
            {
                Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
                return null; // real process found later via install dir
            }
        }

        if (game.ExePath is null || !File.Exists(game.ExePath))
            throw new FileNotFoundException($"Executable not found: {game.ExePath}");

        var psi = new ProcessStartInfo(game.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = game.InstallDir ?? Path.GetDirectoryName(game.ExePath) ?? ""
        };
        if (!string.IsNullOrWhiteSpace(game.Args)) psi.Arguments = game.Args;
        return Process.Start(psi);
    }

    private const long MaxConsoleLogBytes = 1024 * 1024;

    /// <summary>
    /// An emulator that is a console program with nothing to show in its console: KytyPS5, whose
    /// own launcher opens one on purpose for its log. Started the ordinary way, a console window
    /// would sit on the TV beside the game for the whole session. So it gets none, and what it
    /// prints -- with its log at its default of Silent, a few lines at start and any fatal error,
    /// the line worth having when a game will not boot -- goes to "&lt;preset&gt;.log" beside
    /// Loungepad's own log, started afresh each launch and capped at a megabyte. Both streams are
    /// always drained, file or no file: a full pipe would stall the emulator.
    /// </summary>
    private static Process? StartWithoutConsole(EmulatorCommand cmd, string key)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(cmd.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Arguments = cmd.Args,
                WorkingDirectory = cmd.WorkingDir,
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            // An exe that asks to run elevated can only be started by the shell, console and all.
            return Process.Start(new ProcessStartInfo(cmd.Exe)
            {
                UseShellExecute = true,
                Arguments = cmd.Args,
                WorkingDirectory = cmd.WorkingDir,
            });
        }
        if (process is null) return null;

        var path = Path.Combine(Paths.DataDir, key + ".log");
        StreamWriter? writer = null;
        try { writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }
        catch (Exception ex) { Log.Info($"Emulator output not kept: {ex.Message}"); }
        var gate = new object();
        long written = 0;
        var open = 2;
        void Take(string? line)
        {
            lock (gate)
            {
                if (line is null)
                {
                    if (--open == 0) { writer?.Dispose(); writer = null; }
                    return;
                }
                if (writer is null || written >= MaxConsoleLogBytes) return;
                // Colour codes, which a console would have drawn.
                line = AnsiEscape.Replace(line, "");
                try
                {
                    writer.WriteLine(line);
                    written += line.Length + 2;
                    if (written >= MaxConsoleLogBytes) writer.WriteLine("(the rest was not kept)");
                }
                catch { writer.Dispose(); writer = null; }
            }
        }
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Log.Info($"{Path.GetFileName(cmd.Exe)} started with no console; what it prints goes to {path}");
        return process;
    }

    private static readonly System.Text.RegularExpressions.Regex AnsiEscape =
        new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Poll until something of the game is running. Every quarter second rather than every one
    /// and a half: this runs while the user is waiting for the launcher to come back, and a
    /// snapshot of the process list costs a few milliseconds.
    /// </summary>
    private static async Task<Process?> WaitForGameProcess(SessionProcesses session, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (session.FindRunning() is { } found) return found;
            if (DateTime.UtcNow >= deadline || ct.IsCancellationRequested) return null;
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return null; }
        }
    }

    /// <summary>
    /// Keeps the session's picture of the game's processes current. A quarter second while a
    /// launch chain is busy -- the first half minute, and the half minute after anything joins --
    /// so a bootstrap that hands over and exits is seen before it goes; once a second after that.
    /// A refresh is a snapshot of every process, about 5 ms on a PC with 300 of them. The pad
    /// loop never waits for one: a window that comes to the front is looked up there and then.
    /// </summary>
    private static async Task FollowAsync(SessionProcesses session, CancellationToken ct)
    {
        bool reported = false;
        while (!ct.IsCancellationRequested)
        {
            try { session.Refresh(); }
            catch (Exception ex) when (!reported) { reported = true; Log.Info($"Session processes: {ex.Message}"); }
            catch { /* reported once */ }
            try { await Task.Delay(session.MsSinceJoin < 30_000 ? 250 : 1000, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>Is this pid one of the game's processes, its launcher included?</summary>
    private bool Owns(uint pid) => GameRunning && _session is { } s && s.Contains(pid);

    /// <summary>
    /// True when the foreground window is the running game itself -- not its launcher. Used to
    /// gate the joystick mouse: it must keep working when the game is merely running in the
    /// background, and over a launcher, which is a window to click Play in.
    /// </summary>
    public bool IsGameForeground()
    {
        if (!GameRunning || _session is not { } session) return false;
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return session.IsGame(pid);
    }

    /// <summary>Is this window the running game itself, not its launcher? What IsGameForeground asks
    /// of the foreground, asked of the window the Power Wheel opened over.</summary>
    public bool IsGameWindow(IntPtr hwnd)
    {
        if (!GameRunning || hwnd == IntPtr.Zero || _session is not { } session) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return session.IsGame(pid);
    }

    /// <summary>Does this window belong to the running game, launcher included? Used by the in-game menu.</summary>
    public bool OwnsWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return Owns(pid);
    }

    /// <summary>
    /// Every process of the running game right now, for the hardware readings and the pause: the
    /// launcher, the game, anything they started. Answered from the session's snapshot, so a poll
    /// every few seconds costs one detail query per NEW process rather than one per process.
    /// Empty when nothing is running.
    /// </summary>
    public IReadOnlyCollection<int> TrackedPids()
    {
        if (!GameRunning || _session is not { } session) return Array.Empty<int>();
        return session.LivePids();
    }

    /// <summary>
    /// Ask every process of the running game to quit. This backs up the window-by-window close:
    /// a game in exclusive fullscreen (or one whose only window is owned by a child process) may
    /// not appear in the alt-tab enumeration at all, in which case closing "every window the game
    /// owns" closes nothing and the menu looks like it did nothing. Returns how many processes
    /// were asked.
    /// </summary>
    public int RequestClose() => RequestClose(out _);

    /// <summary><paramref name="foundAny"/> says whether any process of the game was running at
    /// all; false means the session was ended here (see Abandon).</summary>
    public int RequestClose(out bool foundAny)
    {
        foundAny = false;
        if (!GameRunning) return 0;
        _closeRequested = true;
        // A frozen process never reads the WM_CLOSE it is about to be posted.
        Resume();
        int n = 0, found = 0;
        foreach (var pid in TrackedPids())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                found++;
                if (p.CloseMainWindow()) n++;
            }
            catch { /* protected, or exited between the snapshot and the call */ }
        }
        Log.Info($"Close game: asked {n} process(es) to quit");
        foundAny = found > 0;
        // Nothing of the game is running at all -- it never started, crashed on the way up, or
        // runs from a folder the session cannot see. There is nothing to close, so the close IS
        // the end of the session: the library goes back to free, and the game can be launched
        // again or another one started, instead of "no window to close" for two minutes.
        if (found == 0) Abandon();
        return n;
    }

    /// <summary>
    /// End the session now, whatever the game is doing: every wait for a process that has not
    /// appeared is cancelled and the session's finally runs. Called by RequestClose when it finds
    /// nothing of the game, which is the user giving up on a launch that went nowhere.
    /// </summary>
    public void Abandon()
    {
        if (!GameRunning) return;
        _closeRequested = true;
        Log.Info($"Close game: nothing of the game is running; ending the session");
        try { _sessionCts?.Cancel(); }
        catch (ObjectDisposedException) { /* the session ended on its own meanwhile */ }
    }

    /// <summary>
    /// Wait for the current session to finish, up to a timeout. False means it is still running,
    /// which happens when a game ignores the close request or puts up its own "really quit?".
    /// </summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (GameRunning && DateTime.UtcNow < until) await Task.Delay(200);
        return !GameRunning;
    }

    // ---- pausing: the game frozen where it stands ----

    /// <summary>Whether the running game's processes are frozen (see Pause).</summary>
    public bool Paused { get; private set; }
    /// <summary>Paused or thawed, for the page's in-game menu. Raised off the UI thread as often as on it.</summary>
    public event Action<bool>? PausedChanged;
    private readonly List<IntPtr> _pausedHandles = new();
    private readonly object _pauseGate = new();

    /// <summary>
    /// Freeze every process of the running game where it stands -- NtSuspendProcess on each, the
    /// way PlayState does it for Playnite -- so it stops drawing, computing and listening while
    /// nobody is playing, and picks up exactly there when thawed. The handles are kept open until
    /// then, which is what the thaw needs and what stops a pid being reused underneath it.
    /// Returns how many froze; 0 when nothing is running or nothing could be opened (a game
    /// running elevated refuses the handle). A game that is already frozen is left alone, so
    /// the caller that froze it is the one that thaws it.
    /// </summary>
    public int Pause()
    {
        int n = 0;
        lock (_pauseGate)
        {
            if (Paused || !GameRunning) return 0;
            foreach (var pid in TrackedPids())
            {
                var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero) { Log.Info($"Pause: could not open pid {pid}"); continue; }
                int rc = NativeMethods.NtSuspendProcess(h);
                if (rc != 0) { Log.Info($"Pause: NtSuspendProcess on pid {pid} answered 0x{rc:X8}"); NativeMethods.CloseHandle(h); continue; }
                _pausedHandles.Add(h);
                n++;
            }
            if (n > 0) Paused = true;
        }
        Log.Info(n > 0 ? $"Pause: froze {n} process(es) of the game" : "Pause: nothing of the game could be frozen");
        if (n > 0) PausedChanged?.Invoke(true);
        return n;
    }

    /// <summary>Thaw what Pause froze. Safe to call at any time; a no-op when nothing is frozen.</summary>
    public void Resume()
    {
        bool was;
        lock (_pauseGate)
        {
            was = Paused;
            // In reverse, so a launcher that watches its child sees the child alive first.
            for (int i = _pausedHandles.Count - 1; i >= 0; i--)
            {
                NativeMethods.NtResumeProcess(_pausedHandles[i]);
                NativeMethods.CloseHandle(_pausedHandles[i]);
            }
            _pausedHandles.Clear();
            Paused = false;
        }
        if (!was) return;
        Log.Info("Pause: the game is running again");
        PausedChanged?.Invoke(false);
    }

    private static void AllowForeground(Process p)
    {
        try
        {
            var ok = NativeMethods.AllowSetForegroundWindow((uint)p.Id);
            if (!ok) Log.Info("AllowSetForegroundWindow was refused: the launcher was not the foreground process");
        }
        catch (Exception ex) { Log.Info($"AllowSetForegroundWindow failed: {ex.Message}"); }
    }

    /// <summary>
    /// Waits for the game's first real window and brings it to the front, once.
    ///
    /// Only if the game has not taken the foreground on its own by then, and never while the
    /// launcher itself is in front -- that is the in-game menu, and a menu that loses focus to
    /// the game it is over reads as broken. Gives up after half a minute: a game that takes
    /// longer than that to open a window is loading, and its window will be the foreground when
    /// it comes.
    /// </summary>
    private async Task FocusGameWindowAsync(Game game, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromSeconds(30))
        {
            var fg = NativeMethods.GetForegroundWindow();
            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out var fgPid);
                if (Owns(fgPid)) return;
                // Ours after the first second means an overlay is up. Right at the start it is
                // only the launcher not having finished hiding yet.
                if (fgPid == (uint)Environment.ProcessId && DateTime.UtcNow - started > TimeSpan.FromSeconds(1)) return;
            }

            var hwnd = FindGameWindow();
            if (hwnd != IntPtr.Zero)
            {
                WindowService.ForceForeground(hwnd);
                var got = NativeMethods.GetForegroundWindow() == hwnd;
                Log.Info($"Brought {game.Title}'s window to the front" + (got ? "" : " (refused; the game keeps whatever focus it has)"));
                return;
            }

            try { await Task.Delay(250, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// <summary>The first visible top-level window of the game's that is big enough to be the
    /// game rather than a splash or a tooltip, or zero. Also the Power Wheel's way back to a game
    /// that is already running when its shortcut is pressed.</summary>
    internal IntPtr FindGameWindow()
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!Owns(pid)) return true;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;
            if (r.Right - r.Left < 200 || r.Bottom - r.Top < 150) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Keeps the game on the TV for the whole session, not just the first window it opens.
    /// Games routinely put a launcher or config dialog on another display, and once the user
    /// clicks it the game itself then opens there, so a one-shot nudge at startup is not enough.
    /// Only windows whose centre has drifted off the TV are touched, so a game already sitting
    /// correctly (including exclusive fullscreen) is left alone.
    /// </summary>
    private async Task MonitorGameWindows(string tvDeviceName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // SetWindowPos waits on the window's thread, and a frozen thread never answers.
                var tv = Paused ? null : _displays.GetDisplay(tvDeviceName);
                if (tv is not null) EnforceOnTv(tv);
            }
            catch (Exception ex) { Log.Info($"Window monitor: {ex.Message}"); }

            try { await Task.Delay(1500, ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void EnforceOnTv(DisplayInfo tv)
    {
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (!Owns(pid)) return true;

            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return true;

            int w0 = r.Right - r.Left, h0 = r.Bottom - r.Top;
            if (w0 < 200 || h0 < 150) return true;                  // splash / tooltip
            if (r.Left <= -30000 || r.Top <= -30000) return true;   // minimized

            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            bool onTv = cx >= tv.X && cx < tv.X + tv.Width && cy >= tv.Y && cy < tv.Y + tv.Height;
            if (onTv) return true;

            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, tv.X, tv.Y,
                Math.Min(w0, tv.Width), Math.Min(h0, tv.Height),
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_SHOWWINDOW);
            Log.Info($"Moved game window {hwnd} (pid {pid}) onto {tv.DeviceName}");
            return true;   // keep scanning: a game can own more than one stray window
        }, IntPtr.Zero);
    }
}
