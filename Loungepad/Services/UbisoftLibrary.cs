using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Ubisoft Connect's games, read from what the client keeps on this PC.
///
/// Installed games are a registry key each (Ubisoft\Launcher\Installs\&lt;product id&gt;, with the
/// folder), the way Playnite reads them. Everything else comes from the client's own caches under
/// %LOCALAPPDATA%\Ubisoft Game Launcher\cache:
///   configuration\configurations  every product the client has a configuration for -- names,
///                                 art, whether it is a game or an add-on, the "space" its online
///                                 services live in. A protobuf of { 1: id, 2: install id, 3: YAML }.
///   ownership\&lt;user id&gt;         what the signed-in account owns: a 264-byte signed header, then
///                                 a protobuf of { 1: product id, 2: product id, ... } per product.
/// The configurations hold far more than the account owns (988 entries against 15 owned games on
/// the PC this was written on), which is why Playnite's "every configuration with a start_game" is
/// not used for the library: the ownership file is the account's own list.
/// </summary>
public static class Ubisoft
{
    public const string Platform = "Ubisoft";
    public const string IdPrefix = "ubisoft:";
    private const string AssetBase = "https://ubistatic3-a.akamaihd.net/orbit/uplay_launcher_3_0/assets/";

    private static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ubisoft Game Launcher", "cache");

    /// <summary>One product out of the configuration cache.</summary>
    public sealed record Product(uint Id, string? Name, string? SpaceId, bool IsGame, bool IsAddon, bool ThirdParty,
        string? Thumb, string? Background, string? RelativeExe, List<uint> Addons);

    public static string? ClientDir()
    {
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\Ubisoft\Launcher");
            return UninstallEntries.Clean(k?.GetValue("InstallDir") as string) is { } dir && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    public static bool ClientInstalled() => ClientDir() is { } d
        && (File.Exists(Path.Combine(d, "upc.exe")) || File.Exists(Path.Combine(d, "UbisoftConnect.exe")));

    public static string LaunchUri(uint id) => $"uplay://launch/{id}/0";
    public static string InstallUri(uint id) => $"uplay://install/{id}";

    public static uint? IdOf(Game g) =>
        g.Id.StartsWith(IdPrefix, StringComparison.Ordinal) && uint.TryParse(g.Id[IdPrefix.Length..], out var id) ? id : null;

    // ---------- the configuration cache ----------

    private static readonly object CacheGate = new();
    private static DateTime _productsStamp;
    private static Dictionary<uint, Product> _products = new();

    /// <summary>Every product in the configuration cache, by id. Re-read only when the file has
    /// changed: it is a few hundred kilobytes of YAML.</summary>
    public static IReadOnlyDictionary<uint, Product> Products()
    {
        var file = Path.Combine(CacheDir, "configuration", "configurations");
        lock (CacheGate)
        {
            try
            {
                if (!File.Exists(file)) return _products = new();
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp == _productsStamp && _products.Count > 0) return _products;
                _products = ReadConfigurations(ReadShared(file));
                _productsStamp = stamp;
            }
            catch (Exception ex) { Log.Info($"Ubisoft: configuration cache not read ({ex.Message})"); }
            return _products;
        }
    }

    /// <summary>The space a game's online services (achievements) are filed under.</summary>
    public static string? SpaceIdFor(Game g) =>
        g.UbisoftSpaceId ?? (IdOf(g) is { } id && Products().TryGetValue(id, out var p) ? p.SpaceId : null);

    internal static Dictionary<uint, Product> ReadConfigurations(byte[] data)
    {
        var products = new Dictionary<uint, Product>();
        foreach (var record in ProtoReader.Fields(data, 0, data.Length).Where(f => f.Number == 1 && f.IsBytes))
        {
            uint id = 0;
            string? yaml = null;
            foreach (var f in ProtoReader.Fields(data, record))
            {
                if (f.Number == 1 && f.Wire == 0) id = (uint)f.Value;
                else if (f.Number == 3 && f.IsBytes) yaml = ProtoReader.Text(data, f);
            }
            if (id == 0 || yaml is null || products.ContainsKey(id)) continue;
            try { products[id] = ParseProduct(id, yaml); }
            catch (Exception ex) { Log.Info($"Ubisoft: configuration {id} skipped ({ex.Message})"); }
        }
        return products;
    }

