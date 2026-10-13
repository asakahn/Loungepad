using System.Text.Json.Serialization;
using Loungepad.Interop;

namespace Loungepad.Models;

/// <summary>
/// One program's actions: a keyboard shortcut the program understands, given a name, an optional
/// gamepad button that fires it from anywhere in that program, and a place on the action wheel.
///
/// The wheel is context aware: which app's actions it shows is decided by the window that was in
/// front when the Power Wheel opened, matched on the process's exe name. "Everywhere" is the one
/// app that matches every window and holds what is true of Windows itself -- volume, media keys,
/// Alt+Tab. An app's actions sit on tabs of up to <see cref="ActionPacks.MaxItems"/>, each tab a
/// wheel of its own (LB/RB turn between them), the app's tabs first and Everywhere's after.
///
/// Bundled packs (<see cref="ActionPacks"/>) are the starting point for the programs we know, and
/// the file only ever holds what differs from the pack (<see cref="ActionAppFile"/>), so a new
/// default arrives on the next build without overwriting anything the user set. This class is the
/// resolved app, pack and file merged, as the page and the pad see it.
/// </summary>
public class ActionApp
{
    /// <summary>The primary exe name, lower case, without ".exe" -- "firefox". Custom apps use
    /// theirs too, so an app added by hand that turns out to be one we have a pack for becomes
    /// that pack rather than a second copy of it.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Every exe name the app runs as, lower case, without ".exe".</summary>
    public List<string> Exes { get; set; } = new();
    /// <summary>The program's path when known, for its icon. Never used to start anything.</summary>
    public string? Path { get; set; }
    /// <summary>Added by the user rather than shipped as a pack.</summary>
    public bool Custom { get; set; }
    /// <summary>A pack the user added by hand: shown whether or not detection finds the program.</summary>
    public bool Pinned { get; set; }
    /// <summary>The wheel's tabs, in order; their actions in the order they go round the ring.</summary>
    public List<ActionTab> Tabs { get; set; } = new();
    /// <summary>The pack's actions that are on none of the tabs: taken off by the user, and offered
    /// again when one is added. Always empty for a custom app.</summary>
    public List<ActionDef> Spare { get; set; } = new();

    /// <summary>Every action on the wheel, tab by tab: what the buttons and the wheel fire.</summary>
    public IEnumerable<ActionDef> Actions => Tabs.SelectMany(t => t.Actions);

    public ActionApp Clone() => new()
    {
        Id = Id, Name = Name, Exes = new List<string>(Exes), Path = Path, Custom = Custom, Pinned = Pinned,
        Tabs = Tabs.Select(t => t.Clone()).ToList(), Spare = Spare.Select(a => a.Clone()).ToList(),
    };
}

public class ActionTab
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ActionDef> Actions { get; set; } = new();

    public ActionTab Clone() => new() { Id = Id, Name = Name, Actions = Actions.Select(a => a.Clone()).ToList() };
}

public class ActionDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>The shortcut, as "Ctrl+Shift+T": zero or more of Ctrl, Shift, Alt, Win, then one
    /// key by the names <see cref="ShortcutKeys"/> knows. Empty means the action has no shortcut
    /// yet and cannot fire.</summary>
    public string Keys { get; set; } = "";
    /// <summary>A gamepad button or combo -- "Y", "LB + Y" -- that fires this action while the app
    /// is in front and the launcher is not. Null or empty for none.</summary>
    public string? Button { get; set; }
    /// <summary>What it does, in a sentence or two, for someone still learning the shortcut. The
    /// packs' own are shipped; a changed pack action with none keeps the pack's.</summary>
    public string? Desc { get; set; }
    /// <summary>Added by the user rather than shipped in the pack.</summary>
    public bool Custom { get; set; }
    /// <summary>Drawn red on the wheel: closes or quits something.</summary>
    public bool Danger { get; set; }
    /// <summary>A pack's action the user has changed, so the edit sheet can offer the pack's own
    /// back. Worked out on every load, never stored.</summary>
    [JsonIgnore] public bool Edited { get; set; }
    /// <summary>Read, never written: version 1 of actions.json could keep an action off the wheel.
    /// <see cref="Services.ActionService"/> turns that into a removal when it loads such a file
    /// (unless the action has a button, which has to keep working) and drops the flag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Hidden { get; set; }

    public ActionDef Clone() => new()
    {
        Id = Id, Name = Name, Keys = Keys, Button = Button, Desc = Desc, Custom = Custom, Danger = Danger, Edited = Edited,
    };
}

