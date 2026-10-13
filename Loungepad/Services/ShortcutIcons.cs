using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Loungepad.Services;

/// <summary>
/// The pictures shortcuts carry that are not the shell's: one the user chose from their PC, and a
/// website's own icon. Both are kept as small PNGs in %APPDATA%\Loungepad\shortcut-icons and reach
/// the page as data URIs in the shortcuts message.
///
/// "ic-&lt;shortcut id&gt;-&lt;random&gt;.png" is a chosen picture; "fav-&lt;hash of the host&gt;.png"
/// is a site's icon, one per site whichever shortcuts point at it. Nothing else is ever written
/// there, and <see cref="Prune"/> deletes whatever of those two shapes no shortcut uses any more.
///
/// Everything that decodes a picture is WPF imaging (WIC), which wants an STA thread: the bridge's
/// UI thread for a chosen file, ShellApps.Sta's thread for the favicons.
/// </summary>
internal static class ShortcutIcons
{
    /// <summary>Beside shortcuts.json: ShortcutService sets it from the file it was given, so a
    /// harness on a scratch file never tidies the real folder.</summary>
    public static string Dir { get; set; } = Path.Combine(Paths.DataDir, "shortcut-icons");
    private const int MaxSide = 128;
    private const long MaxChosenBytes = 20 * 1024 * 1024, MaxIconBytes = 1024 * 1024, MaxHtmlBytes = 512 * 1024;
    private static readonly Regex OurName = new("^(ic|fav)-[a-z0-9-]{1,48}\\.png$", RegexOptions.Compiled);
    private static readonly Regex CustomName = new("^ic-[a-z0-9-]{1,48}\\.png$", RegexOptions.Compiled);
    private static readonly Dictionary<string, string?> Uris = new();
    private static readonly object Gate = new();

    public static bool IsCustomName(string? name) => name is not null && CustomName.IsMatch(name);

    public static string NewCustomName(string shortcutId) =>
        $"ic-{shortcutId}-{Guid.NewGuid().ToString("N")[..6]}.png";

    public static string FaviconName(string host) =>
        "fav-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(host.ToLowerInvariant())))[..12].ToLowerInvariant() + ".png";

    public static bool Exists(string name) => File.Exists(Path.Combine(Dir, name));