    private static readonly HashSet<string> PlaceholderNames = new(StringComparer.OrdinalIgnoreCase) { "NAME", "GAMENAME", "l1", "UBISOFT GAME" };

    private static Product ParseProduct(uint id, string yaml)
    {
        var y = MiniYaml.Read(yaml);
        string? Loc(string? key) =>
            key is null ? null : y.First("localizations.default." + key) ?? key;
        // A picture is a file name, localised or not; a key the localisations do not have
        // ("THUMBIMAGE") is a placeholder for a product with no picture.
        string? Asset(string path)
        {
            var v = Loc(y.First(path));
            if (string.IsNullOrWhiteSpace(v) || !v.Contains('.')) return null;
            return v.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? v : AssetBase + v;
        }

        var rawName = y.First("root.name");
        var name = Loc(rawName);
        if (name is null || PlaceholderNames.Contains(name)) name = y.First("root.installer.game_identifier");
        if (name is not null && PlaceholderNames.Contains(name)) name = null;

        var addons = y.All("root.addons.id").Select(v => uint.TryParse(v, out var n) ? n : 0).Where(n => n > 0).ToList();
        var isUlc = y.First("root.is_ulc") is { } ulc && (ulc.Equals("yes", StringComparison.OrdinalIgnoreCase) || ulc.Equals("true", StringComparison.OrdinalIgnoreCase));
        return new Product(
            id,
            CleanName(name),
            y.First("root.space_id"),
            IsGame: y.Has("root.start_game"),
            IsAddon: isUlc,
            ThirdParty: y.Has("root.third_party_platform"),
            Thumb: Asset("root.thumb_image"),
            Background: Asset("root.background_image"),
            RelativeExe: y.First("root.start_game.offline.executables.path.relative") ?? y.First("root.start_game.online.executables.path.relative"),
            Addons: addons);
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return t.Length == 0 ? null : t;
    }

    // ---------- installed games ----------

    public static List<Game> ScanInstalled()
    {
        var games = new List<Game>();
        var products = Products();
        var uninstall = UninstallEntries.All()
            .Where(e => e.Key.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Key["Uplay Install ".Length..].Trim())
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var installs = root.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
                if (installs is null) continue;
                foreach (var name in installs.GetSubKeyNames())
                {
                    if (!uint.TryParse(name, out var id) || games.Any(g => g.Id == IdPrefix + name)) continue;
                    using var k = installs.OpenSubKey(name);
                    var dir = UninstallEntries.Clean(k?.GetValue("InstallDir") as string);
                    if (dir is null || !Directory.Exists(dir)) continue;
                    products.TryGetValue(id, out var p);
                    uninstall.TryGetValue(name, out var entry);
                    var exe = p?.RelativeExe is { } rel ? Path.Combine(dir, rel.Replace('/', '\\')) : null;
                    games.Add(new Game
                    {
                        Id = IdPrefix + name,
                        Title = p?.Name ?? CleanName(entry?.DisplayName) ?? Path.GetFileName(dir),
                        Platform = Platform,
                        LaunchUri = LaunchUri(id),
                        InstallDir = dir,
                        ExePath = exe is not null && File.Exists(exe) ? exe : null,
                        SizeBytes = (entry?.EstimatedSizeKb ?? 0) * 1024,
                        Installed = true,
                        UbisoftSpaceId = p?.SpaceId,
                        RemoteCoverUrl = p?.Thumb,
                        RemoteBackdropUrl = p?.Background,
                    });
                }
            }
            catch (Exception ex) { Log.Info($"Ubisoft: installs not read ({ex.Message})"); }
        }
        return games;
    }

    // ---------- owned games ----------

    /// <summary>
    /// The games the account owns, from the client's ownership cache for that account (the file is
    /// named after the account's id; with no id, or no file for it, the newest one), joined to the
    /// configuration cache for names and art. A product is a game when its configuration says how to
    /// start it, it is not an add-on of another, and it is not a Steam-linked copy -- those are the
    /// Steam game, and already in the library as one. Null when the client has no ownership cache
    /// for anyone on this PC.
    /// </summary>
    public static List<Game>? Owned(string? userId)
    {
        var dir = Path.Combine(CacheDir, "ownership");
        string? file = null;
        try
        {
            if (!Directory.Exists(dir)) return null;
            if (userId is { Length: > 0 } && File.Exists(Path.Combine(dir, userId))) file = Path.Combine(dir, userId);
            file ??= new DirectoryInfo(dir).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            if (file is null) return null;
        }
        catch { return null; }

        var owned = ReadOwnership(ReadShared(file));
        var products = Products();
        var addons = products.Values.Where(p => owned.Contains(p.Id)).SelectMany(p => p.Addons).ToHashSet();
        var games = new List<Game>();
        foreach (var id in owned)
        {
            if (!products.TryGetValue(id, out var p) || !p.IsGame || p.IsAddon || p.ThirdParty || addons.Contains(id) || p.Name is null) continue;
            games.Add(new Game
            {
                Id = IdPrefix + id,
                Title = p.Name,
                Platform = Platform,
                Installed = false,
                LaunchUri = LaunchUri(id),
                InstallUri = InstallUri(id),
                UbisoftSpaceId = p.SpaceId,
                RemoteCoverUrl = p.Thumb,
                RemoteBackdropUrl = p.Background,
            });
        }
        return games;
    }

    /// <summary>Every product id the ownership cache lists. The first 0x108 bytes are a signed
    /// header; the records after it are plain protobuf.</summary>
    internal static HashSet<uint> ReadOwnership(byte[] data)
    {
        var ids = new HashSet<uint>();
        const int header = 0x108;
        if (data.Length <= header) return ids;
        foreach (var record in ProtoReader.Fields(data, header, data.Length).Where(f => f.Number == 1 && f.IsBytes))
            foreach (var f in ProtoReader.Fields(data, record))
                if (f.Number is 1 or 2 && f.Wire == 0 && f.Value is > 0 and < uint.MaxValue)
                    ids.Add((uint)f.Value);
        return ids;
    }

    /// <summary>The client writes these files while it runs; read without locking it out.</summary>
    private static byte[] ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var data = new byte[fs.Length];
        fs.ReadExactly(data);
        return data;
    }
}

