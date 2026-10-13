namespace Loungepad.Models;

/// <summary>
/// The Power Wheel's Shortcuts: the user's own wheels of things to open. Each tab is one wheel
/// (LB/RB turn between them on the Power Wheel); its shortcuts sit round the ring in their order,
/// spread evenly, the way the action wheel's spokes are. Settings → Shortcuts arranges them.
///
/// The whole layout lives in shortcuts.json and nothing else ever writes it: unlike the action
/// packs there is nothing shipped to merge over, because the arrangement is the point. A file that
/// is not there yet is seeded with <see cref="ShortcutCatalog.Defaults"/>, which is the list the
/// wheel had before it could be changed.
/// </summary>
public class ShortcutsFileData
{
    public int Version { get; set; } = 1;
    public List<ShortcutTab> Tabs { get; set; } = new();
}

public class ShortcutTab
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ShortcutDef> Items { get; set; } = new();

    public ShortcutTab Clone() => new() { Id = Id, Name = Name, Items = Items.Select(i => i.Clone()).ToList() };
}

public class ShortcutDef
{
    /// <summary>Unique across every tab, so a shortcut can be found, moved and bound by id alone.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>One of <see cref="ShortcutKinds"/>.</summary>
    public string Kind { get; set; } = "";
    /// <summary>
    /// What it opens, by kind: a <see cref="ShortcutCatalog"/> id (builtin), a Start menu app's id
    /// -- a packaged app's AUMID or the shell's id for a desktop app -- (app), an exe (program), a
    /// folder or a file, an http(s) address, a library game's id, or a script or a command line.
    /// </summary>
    public string Target { get; set; } = "";
    /// <summary>Arguments for a program or a command.</summary>
    public string? Args { get; set; }
    /// <summary>The folder a program or a command starts in. Null for the program's own folder.</summary>
    public string? StartIn { get; set; }
    /// <summary>A Start menu app's own exe, when the shell names one: what its window is found by,
    /// and its icon. Never used to start anything.</summary>
    public string? Exe { get; set; }
    /// <summary>A command's console: "hide" runs it with no window at all. Anything else shows it.</summary>
    public string? Console { get; set; }
    /// <summary>Ask before running it. Sign out, restart and shut down always ask whatever this says.</summary>
    public bool Confirm { get; set; }
    /// <summary>A picture the user chose from their PC, drawn instead of the automatic icon: a file
    /// name in shortcut-icons (ShortcutIcons), "ic-…png". Null for the automatic one.</summary>
    public string? IconFile { get; set; }
    /// <summary>A gamepad button or chord that opens it from the desktop, as an action's does.</summary>
    public string? Button { get; set; }

    public ShortcutDef Clone() => (ShortcutDef)MemberwiseClone();
}

public static class ShortcutKinds
{
    public const string Builtin = "builtin";
    public const string App = "app";
    public const string Program = "program";
    public const string Folder = "folder";
    public const string File = "file";
    public const string Url = "url";
    public const string Game = "game";
    public const string Command = "command";

    public static readonly string[] All = { Builtin, App, Program, Folder, File, Url, Game, Command };
}

/// <summary>
/// The shortcuts the launcher knows how to open itself: Windows' own windows and pages, and the
/// power actions. The page draws its pickers from this list (it rides in the shortcuts message),
/// so the host is the one place a built-in is defined.
/// </summary>
public static class ShortcutCatalog
{
    /// <param name="Group">"windows" or "power", the two lists the page offers them in.</param>
    /// <param name="Uri">What is started: an ms-settings page or an exe. Null for the ones the host
    /// answers itself (lock, rest, sleep, the power actions).</param>
    /// <param name="Match">The program whose window comes up, for bringing it forward.</param>
    /// <param name="Confirm">Always asks first, whatever the shortcut's own switch says.</param>
    public sealed record Builtin(string Id, string Name, string Icon, string Group, string Desc,
        string? Uri, string[] Match, bool Confirm = false, bool Danger = false);

    public static readonly Builtin[] All =
    {
        new("explorer", "File Explorer", "folder", "windows", "Your files and drives", "explorer.exe", new[] { "explorer" }),
        new("settings", "Windows Settings", "gear", "windows", "Every setting Windows has", "ms-settings:", new[] { "systemsettings" }),
        new("displaySettings", "Display Settings", "monitor", "windows", "Resolution, HDR, scaling and which screen is which", "ms-settings:display", new[] { "systemsettings" }),
        new("soundSettings", "Sound Settings", "volume", "windows", "Which speakers and microphone are in use", "ms-settings:sound", new[] { "systemsettings" }),
        new("bluetoothSettings", "Bluetooth & devices", "gamepad", "windows", "Pair a controller, headphones or a keyboard", "ms-settings:bluetooth", new[] { "systemsettings" }),
        new("networkSettings", "Network & internet", "globe", "windows", "Wi-Fi, Ethernet and VPN", "ms-settings:network", new[] { "systemsettings" }),
        new("windowsUpdate", "Windows Update", "refresh", "windows", "Check for and install updates", "ms-settings:windowsupdate", new[] { "systemsettings" }),
        new("volume", "Volume Mixer", "volume", "windows", "Each program's own volume", "sndvol.exe", new[] { "sndvol" }),
        // Distinct from Rest, which only blanks the screen: this hands the session to the Windows
        // lock screen, which the pad cannot drive at all.
        new("lock", "Lock PC", "lock", "power", "Show the Windows lock screen", null, Array.Empty<string>()),
        new("rest", "Rest", "moon", "power", "Screen off and the game paused. Any button brings both back", null, Array.Empty<string>()),
        // Rest first (the game frozen, the pad parked), then Windows' own sleep. What can wake the
        // PC afterwards is in Settings → General → Rest and sleep.
        new("sleepPc", "Sleep PC", "sleep", "power", "Rest, then put the PC to sleep", null, Array.Empty<string>()),
        new("signOut", "Sign out", "signOut", "power", "Close everything and sign out of Windows", null, Array.Empty<string>(), Confirm: true, Danger: true),
        new("restart", "Restart PC", "restart", "power", "Close everything and restart the PC", null, Array.Empty<string>(), Confirm: true, Danger: true),
        new("shutdown", "Shut down", "power", "power", "Close everything and turn the PC off", null, Array.Empty<string>(), Confirm: true, Danger: true),
    };

    public static Builtin? Find(string? id) => All.FirstOrDefault(b => b.Id == id);

    /// <summary>What the wheel held before it could be changed, in the same order: a new install,
    /// or "Restore default shortcuts", starts from exactly this.</summary>
    public static List<ShortcutTab> Defaults() => new()
    {
        new ShortcutTab
        {
            Id = "system", Name = "System",
            Items = new[] { "explorer", "settings", "displaySettings", "volume", "lock", "sleepPc" }
                .Select(id => new ShortcutDef { Id = "d-" + id.ToLowerInvariant(), Name = Find(id)!.Name, Kind = ShortcutKinds.Builtin, Target = id })
                .ToList(),
        },
    };
}
