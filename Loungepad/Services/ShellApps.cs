using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Loungepad.Services;

/// <summary>
/// The Windows shell, for the shortcuts: the Start menu's list of apps (shell:AppsFolder, which
/// is the one list that has Store apps beside desktop programs), an icon for anything the shell
/// can name -- an app, an exe, a folder, a file -- and starting a packaged app in a way that says
/// which process it became.
///
/// Every call here is COM against the shell and belongs on an STA thread; <see cref="Sta"/> runs
/// one for the duration. The list takes a second or two with a few hundred apps, icons a few
/// milliseconds each, so neither ever runs on the UI thread.
/// </summary>
internal static class ShellApps
{
    public sealed record StartApp(string Id, string Name, string? Exe, bool Packaged);

    /// <summary>
    /// Everything in the Start menu's All apps, by the shell's own id for it: a packaged app's
    /// AUMID ("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"), or for a desktop program the id
    /// its shortcut was registered under, which "shell:AppsFolder\&lt;id&gt;" starts the way the
    /// Start menu does (the shortcut's arguments and folder included). Uninstallers, help files and
    /// web links that some installers drop beside a program are left out: none of them is an app.
    /// </summary>
    public static List<StartApp> List()
    {
        var list = new List<StartApp>();
        var iidItem = typeof(IShellItem).GUID;
        SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iidItem, out var folder);
        try
        {
            var bhid = BHID_EnumItems;
            var iidEnum = typeof(IEnumShellItems).GUID;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumPtr);
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var name = DisplayName(item, SIGDN_NORMALDISPLAY);
                        var id = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                        bool packaged = IsPackagedId(id);
                        string? target = packaged ? null : LinkTarget(item);
                        if (!packaged && !LooksLikeApp(name, target)) continue;
                        var exe = target is not null && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null;
                        list.Add(new StartApp(id, name.Trim(), exe, packaged));
                    }
                    catch { /* one odd entry is not a reason to lose the list */ }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        finally { Marshal.ReleaseComObject(folder); }

        // The same app can be listed twice (a per-user and a machine shortcut); one is enough.
        return list.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A packaged app's id is "family!entry"; a desktop program's never has the "!" and
    /// usually has a path in it.</summary>
    public static bool IsPackagedId(string id) => id.Contains('!') && !id.Contains('\\') && !id.Contains('/');

    private static bool LooksLikeApp(string name, string? target)
    {
        if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) return false;
        if (target is null) return true;   // registered by id alone (Chrome, Steam): trust the shell
        var ext = Path.GetExtension(target).ToLowerInvariant();
        return ext is not (".url" or ".chm" or ".txt" or ".pdf" or ".htm" or ".html" or ".rtf" or ".ini" or ".log");
    }

    /// <summary>
    /// The shell's own picture of something, as a PNG data URI: a Start menu app by its id, or a
    /// path to an exe, a folder or a file. The icon, never a thumbnail of the contents -- a
    /// shortcut to a photo is drawn as the app that opens it, which is what it does.
    /// </summary>
    public static string? Icon(string parsingName, int size = 96) =>
        IconPng(parsingName, size) is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : null;

    /// <summary><see cref="Icon"/>'s picture as PNG bytes, for a program chosen as a shortcut's icon.</summary>
    public static byte[]? IconPng(string parsingName, int size = 96)
    {
        IShellItem? item = null;
        try
        {
            var iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item);
            if (item is not IShellItemImageFactory factory) return null;
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out var hbm) != 0 || hbm == IntPtr.Zero)
                return null;
            try { return EncodeBitmap(hbm); }
            finally { DeleteObject(hbm); }
        }
        catch { return null; }
        finally { if (item is not null) Marshal.ReleaseComObject(item); }
    }

    /// <summary>Start a packaged app the way the Start menu does. The process id it became, or 0
    /// when it could not be started that way (the caller falls back to the shell).</summary>
    public static uint Activate(string aumid)
    {
        try
        {
            var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
            try { return mgr.ActivateApplication(aumid, null, 0, out var pid) == 0 ? pid : 0; }
            finally { Marshal.ReleaseComObject(mgr); }
        }
        catch (Exception ex)
        {
            Log.Info($"Shortcuts: could not activate {aumid}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>The program that opens a file type (".pdf") or a protocol ("http"), as Explorer
    /// would choose it. Null when nothing is registered.</summary>
    public static string? AssociatedExe(string assoc)
    {
        try
        {
            uint len = 1024;
            var sb = new StringBuilder((int)len);
            return AssocQueryString(ASSOCF_NOTRUNCATE, ASSOCSTR_EXECUTABLE, assoc, "open", sb, ref len) == 0 && sb.Length > 0
                ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>Run <paramref name="work"/> on a thread of its own in a single-threaded apartment,
    /// which the shell's objects expect.</summary>
    public static Task<T> Sta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Shortcuts shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    // ---- reading an item ----

    private static string? DisplayName(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out var ptr);
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>Where a desktop program's Start menu shortcut points, when it is a shortcut.</summary>
    private static string? LinkTarget(IShellItem item)
    {
        if (item is not IShellItem2 item2) return null;
        try
        {
            var key = PKEY_Link_TargetParsingPath;
            return item2.GetString(ref key, out var ptr) == 0 && ptr != IntPtr.Zero
                ? TakeString(ptr) : null;
        }
        catch { return null; }
    }

    private static string? TakeString(IntPtr ptr)
    {
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>
    /// The shell hands back a 32-bit DIB whose alpha is premultiplied, as AlphaBlend wants it.
    /// Read top-down through GetDIBits (a DIB section is usually bottom-up) and saved as PNG; WPF's
    /// own CreateBitmapSourceFromHBitmap would drop the alpha and draw every icon on black. A
    /// bitmap with a colour brighter than its alpha cannot be premultiplied, and is read straight.
    /// </summary>
    private static byte[]? EncodeBitmap(IntPtr hbm)
    {
        var bm = new BITMAP();
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var bits = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbm, 0, (uint)h, bits, ref info, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }

        bool premultiplied = true, anyAlpha = false;
        for (int i = 0; i < bits.Length; i += 4)
        {
            byte a = bits[i + 3];
            if (a != 0) anyAlpha = true;
            if (bits[i] > a || bits[i + 1] > a || bits[i + 2] > a) { premultiplied = false; }
        }
        // No alpha at all: an old-style icon drawn opaque. Treat it as such rather than invisible.
        if (!anyAlpha) for (int i = 3; i < bits.Length; i += 4) bits[i] = 255;
        var format = anyAlpha && premultiplied ? PixelFormats.Pbgra32 : PixelFormats.Bgra32;
        var source = BitmapSource.Create(w, h, 96, 96, format, null, bits, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    // ---- interop ----

    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private const int ASSOCF_NOTRUNCATE = 0x20;
    private const int ASSOCSTR_EXECUTABLE = 2;
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    private static PROPERTYKEY PKEY_Link_TargetParsingPath = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int str, string assoc, string? extra, StringBuilder? outBuf, ref uint outLen);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, ref BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        // IShellItem's, in order: a derived COM interface repeats its base's slots.
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem other, uint hint, out int order);
        // IShellItem2's own, up to the one used.
        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr punk, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr pv);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long ft);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int value);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr value);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem item, out uint fetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems copy);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbm);
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string aumid, [MarshalAs(UnmanagedType.LPWStr)] string? args, int options, out uint pid);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string aumid, IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint pid);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string aumid, IntPtr items, out uint pid);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}