/// <summary>
/// The few things read out of a Ubisoft configuration: values by their dotted path, list items
/// flattened into their list's path ("root.addons.id" for every add-on's id). Not a YAML parser --
/// block scalars are skipped, anchors and flow style are not understood -- just enough for a file
/// whose shape is the client's own and has not changed in years.
/// </summary>
internal sealed class MiniYaml
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    public string? First(string path) => _values.TryGetValue(path, out var v) ? v[0] : null;
    public IEnumerable<string> All(string path) => _values.TryGetValue(path, out var v) ? v : Enumerable.Empty<string>();
    public bool Has(string path) => _keys.Contains(path);

    public static MiniYaml Read(string text)
    {
        var y = new MiniYaml();
        var stack = new List<(int Indent, string Key)>();
        var skipAbove = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart(' ');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = line.Length - trimmed.Length;
            if (skipAbove >= 0)
            {
                if (indent > skipAbove) continue;
                skipAbove = -1;
            }
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart(' ');
                indent += 2;
            }
            var colon = KeyEnd(trimmed);
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim().Trim('"', '\'');
            var value = trimmed[(colon + 1)..].Trim();
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var path = string.Join('.', stack.Select(s => s.Key).Append(key));
            y._keys.Add(path);
            if (value.Length == 0) { stack.Add((indent, key)); continue; }
            if (value is "|" or ">" or "|-" or ">-") { skipAbove = indent; continue; }
            if (!y._values.TryGetValue(path, out var list)) y._values[path] = list = new List<string>();
            list.Add(Unquote(value));
        }
        return y;
    }

    /// <summary>Where a key ends: the first ": " (or a colon closing the line) outside quotes.</summary>
    private static int KeyEnd(string s)
    {
        char quote = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { if (i == 0) quote = c; continue; }
            if (c == ':' && (i + 1 == s.Length || s[i + 1] == ' ')) return i;
        }
        return -1;
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\''))
            return v[0] == '\'' ? v[1..^1].Replace("''", "'") : v[1..^1].Replace("\\\"", "\"");
        // A trailing comment on an unquoted value.
        var hash = v.IndexOf(" #", StringComparison.Ordinal);
        return hash > 0 ? v[..hash].TrimEnd() : v;
    }
}