    public static void Save(string name, byte[] png)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllBytes(Path.Combine(Dir, name), png);
        lock (Gate) Uris.Remove(name);
    }

    /// <summary>The file as a data URI, read once and kept. Null when it is not there.</summary>
    public static string? DataUri(string name)
    {
        lock (Gate)
        {
            if (Uris.TryGetValue(name, out var cached)) return cached;
            string? uri = null;
            try
            {
                var path = Path.Combine(Dir, name);
                if (File.Exists(path)) uri = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
            }
            catch { /* a missing picture falls back to the automatic one */ }
            Uris[name] = uri;
            return uri;
        }
    }

    /// <summary>Delete our pictures that nothing uses any more. Only our two name shapes, only in
    /// our folder.</summary>
    public static void Prune(IEnumerable<string> keep)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var used = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(Dir))
            {
                var name = Path.GetFileName(path);
                if (!OurName.IsMatch(name) || used.Contains(name)) continue;
                try { File.Delete(path); lock (Gate) Uris.Remove(name); } catch { }
            }
        }
        catch (Exception ex) { Log.Info($"Shortcuts: tidying the icons failed: {ex.Message}"); }
    }

    // ---- a picture the user chose ----

    /// <summary>
    /// A picture or an icon from the PC as the PNG the wheel draws: any image WIC reads (PNG, JPEG,
    /// GIF, BMP, WebP where Windows has the codec, and the largest picture in an .ico), or for a
    /// program, the icon the shell shows for it. Null when it is none of those.
    /// </summary>
    public static byte[]? FromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".lnk")
            return ShellApps.IconPng(path, 256) is { } shell ? Normalize(shell) : null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaxChosenBytes) return null;
        return Normalize(File.ReadAllBytes(path));
    }

    /// <summary>The largest frame, at most <see cref="MaxSide"/> pixels a side, as PNG.</summary>
    public static byte[]? Normalize(byte[] raw)
    {
        try
        {
            using var ms = new MemoryStream(raw);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var best = decoder.Frames.OrderByDescending(f => f.PixelWidth * f.PixelHeight).FirstOrDefault();
            if (best is null || best.PixelWidth < 1 || best.PixelHeight < 1) return null;
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(best.PixelWidth, best.PixelHeight));
            BitmapSource source = scale < 1 ? new TransformedBitmap(best, new ScaleTransform(scale, scale)) : best;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var outStream = new MemoryStream();
            encoder.Save(outStream);
            return outStream.ToArray();
        }
        catch { return null; }   // not a picture WIC can read
    }

    // ---- a website's own icon ----

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        // Some sites answer a client that does not look like a browser with a block page.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        return http;
    }

    private static readonly Regex LinkTag = new("<link\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Attr = new("([a-zA-Z][\\w:-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)'|([^\\s\"'>]+))", RegexOptions.Compiled);

    /// <summary>
    /// A website's icon, asked of the site itself and nobody else: the page's own &lt;link
    /// rel="icon"&gt; and apple-touch-icon pictures, biggest first (an apple-touch-icon is usually
    /// 180 pixels, a favicon.ico 16 or 32), then /favicon.ico. SVG icons are skipped: WIC cannot
    /// draw them. Synchronous on purpose, so it stays on the shell thread that decodes it. Null
    /// when the site has nothing usable or cannot be reached.
    /// </summary>
    public static byte[]? FetchFavicon(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page) || page.Scheme is not ("http" or "https")) return null;
        var candidates = new List<(Uri Url, int Size)>();
        var origin = new Uri(page.GetLeftPart(UriPartial.Authority) + "/");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, page);
            req.Headers.Accept.ParseAdd("text/html");
            using var resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead);
            var final = resp.RequestMessage?.RequestUri ?? page;
            origin = new Uri(final.GetLeftPart(UriPartial.Authority) + "/");
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (resp.IsSuccessStatusCode && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var html = Encoding.UTF8.GetString(ReadCapped(resp, MaxHtmlBytes, truncate: true) ?? Array.Empty<byte>());
                foreach (Match tag in LinkTag.Matches(html))
                {
                    string? rel = null, href = null, sizes = null, mime = null;
                    foreach (Match a in Attr.Matches(tag.Value))
                    {
                        var value = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                        switch (a.Groups[1].Value.ToLowerInvariant())
                        {
                            case "rel": rel = value.ToLowerInvariant(); break;
                            case "href": href = value.Trim(); break;
                            case "sizes": sizes = value.ToLowerInvariant(); break;
                            case "type": mime = value.ToLowerInvariant(); break;
                        }
                    }
                    if (rel is null || href is null || !rel.Split(' ').Any(r => r is "icon" or "apple-touch-icon" or "apple-touch-icon-precomposed")) continue;
                    if (mime?.Contains("svg") == true || href.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || href.StartsWith("data:")) continue;
                    if (!Uri.TryCreate(final, href, out var icon) || icon.Scheme is not ("http" or "https")) continue;
                    int size = rel.Contains("apple-touch-icon") ? 180 : 32;
                    if (sizes is not null && Regex.Match(sizes, "(\\d+)x(\\d+)") is { Success: true } m) size = int.Parse(m.Groups[1].Value);
                    candidates.Add((icon, size));
                }
            }
        }
        catch (Exception ex) { Log.Info($"Shortcuts: {page.Host} could not be read for its icon: {ex.Message}"); }

        candidates.Add((new Uri(origin, "favicon.ico"), 16));
        foreach (var c in candidates.OrderByDescending(c => Math.Min(c.Size, 512)).DistinctBy(c => c.Url))
        {
            try
            {
                using var resp = Http.Send(new HttpRequestMessage(HttpMethod.Get, c.Url), HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = ReadCapped(resp, MaxIconBytes);
                if (bytes is null || bytes.Length == 0) continue;
                if (Normalize(bytes) is { } png) return png;
            }
            catch { /* the next one */ }
        }
        Log.Info($"Shortcuts: no usable icon found for {page.Host}");
        return null;
    }

    /// <summary>The body, or null when it is bigger than <paramref name="max"/>; with <paramref
    /// name="truncate"/>, its first <paramref name="max"/> bytes instead (a page's head is at its top).</summary>
    private static byte[]? ReadCapped(HttpResponseMessage resp, long max, bool truncate = false)
    {
        if (!truncate && resp.Content.Headers.ContentLength is long len && len > max) return null;
        using var stream = resp.Content.ReadAsStream();
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, n);
            if (ms.Length > max) return truncate ? ms.ToArray()[..(int)max] : null;
        }
        return ms.ToArray();
    }
}
