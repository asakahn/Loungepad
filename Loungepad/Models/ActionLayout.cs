using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Loungepad.Services;

namespace Loungepad.Models;

/// <summary>
/// One program's wheel as it travels between apps, files and the community catalogue
/// (docs/ACTION-SHARING.md). Self-contained: every tab and action written out, not the difference
/// from a pack that actions.json keeps, so it reads the same whatever packs the reading launcher
/// has. No ids (they are made for the app it goes into), no paths, and no danger flag: the
/// launcher decides that from the chord.
/// </summary>
public class ActionLayout
{
    public string Format { get; set; } = ActionLayouts.Format;
    public ActionLayoutApp App { get; set; } = new();
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public string? Author { get; set; }
    public string? Version { get; set; }
    public List<ActionLayoutTab> Tabs { get; set; } = new();
}

public class ActionLayoutApp
{
    public string Name { get; set; } = "";
    /// <summary>Lower case, without ".exe", as <see cref="ActionService.ExeName"/> writes them.</summary>
    public List<string> Exes { get; set; } = new();
    /// <summary>The program's own name from its exe's version information, when known.</summary>
    public string? Product { get; set; }
}

public class ActionLayoutTab
{
    public string Name { get; set; } = "";
    public List<ActionLayoutAction> Actions { get; set; } = new();
}

public class ActionLayoutAction
{
    public string Name { get; set; } = "";
    public string Keys { get; set; } = "";
    public string? Desc { get; set; }
    public string? Button { get; set; }
}

public static class ActionLayouts
{
    public const string Format = "loungepad-actions/1";
    private const string FormatName = "loungepad-actions/";
    private const int FormatMajor = 1;

    public const int MaxName = 48, MaxTabName = 24, MaxDesc = 120;
    public const int MaxTitle = 48, MaxSummary = 120, MaxAuthor = 40, MaxProduct = 64, MaxExes = 8, MaxExe = 64;
    /// <summary>A whole wheel with every description at its longest is about 20 KB.</summary>
    public const int MaxFileBytes = 64 * 1024;
    public const string FileExtension = ".actions.json";

    /// <summary>The file's own spelling: camelCase, nothing written for an empty field, and text as
    /// itself ("Tabs & windows", not "Tabs & windows"): it is read in editors and on GitHub, and
    /// it is never put into a page as it is.</summary>
    private static readonly JsonSerializerOptions FileOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 16,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A layout as its file's text. Clean it first: what is written is what an import reads.</summary>
    public static string ToJson(ActionLayout layout) => JsonSerializer.Serialize(layout, FileOpts);