/// <summary>
/// A Ubisoft Connect account, for the games it owns and their achievements.
///
/// The sign-in is Ubisoft's own page (connect.ubisoft.com) in the usual window. Signed in, the page
/// keeps its session in its own storage ("PRODloginData": a ticket, a session id, the account's id
/// and name; "PRODrememberMe": a remember-me ticket), which is read out of the page the way GOG
/// Galaxy's Ubisoft integration does it, and kept encrypted like every store's. The ticket lasts a
/// few hours and is renewed against Ubisoft's session service, with the remember-me ticket when the
/// ticket itself has run out.
///
/// The owned games come from Ubisoft Connect's own cache on this PC (see <see cref="Ubisoft"/>),
/// for the account that signed in -- so the client has to have been signed in here once. The
/// sign-in is what makes it the account's library rather than whoever last used the client, and it
/// is what the achievements are read with.
/// </summary>
public class UbisoftAccountClient : IStoreAccount
{
    /// <summary>The Ubisoft Connect client's application id (the one GOG Galaxy's integration reads
    /// out of the client); the session service and the GraphQL answer to it.</summary>
    internal const string AppId = "f68a4bb5-608a-4ff2-8123-be8ef797e0a6";
    private const string GenomeId = "954e66a0-be1b-4aa0-9690-fb75201e4e9e";
    private static readonly string LoginUrl = $"https://connect.ubisoft.com/login?appId={AppId}&genomeId={GenomeId}&lang=en-US&nextUrl=https:%2F%2Fconnect.ubisoft.com%2F";
    private const string SessionsUrl = "https://public-ubiservices.ubi.com/v3/profiles/sessions";
    private const string GraphQlUrl = "https://public-ubiservices.ubi.com/v1/profiles/me/uplay/graphql";
    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public string Store => "ubisoft";
    public string DisplayName => "Ubisoft Connect";
    public StoreStatus Status { get; } = new();

    public class Session
    {
        public string Ticket { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string? Username { get; set; }
        public string? RememberMeTicket { get; set; }
        public DateTime? Expires { get; set; }
    }

    private Session? _session;
    private readonly SemaphoreSlim _renew = new(1, 1);

    public UbisoftAccountClient()
    {
        _session = AccountStore.LoadSecret<Session>(Store);
        Status.SignedIn = _session is not null;
        Status.User = _session?.Username;
    }

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        Session? captured = null;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to Ubisoft", AccountStore.ProfileDir(Store), LoginUrl,
            async core =>
            {
                if (!Uri.TryCreate(core.Source ?? "", UriKind.Absolute, out var u)
                    || !u.Host.Equals("connect.ubisoft.com", StringComparison.OrdinalIgnoreCase)) return false;
                var stored = await StoreLoginWindow.EvalStringAsync(core,
                    "(function(){try{return JSON.stringify([localStorage.getItem('PRODloginData'),localStorage.getItem('PRODrememberMe')]);}catch(e){return '';}})()");
                captured = FromStorage(stored);
                return captured is not null;
            }, poll: TimeSpan.FromSeconds(1));
        if (!ok || captured is null) return false;