/// <summary>What actions.json holds: only what differs from the packs, plus the user's own apps.</summary>
public class ActionsFileData
{
    /// <summary>1: one list per app, with actions that could be kept off the wheel. 2: tabs.</summary>
    public int Version { get; set; } = 2;
    public List<ActionAppFile> Apps { get; set; } = new();
}

/// <summary>
/// One app's entry in actions.json. For a pack it is only the difference: the actions the user
/// changed (whole copies, by id) and added, the ones taken off the wheel, and the layout once the
/// user has arranged it -- until then the pack's own tabs are used, so a build that arranges them
/// better reaches everyone who never did. A custom app keeps everything here.
/// </summary>
public class ActionAppFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Exes { get; set; } = new();
    public string? Path { get; set; }
    public bool Pinned { get; set; }
    /// <summary>Changed pack actions and the user's own, whole.</summary>
    public List<ActionDef> Actions { get; set; } = new();
    /// <summary>The tabs as the user arranged them, by action id; null while they are the pack's.</summary>
    public List<ActionTabLayout>? Tabs { get; set; }
    /// <summary>Pack actions taken off the wheel, so that a load does not put them back as new ones.</summary>
    public List<string> Removed { get; set; } = new();

    /// <summary>Nothing but the pin and the path: the pack exactly as shipped.</summary>
    [JsonIgnore] public bool Untouched => Actions.Count == 0 && Tabs is null && Removed.Count == 0;
}

public class ActionTabLayout
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = new();
}

/// <summary>
/// A keyboard shortcut as text, and how it is sent.
///
/// "Ctrl+Shift+T": the modifiers by name, then one key. Letters and digits are their own virtual
/// keys on every layout; a punctuation character is looked up on the current layout at send time
/// (VkKeyScanW), so "Ctrl+=" zooms a browser on AZERTY too; everything else is a named key from
/// the table below. Sent through SendKeyCombo, which carries scan codes as well as virtual keys,
/// so a program reading raw input sees the same press a real keyboard would make.
/// </summary>
public static class ShortcutKeys
{
    public readonly record struct Parsed(ushort Vk, bool Extended, ushort[] Modifiers, string Key, bool SystemWide);