    /// <summary>
    /// A file's bytes as a layout, not yet cleaned. Refuses, with a reason a person can read, a file
    /// over <see cref="MaxFileBytes"/>, anything that is not JSON, and JSON that does not say it is
    /// a layout of actions: a file with no <c>format</c> is somebody else's, however close its shape.
    /// </summary>
    public static ActionLayout Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxFileBytes) throw new InvalidDataException("It is larger than a layout of actions can be");
        if (bytes.StartsWith("﻿"u8)) bytes = bytes[3..];
        try
        {
            var node = JsonNode.Parse(bytes, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node is not JsonObject obj || obj["format"] is not JsonValue v || !v.TryGetValue<string>(out _))
                throw new InvalidDataException("It is not a layout of actions");
            return obj.Deserialize<ActionLayout>(FileOpts) ?? throw new InvalidDataException("There is nothing in it");
        }
        catch (JsonException) { throw new InvalidDataException("It is not a layout of actions"); }
        catch (InvalidOperationException) { throw new InvalidDataException("It is not a layout of actions"); }
    }

    /// <summary>A file chosen on this PC, read with the same limit.</summary>
    public static ActionLayout ReadFile(string path)
    {
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidDataException("It is larger than a layout of actions can be");
        return Parse(File.ReadAllBytes(path));
    }

    /// <summary>"Firefox" → "Firefox.actions.json", with anything a file name cannot hold left out.</summary>
    public static string FileName(string appName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((appName ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return (s.Length > 0 ? s : "Actions") + FileExtension;
    }

    /// <summary>An app's wheel, or one tab of it, as a layout: what a copy carries. Empty tabs
    /// are left out; the app's path and the ids stay behind.</summary>
    public static ActionLayout FromApp(ActionApp app, string? tabId = null) => new()
    {
        App = new ActionLayoutApp { Name = app.Name, Exes = new List<string>(app.Exes) },
        Tabs = app.Tabs.Where(t => (tabId is null || t.Id == tabId) && t.Actions.Count > 0)
            .Select(t => new ActionLayoutTab
            {
                Name = t.Name,
                Actions = t.Actions.Select(a => new ActionLayoutAction { Name = a.Name, Keys = a.Keys, Desc = a.Desc, Button = a.Button }).ToList(),
            }).ToList(),
    };

    /// <summary>
    /// The layout as it may be imported: every string through <see cref="Text"/> and its limit,
    /// every chord parsed and spelt the launcher's way, every button in its names, and no more tabs
    /// or actions than a wheel holds. What cannot be kept is dropped and said in
    /// <paramref name="notes"/>. Throws when the format is not one this launcher reads, or when
    /// nothing usable is left.
    /// </summary>
    public static ActionLayout Clean(ActionLayout? raw, List<string> notes)
    {
        if (raw is null) throw new InvalidDataException("There is nothing in it");
        if (!KnownFormat(raw.Format))
            throw new InvalidDataException("It is from a newer Loungepad, or is not a layout of actions at all");

        var exes = (raw.App?.Exes ?? new()).Where(e => e is not null).Select(ActionService.ExeName)
            .Where(ValidExe).Distinct().Take(MaxExes).ToList();
        var layout = new ActionLayout
        {
            App = new ActionLayoutApp { Name = Text(raw.App?.Name, MaxName), Exes = exes, Product = Optional(raw.App?.Product, MaxProduct) },
            Title = Optional(raw.Title, MaxTitle),
            Summary = Optional(raw.Summary, MaxSummary),
            Author = Optional(raw.Author, MaxAuthor),
            Version = Optional(raw.Version, 20),
        };

        int noKeys = 0, overTab = 0, overWheel = 0;
        foreach (var rt in raw.Tabs ?? new())
        {
            if (rt is null) continue;
            var tab = new ActionLayoutTab { Name = Text(rt.Name, MaxTabName) };
            foreach (var ra in rt.Actions ?? new())
            {
                if (ra is null) continue;
                if (!ShortcutKeys.IsValid(ra.Keys ?? "")) { noKeys++; continue; }
                if (tab.Actions.Count >= ActionPacks.MaxItems) { overTab++; continue; }
                var keys = ShortcutKeys.Normalize(ra.Keys!);
                var name = Text(ra.Name, MaxName);
                tab.Actions.Add(new ActionLayoutAction
                {
                    Name = name.Length > 0 ? name : keys,
                    Keys = keys,
                    Desc = Optional(ra.Desc, MaxDesc),
                    Button = ActionService.CleanButton(ra.Button),
                });
            }
            if (tab.Actions.Count == 0) continue;
            if (layout.Tabs.Count >= ActionPacks.MaxTabs) { overWheel += tab.Actions.Count; continue; }
            if (tab.Name.Length == 0) tab.Name = "Tab " + (layout.Tabs.Count + 1);
            layout.Tabs.Add(tab);
        }

        if (noKeys > 0) notes.Add(noKeys == 1 ? "One action has no shortcut Loungepad can send, and is left out"
            : $"{noKeys} actions have no shortcut Loungepad can send, and are left out");
        if (overTab > 0) notes.Add($"A tab holds {ActionPacks.MaxItems}: {Count(overTab)} past that {(overTab == 1 ? "is" : "are")} left out");
        if (overWheel > 0) notes.Add($"An app holds {ActionPacks.MaxTabs} tabs: {Count(overWheel)} on tabs past that {(overWheel == 1 ? "is" : "are")} left out");
        if (layout.Tabs.Count == 0) throw new InvalidDataException("There is nothing in it Loungepad can send");
        return layout;
    }

    private static string Count(int n) => n == 1 ? "one action" : $"{n} actions";

    private static bool KnownFormat(string? format)
    {
        if (format is null || !format.StartsWith(FormatName, StringComparison.Ordinal)) return false;
        var v = format[FormatName.Length..];
        var major = v.Split('.')[0];
        return int.TryParse(major, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m == FormatMajor;
    }

    private static bool ValidExe(string exe) =>
        exe.Length is > 0 and <= MaxExe && exe.All(c => !char.IsControl(c) && "\\/:*?\"<>|".IndexOf(c) < 0);

    private static string? Optional(string? s, int max)
    {
        var t = Text(s, max);
        return t.Length == 0 ? null : t;
    }

    /// <summary>
    /// Text from outside, as it may be kept: no control or format characters (which takes bidi
    /// overrides and zero-width characters with it), no angle brackets, runs of white space as one
    /// space, trimmed, and at most <paramref name="max"/> UTF-16 units, never cut inside a pair.
    /// The page escapes everything it draws anyway; this is so that nothing that reaches it ever
    /// looks like markup, or reads differently from how it is stored.
    /// </summary>
    public static string Text(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 8));
        bool space = false;
        foreach (var r in s.EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator || Rune.IsWhiteSpace(r))
            {
                space = sb.Length > 0;
                continue;
            }
            if (cat is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate) continue;
            if (r.Value is '<' or '>') continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(r.ToString());
            if (sb.Length > max) break;
        }
        var t = sb.ToString();
        if (t.Length > max)
        {
            int cut = max;
            if (char.IsHighSurrogate(t[cut - 1])) cut--;
            t = t[..cut];
        }
        return t.TrimEnd();
    }

    /// <summary>
    /// What makes two layouts the same layout: the programs, then each tab's chords in order, in
    /// their normal form. Names, descriptions and buttons are left out, so rewording a layout does
    /// not make it another one (docs/ACTION-SHARING.md, "Duplicates").
    /// </summary>
    public static string Fingerprint(ActionLayout layout)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", layout.App.Exes.Select(e => e.ToLowerInvariant()).Distinct().OrderBy(e => e, StringComparer.Ordinal)));
        foreach (var t in layout.Tabs)
            sb.Append('\n').Append(string.Join("|", t.Actions.Select(a => ShortcutKeys.Normalize(a.Keys))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    /// <summary>"Zoom & pages" → "zoom-pages": an id made from a name, for what an import adds.</summary>
    public static string Slug(string s, int max = 32)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (sb.Length >= max) break;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
