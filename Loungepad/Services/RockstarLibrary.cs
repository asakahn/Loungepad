using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// The Rockstar Games Launcher's games, the way Playnite's Rockstar library reads them.
///
/// Every game the launcher installs gets an uninstall entry whose uninstaller is told the game's
/// title id (`uninstall.exe -enableFullMode -uninstall=rdr2`), which <see cref="Titles"/> turns into
/// a name and an exe. Each game also writes Rockstar's own key, HKLM\SOFTWARE\Rockstar
/// Games\&lt;game&gt; with InstallFolder -- the keys the launcher's public title_metadata.json
/// names -- which is read when an uninstall entry is missing.
///
/// A game starts through the launcher (`Launcher.exe -launchTitleInFolder "&lt;folder&gt;"`, Playnite's
/// call), which signs in and checks ownership first. A Steam or Epic copy of a Rockstar game also
/// runs through the launcher but belongs to that store's entry; one found here in a folder another
/// store already has is left out (<see cref="ScanInstalled"/>).
///
/// Rockstar publishes no list of the games an account owns and no achievements outside Social Club's
/// own pages, so there is no sign-in here: what is on disk is the library.
/// </summary>
public static class Rockstar
{
    public const string Platform = "Rockstar";
    public const string IdPrefix = "rockstar:";

    /// <summary>A launcher title: its id, the name, the exe Playnite starts it by (relative to the
    /// folder), and Rockstar's own registry key for it under HKLM\SOFTWARE (32-bit view).</summary>
    public sealed record Title(string Id, string Name, string Exe, string? RegKey);

    /// <summary>
    /// Playnite's RockstarGames.cs (PlayniteExtensions, MIT, Mar 2025) with the registry keys from
    /// Rockstar's title_metadata.json (Oct 2026). GTA V is the launcher's "Grand Theft Auto V
    /// Legacy" since Enhanced arrived, which is also what Steam calls it now, so the lookup by title
    /// finds the right one. Enhanced and the 2024 Red Dead Redemption are not in the metadata yet
    /// and have no key.
    /// </summary>
    public static readonly IReadOnlyList<Title> Titles = new Title[]
    {
        new("gta5", "Grand Theft Auto V Legacy", "PlayGTAV.exe", @"Rockstar Games\Grand Theft Auto V"),
        new("gta5_gen9", "Grand Theft Auto V Enhanced", "GTA5_Enhanced_BE.exe", null),
        new("rdr", "Red Dead Redemption", "RDR.exe", null),
        new("rdr2", "Red Dead Redemption 2", "RDR2.exe", @"Rockstar Games\Red Dead Redemption 2"),
        new("lanoire", "L.A. Noire", "LANoire.exe", @"Rockstar Games\LANoireMTL"),
        new("lanoirevr", "L.A. Noire: The VR Case Files", "LANoireVR.exe", @"Rockstar Games\LANoireVR"),
        new("mp3", "Max Payne 3", "MaxPayne3.exe", @"Rockstar Games\MP3MTL"),
        new("gtasa", "Grand Theft Auto: San Andreas", "gta_sa.exe", @"Rockstar Games\GTA: San Andreas"),
        new("gta3", "Grand Theft Auto III", "gta3.exe", @"Rockstar Games\Grand Theft Auto III"),
        new("gtavc", "Grand Theft Auto: Vice City", "gta-vc.exe", @"Rockstar Games\Grand Theft Auto: Vice City"),
        new("bully", "Bully: Scholarship Edition", "Bully.exe", @"Rockstar Games\Bully"),
        new("gta4", "Grand Theft Auto IV", "GTAIV.exe", @"Rockstar Games\Grand Theft Auto IV"),
        new("gta3unreal", "Grand Theft Auto III – The Definitive Edition", @"Gameface\Binaries\Win64\LibertyCity.exe", @"Rockstar Games\GTA III - DE"),
        new("gtavcunreal", "Grand Theft Auto: Vice City – The Definitive Edition", @"Gameface\Binaries\Win64\ViceCity.exe", @"Rockstar Games\GTA Vice City - DE"),
        new("gtasaunreal", "Grand Theft Auto: San Andreas – The Definitive Edition", @"Gameface\Binaries\Win64\SanAndreas.exe", @"Rockstar Games\GTA San Andreas - DE"),
    };

    public static Title? ById(string id) => Titles.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---------- the launcher ----------