    /// <summary>Named keys: the virtual key and whether it is an extended key (E0-prefixed scan code).</summary>
    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = (NativeMethods.VK_RETURN, false), ["Tab"] = (NativeMethods.VK_TAB, false),
        ["Esc"] = (NativeMethods.VK_ESCAPE, false), ["Escape"] = (NativeMethods.VK_ESCAPE, false),
        ["Space"] = (NativeMethods.VK_SPACE, false), ["Backspace"] = (NativeMethods.VK_BACK, false),
        ["Delete"] = (NativeMethods.VK_DELETE, true), ["Insert"] = (NativeMethods.VK_INSERT, true),
        ["Home"] = (NativeMethods.VK_HOME, true), ["End"] = (NativeMethods.VK_END, true),
        ["PageUp"] = (NativeMethods.VK_PRIOR, true), ["PageDown"] = (NativeMethods.VK_NEXT, true),
        ["Up"] = (NativeMethods.VK_UP, true), ["Down"] = (NativeMethods.VK_DOWN, true),
        ["Left"] = (NativeMethods.VK_LEFT, true), ["Right"] = (NativeMethods.VK_RIGHT, true),
        ["CapsLock"] = (NativeMethods.VK_CAPITAL, false), ["NumLock"] = (NativeMethods.VK_NUMLOCK, true),
        ["ScrollLock"] = (NativeMethods.VK_SCROLL, false), ["Pause"] = (NativeMethods.VK_PAUSE, false),
        ["PrintScreen"] = (NativeMethods.VK_SNAPSHOT, true), ["Apps"] = (NativeMethods.VK_APPS, true),
        ["NumAdd"] = (NativeMethods.VK_ADD, false), ["NumSubtract"] = (NativeMethods.VK_SUBTRACT, false),
        ["NumMultiply"] = (NativeMethods.VK_MULTIPLY, false), ["NumDivide"] = (NativeMethods.VK_DIVIDE, true),
        ["NumDecimal"] = (NativeMethods.VK_DECIMAL, false), ["NumEnter"] = (NativeMethods.VK_RETURN, true),
        ["VolumeUp"] = (NativeMethods.VK_VOLUME_UP, true), ["VolumeDown"] = (NativeMethods.VK_VOLUME_DOWN, true),
        ["VolumeMute"] = (NativeMethods.VK_VOLUME_MUTE, true),
        ["MediaPlayPause"] = (NativeMethods.VK_MEDIA_PLAY_PAUSE, true), ["MediaNext"] = (NativeMethods.VK_MEDIA_NEXT_TRACK, true),
        ["MediaPrev"] = (NativeMethods.VK_MEDIA_PREV_TRACK, true), ["MediaStop"] = (NativeMethods.VK_MEDIA_STOP, true),
        ["BrowserBack"] = (NativeMethods.VK_BROWSER_BACK, true), ["BrowserForward"] = (NativeMethods.VK_BROWSER_FORWARD, true),
        ["BrowserRefresh"] = (NativeMethods.VK_BROWSER_REFRESH, true), ["BrowserHome"] = (NativeMethods.VK_BROWSER_HOME, true),
        ["BrowserSearch"] = (NativeMethods.VK_BROWSER_SEARCH, true), ["BrowserFavorites"] = (NativeMethods.VK_BROWSER_FAVORITES, true),
    };

    /// <summary>Keys that act on the system rather than on the window in front, so they can be
    /// sent with nothing in front of the launcher. A Win chord is the other kind.</summary>
    private static readonly HashSet<string> SystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "VolumeUp", "VolumeDown", "VolumeMute", "MediaPlayPause", "MediaNext", "MediaPrev", "MediaStop",
    };

    /// <summary>Split "Ctrl+Shift+T" into its parts. The key is the last part; "+" on its own is a key.</summary>
    public static List<string> Parts(string keys)
    {
        var list = new List<string>();
        var s = (keys ?? "").Trim();
        if (s.Length == 0) return list;
        int i = 0;
        while (i < s.Length)
        {
            int j = s.IndexOf('+', i);
            // A "+" that is the key itself: at the end, or followed by nothing but whitespace.
            if (j < 0 || j == s.Length - 1) { list.Add(s[i..].Trim()); break; }
            var part = s[i..j].Trim();
            if (part.Length > 0) list.Add(part);
            else list.Add("+");
            i = j + 1;
        }
        return list.Where(p => p.Length > 0).ToList();
    }

    public static bool IsModifier(string part) =>
        part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Shift", StringComparison.OrdinalIgnoreCase)
        || part.Equals("Alt", StringComparison.OrdinalIgnoreCase) || part.Equals("Win", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the text is not a shortcut we can send.</summary>
    public static Parsed? Parse(string keys)
    {
        var parts = Parts(keys);
        if (parts.Count == 0) return null;
        var mods = new List<ushort>();
        bool win = false;
        for (int i = 0; i < parts.Count - 1; i++)
        {
            var p = parts[i];
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_CONTROL);
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_SHIFT);
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods.Add(NativeMethods.VK_MENU);
            else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) { mods.Add(NativeMethods.VK_LWIN); win = true; }
            else return null;
        }
        var key = parts[^1];
        if (IsModifier(key)) return null;

        ushort vk; bool ext = false;
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') vk = c;
            else
            {
                // Punctuation goes by the layout in use. The high byte says which modifiers type
                // the character; Shift is added when it is needed and not already held, so "Ctrl++"
                // on a layout where + is Shift+= still sends Ctrl+Shift+=.
                short scan = NativeMethods.VkKeyScanW(key[0]);
                if (scan == -1) return null;
                vk = (ushort)(scan & 0xFF);
                if ((scan & 0x100) != 0 && !mods.Contains(NativeMethods.VK_SHIFT)) mods.Add(NativeMethods.VK_SHIFT);
            }
        }
        else if (key.Length is 2 or 3 && (key[0] == 'F' || key[0] == 'f') && int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
            vk = (ushort)(NativeMethods.VK_F1 + fn - 1);
        else if (key.Length == 4 && key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[3]))
            vk = (ushort)(NativeMethods.VK_NUMPAD0 + (key[3] - '0'));
        else if (Named.TryGetValue(key, out var named)) { vk = named.Vk; ext = named.Ext; }
        else return null;

        return new Parsed(vk, ext, mods.ToArray(), key, win || SystemKeys.Contains(key));
    }

    public static bool IsValid(string keys) => Parse(keys) is not null;

    /// <summary>A key as the page writes it, whatever was typed: "t" is "T", "pagedown" is
    /// "PageDown", "f5" is "F5".</summary>
    public static string CanonicalKey(string key)
    {
        if (key.Length == 1) return key.ToUpperInvariant();
        if (key.Length is 2 or 3 && (key[0] == 'f' || key[0] == 'F') && key[1..].All(char.IsDigit)) return "F" + key[1..];
        if (key.Length == 4 && key.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[3])) return "Num" + key[3];
        return Named.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
    }

    /// <summary>True for a shortcut that needs no particular window in front: media keys, and any
    /// Win chord, which the shell takes whoever has the focus.</summary>
    public static bool IsSystemWide(string keys) => Parse(keys)?.SystemWide ?? false;

    /// <summary>Press it. False when the text is not a shortcut.</summary>
    public static bool Send(string keys)
    {
        if (Parse(keys) is not { } p) return false;
        NativeMethods.SendKeyCombo(p.Vk, p.Extended, p.Modifiers);
        return true;
    }
}