        _session = captured;
        AccountStore.SaveSecret(Store, captured);
        Status.SignedIn = true;
        Status.User = captured.Username;
        Status.Error = null;
        Log.Info($"Ubisoft: signed in as {captured.Username}");
        return true;
    }

    /// <summary>The session out of the page's storage: the two values as the page wrote them.</summary>
    internal static Session? FromStorage(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            using var outer = JsonDocument.Parse(stored);
            if (outer.RootElement.ValueKind != JsonValueKind.Array) return null;
            var s = new Session();
            foreach (var item in outer.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } json) continue;
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                s.Ticket = Str(r, "ticket") ?? s.Ticket;
                s.SessionId = Str(r, "sessionId") ?? s.SessionId;
                s.UserId = Str(r, "userId") ?? Str(r, "profileId") ?? s.UserId;
                s.Username = Str(r, "nameOnPlatform") ?? s.Username;
                s.RememberMeTicket = Str(r, "rememberMeTicket") ?? s.RememberMeTicket;
                if (Str(r, "expiration") is { } exp && DateTime.TryParse(exp, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var e)) s.Expires = e;
            }
            return s.Ticket.Length > 0 && s.SessionId.Length > 0 && s.UserId.Length > 0 ? s : null;
        }
        catch (JsonException) { return null; }
    }

    public void SignOut()
    {
        _session = null;
        AccountStore.DeleteSecret(Store);
        AccountStore.DeleteCache(Store);
        AccountStore.ClearProfile(Store);
        Status.SignedIn = false;
        Status.User = null;
        Status.Count = 0;
        Status.FetchedAt = null;
        Status.Error = null;
    }

    public Task<List<Game>> GetOwnedAsync(bool force, CancellationToken ct = default)
    {
        if (_session is null)
        {
            Status.SignedIn = false;
            Status.Count = 0;
            return Task.FromResult(new List<Game>());
        }
        // Read off this PC every time: two local files, and the client may have learned of a new
        // purchase since the last scan.
        var games = Ubisoft.Owned(_session.UserId);
        Status.FetchedAt = DateTime.UtcNow;
        Status.Count = games?.Count ?? 0;
        Status.Error = games is null
            ? (Ubisoft.ClientInstalled() ? "Open Ubisoft Connect and sign in to it once, so it lists your games on this PC" : "Install Ubisoft Connect to list the games you own")
            : null;
        if (games is not null) Log.Info($"Ubisoft: {games.Count} game(s) owned by {_session.Username}");
        return Task.FromResult(games ?? new List<Game>());
    }

    // ---------- the session, for the achievements ----------

    /// <summary>A ticket good for the next few minutes, renewed when near its end: first with the
    /// ticket itself, then with the remember-me ticket. Null when neither works -- signing in again.</summary>
    public async Task<Session?> SessionAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null;
        if (s.Expires is { } exp && DateTime.UtcNow < exp.AddMinutes(-5)) return s;
        await _renew.WaitAsync(ct);
        try
        {
            s = _session;
            if (s is null) return null;
            if (s.Expires is { } exp2 && DateTime.UtcNow < exp2.AddMinutes(-5)) return s;
            var renewed = await RenewAsync(HttpMethod.Put, $"Ubi_v1 t={s.Ticket}", null, ct)
                          ?? (s.RememberMeTicket is { Length: > 0 } rm ? await RenewAsync(HttpMethod.Post, $"rm_v1 t={rm}", "{\"rememberMe\":true}", ct) : null);
            if (renewed is null)
            {
                Status.Error = "The Ubisoft sign-in has expired. Sign in again";
                return null;
            }
            renewed.Username ??= s.Username;
            renewed.RememberMeTicket ??= s.RememberMeTicket;
            if (renewed.UserId.Length == 0) renewed.UserId = s.UserId;
            _session = renewed;
            AccountStore.SaveSecret(Store, renewed);
            return renewed;
        }
        finally { _renew.Release(); }
    }

    private static async Task<Session?> RenewAsync(HttpMethod method, string auth, string? body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(method, SessionsUrl);
            req.Headers.TryAddWithoutValidation("Authorization", auth);
            AddHeaders(req);
            req.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                Log.Info($"Ubisoft: session renewal ({auth[..auth.IndexOf(' ')]}) answered {(int)res.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var s = new Session
            {
                Ticket = Str(r, "ticket") ?? "",
                SessionId = Str(r, "sessionId") ?? "",
                UserId = Str(r, "userId") ?? Str(r, "profileId") ?? "",
                Username = Str(r, "nameOnPlatform") ?? Str(r, "username"),
                RememberMeTicket = Str(r, "rememberMeTicket"),
            };
            // The expiry is the server's clock; the gap to its own "now" is what is left.
            if (Str(r, "expiration") is { } e && DateTime.TryParse(e, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var exp))
            {
                var server = Str(r, "serverTime") is { } st && DateTime.TryParse(st, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var now) ? now : DateTime.UtcNow;
                s.Expires = DateTime.UtcNow + (exp - server);
            }
            else s.Expires = DateTime.UtcNow.AddHours(2);
            return s.Ticket.Length > 0 ? s : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Info($"Ubisoft: session renewal failed ({ex.Message})");
            return null;
        }
    }

    private static void AddHeaders(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Ubi-AppId", AppId);
        req.Headers.TryAddWithoutValidation("User-Agent", Chrome);
        req.Headers.TryAddWithoutValidation("Origin", "https://connect.ubisoft.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://connect.ubisoft.com/");
    }

    /// <summary>One GraphQL request with the session: the status and the body.</summary>
    public async Task<(int Status, string Body)?> GraphQlAsync(string operation, string query, object variables, CancellationToken ct)
    {
        var s = await SessionAsync(ct);
        if (s is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { operationName = operation, variables, query }), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Ubi_v1 t={s.Ticket}");
        req.Headers.TryAddWithoutValidation("Ubi-SessionId", s.SessionId);
        AddHeaders(req);
        using var res = await Http.SendAsync(req, ct);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(ct));
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