    /// <summary>Launcher.exe: where its uninstall entry or its own registry key says, else where
    /// the rockstar: link handler points. Null when it is not installed.</summary>
    public static string? ClientExe()
    {
        try
        {
            foreach (var e in UninstallEntries.All())
                if ((e.DisplayName == "Rockstar Games Launcher" || e.UninstallString?.Contains("-uninstall=launcher", StringComparison.OrdinalIgnoreCase) == true)
                    && e.InstallLocation is { } loc && File.Exists(Path.Combine(loc, "Launcher.exe")))
                    return Path.Combine(loc, "Launcher.exe");
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var k = root.OpenSubKey(@"SOFTWARE\Rockstar Games\Launcher"))
                if (UninstallEntries.Clean(k?.GetValue("InstallFolder") as string) is { } dir && File.Exists(Path.Combine(dir, "Launcher.exe")))
                    return Path.Combine(dir, "Launcher.exe");
            using var cmd = Registry.ClassesRoot.OpenSubKey(@"rockstar\shell\open\command");
            if (cmd?.GetValue(null) is string c && Regex.Match(c, "^\"([^\"]+\\.exe)\"") is { Success: true } m && File.Exists(m.Groups[1].Value))
                return m.Groups[1].Value;
        }
        catch { /* not installed, as far as can be told */ }
        return null;
    }

    public static bool ClientInstalled() => ClientExe() is not null;

    /// <summary>
    /// Start the game through the launcher, which signs in and checks the game is owned before it
    /// runs it. The launcher stays in the tray afterwards, so it is not the session's process; the
    /// game is found in its folder like any store launch. With no launcher to be found, the game's
    /// own exe, which asks for the launcher itself.
    /// </summary>
    public static void Start(Game game)
    {
        var dir = game.InstallDir ?? throw new InvalidOperationException($"{game.Title} has no folder");
        if (ClientExe() is { } launcher)
        {
            Log.Info($"Rockstar: launch {game.Title} through the launcher");
            Process.Start(new ProcessStartInfo(launcher, $"-launchTitleInFolder \"{dir.TrimEnd('\\')}\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(launcher),
            })?.Dispose();
            return;
        }
        if (game.ExePath is { } exe && File.Exists(exe))
        {
            Log.Info($"Rockstar: no launcher found; starting {Path.GetFileName(exe)} directly");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
            return;
        }
        throw new FileNotFoundException("The Rockstar Games Launcher is not installed");
    }

    // ---------- installed games ----------

    /// <summary>
    /// Every launcher game on disk. <paramref name="others"/> is what the other stores found: a
    /// Rockstar game bought on Steam or Epic installs where that store says and may still leave
    /// Rockstar's own key behind it, and launching that copy through the Rockstar launcher would
    /// fail, so a folder another store has is that store's game.
    /// </summary>
    public static List<Game> ScanInstalled(IEnumerable<Game>? others = null) =>
        ScanInstalled(others, UninstallEntries.All(), readRegistry: true);

    /// <summary>The scan over given uninstall entries, for a test with no Rockstar game on its PC.</summary>
    internal static List<Game> ScanInstalled(IEnumerable<Game>? others, IEnumerable<UninstallEntries.Entry> entries, bool readRegistry)
    {
        var taken = (others ?? Enumerable.Empty<Game>())
            .Select(g => UninstallEntries.Clean(g.InstallDir)).Where(d => d is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        void Add(Title t, string dir, long sizeKb)
        {
            if (taken.Contains(dir) || dir.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase)) return;
            var exe = Path.Combine(dir, t.Exe);
            games.TryAdd(IdPrefix + t.Id, new Game
            {
                Id = IdPrefix + t.Id,
                Title = t.Name,
                Platform = Platform,
                InstallDir = dir,
                ExePath = File.Exists(exe) ? exe : null,
                SizeBytes = sizeKb * 1024,
                Installed = true,
            });
        }

        foreach (var e in entries)
        {
            if (e.UninstallString is not { } u) continue;
            var m = Regex.Match(u, @"(?:Launcher|uninstall)\.exe.+-uninstall=([A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups[1].Value.Equals("launcher", StringComparison.OrdinalIgnoreCase)) continue;
            if (ById(m.Groups[1].Value) is not { } title)
            {
                Log.Info($"Rockstar: no game known for title id {m.Groups[1].Value} ({e.DisplayName})");
                continue;
            }
            if (e.InstallLocation is { } dir && Directory.Exists(dir)) Add(title, dir, e.EstimatedSizeKb ?? 0);
        }

        // Rockstar's own key, for a game whose uninstall entry is missing. Only with the game's exe
        // in the folder it names: an uninstall can leave the key behind.
        if (readRegistry) try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            foreach (var t in Titles.Where(t => t.RegKey is not null && !games.ContainsKey(IdPrefix + t.Id)))
            {
                using var k = root.OpenSubKey(@"SOFTWARE\" + t.RegKey);
                if (UninstallEntries.Clean(k?.GetValue("InstallFolder") as string) is { } dir
                    && Directory.Exists(dir) && File.Exists(Path.Combine(dir, t.Exe)))
                    Add(t, dir, 0);
            }
        }
        catch (Exception ex) { Log.Info($"Rockstar: registry keys not read ({ex.Message})"); }
        return games.Values.ToList();
    }
}