/// <summary>
/// The packs: what we know the common programs answer to, laid out on tabs. Every shortcut here is
/// the program's own default binding; a button is only pre-assigned where the reach is obvious and
/// the button is free on the desktop (A and B click, View raises the keyboard, RT boosts the
/// pointer). The first tab is what a hand reaches for most; the rest are there to be found. Each
/// action says in a sentence what it does, for anyone still learning the shortcuts: it is drawn
/// in the wheel's centre and on the Settings card, so it stays short enough for two lines there.
/// </summary>
public static class ActionPacks
{
    public const string EverywhereId = "everywhere";
    /// <summary>Spokes on one tab: at eight they sit a comfortable push of the stick apart.</summary>
    public const int MaxItems = 8;
    public const int MaxTabs = 12;

    private static ActionDef A(string id, string name, string keys, string desc, string? button = null, bool danger = false) =>
        new() { Id = id, Name = name, Keys = keys, Desc = desc, Button = button, Danger = danger };

    private static ActionTab T(string id, string name, params ActionDef[] actions) =>
        new() { Id = id, Name = name, Actions = actions.ToList() };

    private static ActionApp App(string id, string name, string[] exes, params ActionTab[] tabs) =>
        new() { Id = id, Name = name, Exes = exes.ToList(), Tabs = tabs.ToList() };

