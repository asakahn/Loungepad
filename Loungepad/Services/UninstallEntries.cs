using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// The programs Windows lists under Apps → Installed apps, read from the three uninstall keys. It is
/// where Battle.net and the EA app record each game they install (Battle.net's entry names the
/// game's uid on its uninstaller's command line; EA's points at the game's folder), so it is the
/// cheapest reliable answer to "which of their games are on this PC". Read fresh on every call.
/// </summary>
internal static class UninstallEntries
{
    public sealed record Entry(string Key, string? DisplayName, string? Publisher, string? UninstallString,
        string? InstallLocation, string? DisplayIcon, long? EstimatedSizeKb);

    private static readonly (RegistryKey Hive, string Path)[] Roots =
    {
        (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
    };

    public static List<Entry> All()
    {
        var entries = new List<Entry>();
        foreach (var (hive, path) in Roots)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        if (sub is null) continue;
                        entries.Add(new Entry(name,
                            sub.GetValue("DisplayName") as string,
                            sub.GetValue("Publisher") as string,
                            sub.GetValue("UninstallString") as string,
                            Clean(sub.GetValue("InstallLocation") as string),
                            sub.GetValue("DisplayIcon") as string,
                            sub.GetValue("EstimatedSize") is int kb && kb > 0 ? kb : null));
                    }
                    catch { /* one unreadable entry */ }
                }
            }
            catch { /* a hive this account cannot read */ }
        }
        return entries;
    }

    /// <summary>A folder as the installers write it: quoted sometimes, forward slashes sometimes, a
    /// trailing separator usually. Null for an empty value.</summary>
    public static string? Clean(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = folder.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        return f.Length == 0 ? null : f;
    }

    /// <summary>The exe a DisplayIcon value names ("C:\x\game.exe",0 → C:\x\game.exe), or null.</summary>
    public static string? IconExe(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var exe = displayIcon.Split(',')[0].Trim().Trim('"');
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }
}