    /// <summary>The browsers share one layout; only the odd shortcut differs.</summary>
    private static ActionTab[] Browser(string downloads, string privateWindow) => new[]
    {
        T("browse", "Browse",
            A("new-tab", "New tab", "Ctrl+T", "Opens a new tab, ready for an address or a search.", "Y"),
            A("close-tab", "Close tab", "Ctrl+W", "Closes the tab you are on."),
            A("back", "Back", "Alt+Left", "Goes back to the page before this one.", "X"),
            A("forward", "Forward", "Alt+Right", "Goes forward again, after going back."),
            A("reload", "Reload", "F5", "Loads the page again, to see anything new on it."),
            A("address", "Address bar", "Ctrl+L", "Selects the address bar, to type an address or a search."),
            A("find", "Find in page", "Ctrl+F", "Looks for a word or phrase on this page."),
            A("fullscreen", "Full screen", "F11", "Fills the whole screen with the page. Press it again to go back.")),
        T("tabs", "Tabs & windows",
            A("reopen-tab", "Reopen closed tab", "Ctrl+Shift+T", "Brings back the tab you closed last."),
            A("next-tab", "Next tab", "Ctrl+Tab", "Switches to the tab on the right."),
            A("prev-tab", "Previous tab", "Ctrl+Shift+Tab", "Switches to the tab on the left."),
            A("new-window", "New window", "Ctrl+N", "Opens another browser window."),
            A("private", "Private window", privateWindow, "Opens a private window, which forgets its history and sign-ins once closed."),
            A("home", "Home page", "Alt+Home", "Goes to your home page.")),
        T("pages", "Zoom & pages",
            A("zoom-in", "Zoom in", "Ctrl+=", "Makes the page bigger, text and pictures alike."),
            A("zoom-out", "Zoom out", "Ctrl+-", "Makes the page smaller, to fit more on the screen."),
            A("zoom-reset", "Reset zoom", "Ctrl+0", "Puts the page back to its normal size."),
            A("bookmark", "Bookmark this page", "Ctrl+D", "Saves the page as a bookmark, to find it again later."),
            A("history", "History", "Ctrl+H", "Shows the pages you have visited."),
            A("downloads", "Downloads", downloads, "Shows the files you have downloaded.")),
    };

    public static IReadOnlyList<ActionApp> All { get; } = new List<ActionApp>
    {
        App(EverywhereId, "Everywhere", Array.Empty<string>(),
            T("media", "Media",
                A("vol-up", "Volume up", "VolumeUp", "Turns the PC's volume up a step."),
                A("vol-down", "Volume down", "VolumeDown", "Turns the PC's volume down a step."),
                A("mute", "Mute", "VolumeMute", "Mutes the PC's sound, or brings it back."),
                A("play-pause", "Play / pause", "MediaPlayPause", "Plays or pauses whatever is playing: music, a video, a podcast."),
                A("next-track", "Next track", "MediaNext", "Skips to the next track in whatever is playing."),
                A("prev-track", "Previous track", "MediaPrev", "Goes back to the previous track, or the start of this one.")),
            T("windows", "Windows",
                A("show-desktop", "Show desktop", "Win+D", "Moves every window aside to show the desktop. Press it again to bring them back."),
                A("last-window", "Last window", "Alt+Tab", "Switches to the window you were using before this one."),
                A("task-view", "Task view", "Win+Tab", "Shows every open window side by side, to pick one."),
                A("maximize", "Maximize window", "Win+Up", "Makes the window in front fill the screen."),
                A("snap-left", "Snap left", "Win+Left", "Puts the window in front on the left half of the screen."),
                A("snap-right", "Snap right", "Win+Right", "Puts the window in front on the right half of the screen."),
                A("screenshot", "Save a screenshot", "Win+PrintScreen", "Saves a picture of the whole screen in Pictures › Screenshots."),
                A("close-window", "Close window", "Alt+F4", "Closes the window in front, as its X would. It may ask to save first.", danger: true)),
            T("keys", "Keys",
                A("enter", "Enter", "Enter", "Presses Enter: confirms a question, or opens what is selected."),
                A("escape", "Escape", "Esc", "Presses Escape: closes a menu or a question, or cancels."),
                A("copy", "Copy", "Ctrl+C", "Copies what is selected, ready to paste somewhere."),
                A("paste", "Paste", "Ctrl+V", "Puts in what was last copied or cut."),
                A("undo", "Undo", "Ctrl+Z", "Takes back the last change."),
                A("select-all", "Select all", "Ctrl+A", "Selects everything: all the text, or every file in a folder.")),
            // The D-pad as arrow keys on the desktop: menus, lists, a video's seek. The buttons
            // are the point; the spokes say that they are there, laid out the way they point.
            T("arrows", "Arrows",
                A("arrow-up", "Arrow up", "Up", "The Up arrow key: up a step in a list, a menu or a page.", "Up"),
                A("arrow-right", "Arrow right", "Right", "The Right arrow key: the next item, or forward in a video.", "Right"),
                A("arrow-down", "Arrow down", "Down", "The Down arrow key: down a step in a list, a menu or a page.", "Down"),
                A("arrow-left", "Arrow left", "Left", "The Left arrow key: the item before, or back in a video.", "Left"))),

        App("firefox", "Firefox", new[] { "firefox" }, Browser("Ctrl+Shift+Y", "Ctrl+Shift+P")),
        App("chrome", "Google Chrome", new[] { "chrome" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("msedge", "Microsoft Edge", new[] { "msedge" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("brave", "Brave", new[] { "brave" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("opera", "Opera", new[] { "opera" }, Browser("Ctrl+J", "Ctrl+Shift+N")),
        App("vivaldi", "Vivaldi", new[] { "vivaldi" }, Browser("Ctrl+J", "Ctrl+Shift+N")),

        App("vlc", "VLC", new[] { "vlc" },
            T("playback", "Playback",
                A("play-pause", "Play / pause", "Space", "Plays or pauses the video.", "X"),
                A("fullscreen", "Full screen", "F", "Fills the screen with the video, or goes back to the window.", "Y"),
                A("mute", "Mute", "M", "Mutes VLC's sound, or brings it back."),
                A("skip-fwd", "Skip forward 10 s", "Alt+Right", "Jumps ten seconds ahead."),
                A("skip-back", "Skip back 10 s", "Alt+Left", "Jumps ten seconds back."),
                A("next", "Next", "N", "Plays the next item in the playlist."),
                A("prev", "Previous", "P", "Plays the item before in the playlist."),
                A("stop", "Stop", "S", "Stops playing and goes back to the start.")),
            T("tracks", "Tracks & speed",
                A("subtitles", "Subtitle track", "V", "Switches to the next subtitles, or turns them off."),
                A("audio", "Audio track", "B", "Switches to the next audio track, such as another language."),
                A("faster", "Faster", "]", "Plays a little faster."),
                A("slower", "Slower", "[", "Plays a little slower."),
                A("speed-normal", "Normal speed", "=", "Back to normal speed."),
                A("playlist", "Playlist", "Ctrl+L", "Shows or hides the playlist.")),
            T("more", "More",
                A("vol-up", "Volume up", "Ctrl+Up", "Turns VLC's own volume up."),
                A("vol-down", "Volume down", "Ctrl+Down", "Turns VLC's own volume down."),
                A("open", "Open file", "Ctrl+O", "Picks a file to play."),
                A("quit", "Quit VLC", "Ctrl+Q", "Closes VLC.", danger: true))),

        App("spotify", "Spotify", new[] { "spotify" },
            T("playback", "Playback",
                A("play-pause", "Play / pause", "Space", "Plays or pauses the music.", "X"),
                A("next", "Next track", "Ctrl+Right", "Skips to the next song.", "Y"),
                A("prev", "Previous track", "Ctrl+Left", "Goes back to the song before."),
                A("vol-up", "Volume up", "Ctrl+Up", "Turns Spotify's own volume up."),
                A("vol-down", "Volume down", "Ctrl+Down", "Turns Spotify's own volume down."),
                A("mute", "Mute", "Ctrl+Shift+Down", "Turns Spotify's volume all the way down."),
                A("shuffle", "Shuffle", "Ctrl+S", "Turns shuffle on or off."),
                A("repeat", "Repeat", "Ctrl+R", "Steps through repeat: off, the whole list, this song.")),
            T("browse", "Browse",
                A("search", "Search", "Ctrl+L", "Opens search, to find a song, an artist or a podcast."),
                A("like", "Like this song", "Alt+Shift+B", "Saves the song playing to your Liked Songs."),
                A("home", "Home", "Alt+Shift+H", "Goes to Spotify's home page."),
                A("back", "Back", "Alt+Left", "Goes back to the page before."),
                A("forward", "Forward", "Alt+Right", "Goes forward again, after going back."))),

        App("discord", "Discord", new[] { "discord" },
            T("voice", "Voice & channels",
                A("mute", "Mute microphone", "Ctrl+Shift+M", "Mutes or unmutes your microphone in a voice call.", "X"),
                A("deafen", "Deafen", "Ctrl+Shift+D", "Stops you hearing the call and mutes you too. Press it again to come back.", "Y"),
                A("back", "Back", "Alt+Left", "Goes back to where you were before."),
                A("forward", "Forward", "Alt+Right", "Goes forward again, after going back."),
                A("next-channel", "Next channel", "Alt+Down", "Moves to the channel below in the server."),
                A("prev-channel", "Previous channel", "Alt+Up", "Moves to the channel above in the server."),
                A("next-unread", "Next unread", "Alt+Shift+Down", "Jumps to the next channel with unread messages."),
                A("prev-unread", "Previous unread", "Alt+Shift+Up", "Jumps to the previous channel with unread messages.")),
            T("more", "More",
                A("search", "Search", "Ctrl+K", "Opens the quick switcher, to jump to any server, channel or friend."),
                A("mark-read", "Mark channel read", "Esc", "Marks every message in this channel as read."),
                A("upload", "Upload a file", "Ctrl+Shift+U", "Picks a file to send in this channel."),
                A("emoji", "Emoji picker", "Ctrl+E", "Opens the emoji picker."),
                A("pins", "Pinned messages", "Ctrl+P", "Shows this channel's pinned messages."))),

        App("explorer", "File Explorer", new[] { "explorer" },
            T("navigate", "Navigate",
                A("back", "Back", "Alt+Left", "Goes back to the folder you were in before.", "X"),
                A("forward", "Forward", "Alt+Right", "Goes forward again, after going back."),
                A("up", "Up a folder", "Alt+Up", "Goes up to the folder this one is in.", "Y"),
                A("open", "Open", "Enter", "Opens the file or folder that is selected."),
                A("address", "Address bar", "Alt+D", "Selects the address bar, to type where to go."),
                A("search", "Search", "Ctrl+E", "Searches this folder and every folder in it."),
                A("refresh", "Refresh", "F5", "Updates the list of files.")),
            T("files", "Files",
                A("new-folder", "New folder", "Ctrl+Shift+N", "Makes a new folder here."),
                A("rename", "Rename", "F2", "Renames the file or folder that is selected."),
                A("delete", "Delete", "Delete", "Moves what is selected to the Recycle Bin."),
                A("cut", "Cut", "Ctrl+X", "Takes what is selected, to paste it somewhere else."),
                A("copy", "Copy", "Ctrl+C", "Copies what is selected, to paste somewhere else."),
                A("paste", "Paste", "Ctrl+V", "Puts what was cut or copied into this folder."),
                A("select-all", "Select all", "Ctrl+A", "Selects every file and folder here."),
                A("properties", "Properties", "Alt+Enter", "Shows the size, dates and other details of what is selected.")),
            T("window", "Window",
                A("new-window", "New window", "Ctrl+N", "Opens another File Explorer window."),
                A("close", "Close window", "Ctrl+W", "Closes this File Explorer window.", danger: true))),

        App("retroarch", "RetroArch", new[] { "retroarch" },
            T("play", "Play",
                A("menu", "Menu", "F1", "Opens RetroArch's quick menu over the game.", "Y"),
                A("save-state", "Save state", "F2", "Saves the game exactly where you are, in the current slot.", "X"),
                A("load-state", "Load state", "F4", "Goes back to the moment saved in the current slot."),
                A("slot-next", "Next state slot", "F7", "Moves on to the next save slot."),
                A("slot-prev", "Previous state slot", "F6", "Moves back to the save slot before."),
                A("fast-forward", "Fast forward", "Space", "Runs the game fast. Press it again for normal speed."),
                A("rewind", "Rewind", "R", "Winds the game back a moment, when rewind is on in RetroArch's settings."),
                A("pause", "Pause", "P", "Pauses the game, or carries on.")),
            T("more", "More",
                A("screenshot", "Screenshot", "F8", "Saves a picture of the game."),
                A("fullscreen", "Full screen", "F", "Switches between full screen and a window."),
                A("mute", "Mute", "F9", "Mutes the game's sound, or brings it back."),
                A("reset", "Reset game", "H", "Restarts the game from the beginning. Anything unsaved is lost.", danger: true),
                A("quit", "Quit RetroArch", "Esc", "Closes RetroArch. It may want the press twice.", danger: true))),

        App("pcsx2", "PCSX2", new[] { "pcsx2-qt", "pcsx2-qtx64", "pcsx2-qtx64-avx2", "pcsx2" },
            T("play", "Play",
                A("save-state", "Save state", "F1", "Saves the game exactly where you are, in the current slot.", "X"),
                A("load-state", "Load state", "F3", "Goes back to the moment saved in the current slot.", "Y"),
                A("slot-next", "Next state slot", "F2", "Moves on to the next save slot."),
                A("slot-prev", "Previous state slot", "Shift+F2", "Moves back to the save slot before."),
                A("pause", "Pause menu", "Esc", "Pauses the game and opens PCSX2's menu."),
                A("turbo", "Turbo", "Tab", "Runs the game fast. Press it again for normal speed.")),
            T("view", "View",
                A("screenshot", "Screenshot", "F8", "Saves a picture of the game."),
                A("fullscreen", "Full screen", "Alt+Enter", "Switches between full screen and a window."),
                A("frame-limit", "Frame limiter", "F4", "Turns the speed limit off and on. Off runs the game as fast as the PC can."))),

        App("dolphin", "Dolphin", new[] { "dolphin" },
            T("states", "States",
                A("play-pause", "Play / pause", "F10", "Pauses the game, or carries on.", "X"),
                A("save-1", "Save state 1", "Shift+F1", "Saves the game exactly where you are, in slot 1.", "Y"),
                A("load-1", "Load state 1", "F1", "Goes back to the moment saved in slot 1."),
                A("save-2", "Save state 2", "Shift+F2", "Saves the game exactly where you are, in slot 2."),
                A("load-2", "Load state 2", "F2", "Goes back to the moment saved in slot 2."),
                A("save-3", "Save state 3", "Shift+F3", "Saves the game exactly where you are, in slot 3."),
                A("load-3", "Load state 3", "F3", "Goes back to the moment saved in slot 3.")),
            T("more", "More",
                A("screenshot", "Screenshot", "F9", "Saves a picture of the game."),
                A("fullscreen", "Full screen", "Alt+Enter", "Switches between full screen and a window."),
                A("stop", "Stop", "Esc", "Stops the game and goes back to Dolphin's list. Anything unsaved is lost.", danger: true))),
    };

    public static ActionApp? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>The pack an exe name belongs to, if any. "chrome" → Google Chrome.</summary>
    public static ActionApp? ForExe(string exe) =>
        All.FirstOrDefault(a => a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase));
}
