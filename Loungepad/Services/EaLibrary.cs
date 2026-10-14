using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Loungepad.Models;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// The EA app's games on this PC.
///
/// Every game the EA app (and Origin before it) installs gets an uninstall entry pointing at the
/// game's folder, and every such folder carries the installer's manifest,
/// __Installer\installerdata.xml: the game's content ids, its title, and in newer manifests the
/// exe it runs. That pair is the whole scan -- no sign-in, no network. The EA app's own record of
/// what it installed (ProgramData\EA Desktop\...\IS) is encrypted to the machine and is not read.
///
/// A game is launched and installed through the app's own link, origin2://game/launch, by content
/// id: the same link Lutris and Playnite's EA library use. The id is "ea:" and the manifest's first
/// content id, which is the base game's; the account's library (EaAccountClient) gives the same id
/// to the same game, by any of the content ids the manifest lists.
/// </summary>
public static class EaApp
{
    public const string Platform = "EA";
    public const string IdPrefix = "ea:";

    public static string LaunchUri(string contentId) => $"origin2://game/launch?offerIds={Uri.EscapeDataString(contentId)}&autoDownload=1";

    public static bool ClientInstalled()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = root.OpenSubKey(@"SOFTWARE\Electronic Arts\EA Desktop");
                if (k?.GetValue("ClientPath") is string p && File.Exists(p)) return true;
            }
            catch { /* the other view */ }
        }
        return false;
    }

    /// <summary>What one installed game's manifest says.</summary>
    internal sealed record Manifest(List<string> ContentIds, string? Title, string? Exe);

    public static List<Game> ScanInstalled() => Installed().Select(x => x.Game).ToList();

    /// <summary>Every installed EA game with all of its content ids, for matching the account's
    /// library to what is on disk.</summary>
    internal static List<(Game Game, List<string> ContentIds)> Installed()
    {
        var games = new List<(Game, List<string>)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in UninstallEntries.All())
        {
            if (e.InstallLocation is not { } dir) continue;
            var file = Path.Combine(dir, "__Installer", "installerdata.xml");
            if (!File.Exists(file)) continue;
            var m = ReadManifest(file, dir);
            if (m is null || m.ContentIds.Count == 0) continue;
            var id = IdPrefix + m.ContentIds[0];
            if (!seen.Add(id)) continue;
            var iconExe = UninstallEntries.IconExe(e.DisplayIcon);
            var exe = m.Exe ?? (iconExe is not null && iconExe.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(iconExe) ? iconExe : null);
            games.Add((new Game
            {
                Id = id,
                Title = Clean(m.Title) ?? Clean(e.DisplayName) ?? Path.GetFileName(dir),
                Platform = Platform,
                LaunchUri = LaunchUri(m.ContentIds[0]),
                InstallDir = dir,
                ExePath = exe,
                SizeBytes = (e.EstimatedSizeKb ?? 0) * 1024,
                Installed = Directory.Exists(dir),
            }, m.ContentIds));
        }
        return games;
    }

    /// <summary>
    /// The manifest, in either of its two shapes: the older &lt;game&gt; (manifestVersion 3), with
    /// the title under metadata/localeInfo, and the newer &lt;DiPManifest&gt;, with gameTitles and a
    /// runtime/launcher naming the exe -- often as a registry value in brackets followed by a file
    /// name, which is resolved here. English first, then whatever is there.
    /// </summary>
    internal static Manifest? ReadManifest(string file, string installDir)
    {
        try
        {
            var doc = XDocument.Load(file);
            if (doc.Root is not { } root) return null;
            IEnumerable<XElement> All(string name) => root.Descendants().Where(x => x.Name.LocalName == name);
            var ids = All("contentID").Select(x => x.Value.Trim()).Where(v => v.Length > 0).Distinct().ToList();

            string? English(IEnumerable<XElement> titles) =>
                titles.FirstOrDefault(t => (string?)t.Attribute("locale") is "en_US" or "en_GB")?.Value.Trim() is { Length: > 0 } en ? en
                : titles.Select(t => t.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            var title = English(All("gameTitle"))
                        ?? English(All("localeInfo").Select(l => l.Elements().FirstOrDefault(c => c.Name.LocalName == "title")).OfType<XElement>()
                            .Select(t => new XElement("t", new XAttribute("locale", (string?)t.Parent?.Attribute("locale") ?? ""), t.Value)));

            string? exe = null;
            foreach (var launcher in All("launcher"))
            {
                if (launcher.Elements().FirstOrDefault(x => x.Name.LocalName == "trial")?.Value.Trim() == "1") continue;
                var path = launcher.Elements().FirstOrDefault(x => x.Name.LocalName == "filePath")?.Value.Trim();
                if (ResolvePath(path, installDir) is { } resolved && File.Exists(resolved)) { exe = resolved; break; }
            }
            return new Manifest(ids, title, exe);
        }
        catch (Exception ex)
        {
            Log.Info($"EA: {file} not read ({ex.Message})");
            return null;
        }
    }

    private static readonly Regex Bracketed = new(@"^\[(?<key>[^\]]+)\](?<rest>.*)$");

    /// <summary>"[HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Apex\Install Dir]r5apex.exe" → the folder that
    /// registry value names, plus the file; a plain relative path → under the install folder.</summary>
    private static string? ResolvePath(string? raw, string installDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Bracketed.Match(raw);
        if (!m.Success) return Path.Combine(installDir, raw.TrimStart('\\', '/').Replace('/', '\\'));
        var key = m.Groups["key"].Value;
        var cut = key.LastIndexOf('\\');
        if (cut < 0) return null;
        var (hive, sub) = key.IndexOf('\\') is var first and > 0 ? (key[..first], key[(first + 1)..cut]) : ("", "");
        var name = key[(cut + 1)..];
        var rh = hive switch
        {
            "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => (RegistryHive?)null,
        };
        if (rh is null) return null;
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(rh.Value, view);
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue(name) is string folder && folder.Length > 0)
                    return Path.Combine(folder, m.Groups["rest"].Value.TrimStart('\\', '/').Replace('/', '\\'));
            }
            catch { /* the other view */ }
        }
        return null;
    }

    internal static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return t.Length == 0 ? null : t;
    }
}

/// <summary>
/// An EA account, for the games it owns and their achievements, the way Lutris and Playnite's
/// achievement plugins reach it in 2026.
///
/// The user signs in on www.ea.com in the usual window, which then opens the token address of EA's
/// own JavaScript SDK client (connect/auth?client_id=ORIGIN_JS_SDK&amp;prompt=none) and reads the
/// access token off the page; EA's GraphQL service (service-aggregation-layer.juno.ea.com) takes it
/// as a bearer token -- the service the EA app's own pages talk to. It lasts a few hours, and a new
/// one is read the same way in the same window run hidden, from the remember-me cookie the browser
/// profile keeps. Only the token and the account's ids are stored. The EA app's own client login is
/// not used: it needs a signature of this PC's hardware made with a key taken out of the app, and a
/// web session needs nothing of the kind.
/// </summary>
public class EaAccountClient : IStoreAccount
{
    private const string LoginUrl = "https://www.ea.com/login";
    private const string TokenUrl = "https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&response_type=token&redirect_uri=nucleus:rest&prompt=none";
    internal const string GraphQl = "https://service-aggregation-layer.juno.ea.com/graphql";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = MakeClient();

    public string Store => "ea";
    public string DisplayName => "EA";
    public StoreStatus Status { get; } = new();

    public class Session
    {
        public string? AccessToken { get; set; }
        public DateTime? Expires { get; set; }
        /// <summary>The account's user id ("pd") and persona id ("psd"); achievements are asked
        /// for by the persona.</summary>
        public string? UserId { get; set; }
        public string? PersonaId { get; set; }
        public string? Name { get; set; }
    }

    private Session? _session;
    private OwnedCache? _cache;
    private bool _cacheLoaded;
    private readonly SemaphoreSlim _renew = new(1, 1);

    public EaAccountClient()
    {
        _session = AccountStore.LoadSecret<Session>(Store);
        Status.SignedIn = _session is not null;
        Status.User = _session?.Name;
    }

    private static HttpClient MakeClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return c;
    }

    // ---------- sign in ----------

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        var (token, _, refused) = await TokenFromBrowserAsync(owner, visible: true);
        if (token is null)
        {
            if (refused) Status.Error = "EA did not finish the sign-in. Try again, and tick Remember me";
            return false;
        }
        var captured = new Session { AccessToken = token.Value.Token, Expires = token.Value.Expires };
        await IdentityAsync(captured, CancellationToken.None);

        _session = captured;
        AccountStore.SaveSecret(Store, captured);
        Status.SignedIn = true;
        Status.User = captured.Name;
        Status.Error = null;
        Log.Info($"EA: signed in as {captured.Name}");
        return true;
    }

    public void SignOut()
    {
        _session = null;
        _cache = null;
        AccountStore.DeleteSecret(Store);
        AccountStore.DeleteCache(Store);
        AccountStore.ClearProfile(Store);
        Status.SignedIn = false;
        Status.User = null;
        Status.Count = 0;
        Status.FetchedAt = null;
        Status.Error = null;
    }

    public string? PersonaId => _session?.PersonaId;

    /// <summary>
    /// A token, asked for by the sign-in window's own browser: it opens the token address itself, so
    /// EA sees every cookie the sign-in left exactly as the browser keeps them -- the remember-me
    /// cookie is scoped to /connect and the session cookie lives only in memory, and copies of them
    /// sent from elsewhere came back login_required every time (Oct 14 2026). PlayniteAchievements
    /// reads it the same way.
    ///
    /// Visible, for the sign-in: the window starts on www.ea.com's login, and once EA hands the user
    /// back to www.ea.com it goes to the token address and reads the answer. A login_required there
    /// sends it back to the login, three times at most. Hidden, for a renewal: it goes straight to
    /// the token address, and login_required means the sign-in has gone (<paramref
    /// name="visible"/> false and SignedOut true).
    /// </summary>
    private static async Task<((string Token, DateTime Expires)? Token, bool SignedOut, bool Refused)> TokenFromBrowserAsync(
        System.Windows.Window? owner, bool visible)
    {
        (string, DateTime)? captured = null;
        var signedOut = false;
        var refusals = 0;
        var lastSent = DateTime.MinValue;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to EA", AccountStore.ProfileDir("ea"), visible ? LoginUrl : TokenUrl,
            async core =>
            {
                if (!Uri.TryCreate(core.Source ?? "", UriKind.Absolute, out var u)) return false;
                if (u.Host.Equals("accounts.ea.com", StringComparison.OrdinalIgnoreCase)
                    && u.AbsolutePath.StartsWith("/connect/auth", StringComparison.OrdinalIgnoreCase)
                    && u.Query.Contains("client_id=ORIGIN_JS_SDK", StringComparison.OrdinalIgnoreCase))
                {
                    // Chromium shows a JSON answer as text, sometimes under a "Pretty-print" switch:
                    // the object is whatever sits between the first brace and the last. Read only
                    // from the token page itself -- the address changes before the page does.
                    var text = await StoreLoginWindow.EvalStringAsync(core,
                        "(location.host === 'accounts.ea.com' && document.body ? document.body.innerText : '')");
                    int open = text.IndexOf('{'), close = text.LastIndexOf('}');
                    if (open < 0 || close <= open) return false;   // not loaded yet
                    var (token, expires, error) = ReadToken(text[open..(close + 1)]);
                    if (token is not null) { captured = (token, expires); return true; }
                    if (error is null) return false;               // not an answer yet
                    Log.Info($"EA: no token from the sign-in ({error})");
                    if (!visible) { signedOut = error is "login_required"; return true; }
                    if (++refusals >= 3) return true;
                    core.Navigate(LoginUrl);
                    return false;
                }
                // Back on the store after the sign-in: ask for the token. Not twice for one landing.
                if (visible && u.Host.Equals("www.ea.com", StringComparison.OrdinalIgnoreCase)
                    && !u.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
                    && DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(5))
                {
                    lastSent = DateTime.UtcNow;
                    core.Navigate(TokenUrl);
                }
                return false;
            }, visible);
        return (ok ? captured : null, signedOut, refusals >= 3);
    }

    /// <summary>The token answer: the token and when it runs out, or the error EA named.</summary>
    internal static (string? Token, DateTime Expires, string? Error) ReadToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return (null, default, null);
            var token = Str(r, "access_token");
            if (string.IsNullOrEmpty(token)) return (null, default, Str(r, "error") ?? Str(r, "error_code"));
            var secs = !r.TryGetProperty("expires_in", out var e) ? 3600
                : e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n
                : e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s) ? s : 3600;
            return (token, DateTime.UtcNow.AddSeconds(Math.Clamp(secs, 300, 24 * 3600)), null);
        }
        catch (JsonException) { return (null, default, null); }
    }

    /// <summary>Who the token belongs to: user id, persona id and display name.</summary>
    private static async Task IdentityAsync(Session session, CancellationToken ct)
    {
        var doc = await QueryAsync(session.AccessToken!, "query{me{player{pd psd displayName}}}", null, ct);
        if (doc is null) return;
        using (doc)
        {
            if (Dig(doc.RootElement, "data", "me", "player") is { } p)
            {
                session.UserId = Str(p, "pd") ?? (JsonNum.Long(p, "pd") is { } pd ? pd.ToString() : session.UserId);
                session.PersonaId = Str(p, "psd") ?? (JsonNum.Long(p, "psd") is { } psd ? psd.ToString() : session.PersonaId);
                session.Name = Str(p, "displayName") ?? session.Name;
            }
        }
    }

    /// <summary>A token good for the next few minutes, asked for again by the sign-in's browser,
    /// run hidden, when the last has run out (about an hour): the browser profile keeps EA's
    /// remember-me cookie. Null when the sign-in itself has gone -- signing in again.</summary>
    public async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null;
        if (s.AccessToken is { Length: > 0 } t && s.Expires is { } exp && DateTime.UtcNow < exp.AddMinutes(-5)) return t;
        await _renew.WaitAsync(ct);
        try
        {
            s = _session;
            if (s is null) return null;
            if (s.AccessToken is { Length: > 0 } t2 && s.Expires is { } exp2 && DateTime.UtcNow < exp2.AddMinutes(-5)) return t2;
            var (token, signedOut, _) = await StoreLoginWindow.OnUiThread(() => TokenFromBrowserAsync(null, visible: false));
            if (token is null)
            {
                Status.Error = signedOut ? "The EA sign-in has expired. Sign in again" : "Could not renew the EA sign-in";
                Log.Info($"EA: renewal {(signedOut ? "found the sign-in gone" : "did not answer")}");
                return null;
            }
            s.AccessToken = token.Value.Token;
            s.Expires = token.Value.Expires;
            if (s.PersonaId is null) await IdentityAsync(s, ct);
            AccountStore.SaveSecret(Store, s);
            Status.Error = null;
            return s.AccessToken;
        }
        finally { _renew.Release(); }
    }

    /// <summary>One GraphQL request, posted. Null on a transport failure or a refusal; a document
    /// with an "errors" array is handed back for the caller to read.</summary>
    internal static async Task<JsonDocument?> QueryAsync(string? token, string query, object? variables, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, GraphQl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query, variables = variables ?? new { } }), Encoding.UTF8, "application/json"),
        };
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            Log.Info($"EA: GraphQL answered {(int)res.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
            if ((int)res.StatusCode is 401 or 403) throw new UnauthorizedAccessException("EA refused the token");
            return null;
        }
        return JsonDocument.Parse(text);
    }

    internal static string? Errors(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0
            ? string.Join("; ", errs.EnumerateArray().Select(e => Str(e, "message")).Where(m => m is not null))
            : null;

    // ---------- the library ----------

    /// <summary>The owned games, base games only, PC only, EA's store only -- a Steam or Epic copy of
    /// an EA game is that store's. Paged like the EA app pages it.</summary>
    private const string OwnedQuery = @"query getOwned($limit: Int, $next: String) {
  me {
    ownedGameProducts(locale: ""DEFAULT"", entitlementEnabled: true, storefronts: [EA], type: [DIGITAL_FULL_GAME, PACKAGED_FULL_GAME], platforms: [PC], paging: { limit: $limit, next: $next }) {
      next
      items { originOfferId product { id name gameSlug baseItem { gameType } gameProductUser { ownershipMethods } } }
    }
  }
}";

    /// <summary>The same without the ownership methods, for a service that stops knowing the field:
    /// one unknown field fails the whole query, and the library matters more than the filter.</summary>
    private const string OwnedQueryPlain = @"query getOwned($limit: Int, $next: String) {
  me {
    ownedGameProducts(locale: ""DEFAULT"", entitlementEnabled: true, storefronts: [EA], type: [DIGITAL_FULL_GAME, PACKAGED_FULL_GAME], platforms: [PC], paging: { limit: $limit, next: $next }) {
      next
      items { originOfferId product { id name gameSlug baseItem { gameType } } }
    }
  }
}";

    private const string OffersQuery = @"query getOffers($offerIds: [String!]!) {
  legacyOffers(offerIds: $offerIds, locale: ""DEFAULT"") { offerId: id contentId }
  gameProducts(offerIds: $offerIds, locale: ""DEFAULT"") {
    items { originOfferId baseItem { packArt { largestImage { path } } keyArt { largestImage { path } } } }
  }
}";

    private const string OffersQueryPlain = @"query getOffers($offerIds: [String!]!) {
  legacyOffers(offerIds: $offerIds, locale: ""DEFAULT"") { offerId: id contentId }
}";

    /// <summary>Owned through a subscription only (EA Play, Game Pass's EA Play): a game the account
    /// may play while it subscribes, not one it owns, as GOG Galaxy's EA integration counts them.</summary>
    private static readonly HashSet<string> SubscriptionOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "VAULT", "XGP_VAULT", "STEAM_VAULT", "STEAM_SUBSCRIPTION", "EPIC_VAULT", "EPIC_SUBSCRIPTION",
    };

    public async Task<List<Game>> GetOwnedAsync(bool force, CancellationToken ct = default)
    {
        var cached = Cached();
        if (_session is null)
        {
            Status.SignedIn = false;
            return Report(null);
        }
        if (!force && cached is not null && DateTime.UtcNow - cached.FetchedAt < AccountStore.Freshness)
        {
            Status.Error = null;
            return Report(cached);
        }

        try
        {
            var token = await AccessTokenAsync(ct);
            if (token is null) return Report(cached);

            var owned = await OwnedOffersAsync(token, ct);
            var known = (cached?.Games ?? new()).Where(g => g.EaOfferId is not null)
                .GroupBy(g => g.EaOfferId!).ToDictionary(g => g.Key, g => g.First());
            // What is on disk, by every content id its manifest lists, so the account's copy of an
            // installed game carries the installed one's id and the two are one entry.
            var onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, ids) in EaApp.Installed())
                foreach (var id in ids) onDisk.TryAdd(id, game.Id);

            var missing = owned.Where(o => !known.ContainsKey(o.OfferId)).Select(o => o.OfferId).Distinct().ToList();
            var details = await OffersAsync(missing, ct);

            var games = new List<Game>();
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in owned)
            {
                string? contentId = null; string? cover = null, backdrop = null;
                if (known.TryGetValue(o.OfferId, out var have))
                {
                    contentId = have.Id[EaApp.IdPrefix.Length..];
                    cover = have.RemoteCoverUrl; backdrop = have.RemoteBackdropUrl;
                }
                else if (details.TryGetValue(o.OfferId, out var d)) (contentId, cover, backdrop) = d;
                if (string.IsNullOrEmpty(contentId)) continue;
                var id = onDisk.TryGetValue(contentId, out var installedId) ? installedId : EaApp.IdPrefix + contentId;
                if (!taken.Add(id)) continue;
                games.Add(new Game
                {
                    Id = id,
                    Title = EaApp.Clean(o.Name) ?? o.Name,
                    Platform = EaApp.Platform,
                    Installed = false,
                    LaunchUri = EaApp.LaunchUri(contentId),
                    // The same link installs: the app opens the game ready to download.
                    InstallUri = EaApp.LaunchUri(contentId),
                    EaOfferId = o.OfferId,
                    RemoteCoverUrl = cover,
                    RemoteBackdropUrl = backdrop,
                });
            }

            var cache = new OwnedCache { FetchedAt = DateTime.UtcNow, Games = games };
            _cache = cache;
            _cacheLoaded = true;
            AccountStore.SaveCache(Store, cache);
            Status.Error = null;
            Log.Info($"EA: {games.Count} game(s) owned by {_session.Name} ({missing.Count} offer lookups)");
            return Report(cache);
        }
        catch (UnauthorizedAccessException)
        {
            if (_session is { } s) { s.AccessToken = null; s.Expires = null; }
            Status.Error = "EA refused the sign-in. Sign in again";
            return Report(cached);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status.Error = cached is not null
                ? $"Could not reach EA; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}"
                : "Could not reach EA";
            Log.Info($"EA: library fetch failed ({ex.Message})");
            return Report(cached);
        }
    }

    private sealed record OwnedOffer(string OfferId, string Name);

    private static async Task<List<OwnedOffer>> OwnedOffersAsync(string token, CancellationToken ct)
    {
        var list = new List<OwnedOffer>();
        var query = OwnedQuery;
        string? next = null;
        for (var page = 0; page < 50; page++)
        {
            using var doc = await QueryAsync(token, query, new { limit = 100, next }, ct)
                            ?? throw new HttpRequestException("EA did not answer for the library");
            if (Errors(doc) is { } why)
            {
                if (query == OwnedQuery) { Log.Info($"EA: library query refused ({why}); asking without the ownership methods"); query = OwnedQueryPlain; page--; continue; }
                throw new HttpRequestException("EA: " + why);
            }
            if (Dig(doc.RootElement, "data", "me", "ownedGameProducts") is not { } products) break;
            if (products.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var item in items.EnumerateArray())
                {
                    var offer = Str(item, "originOfferId");
                    if (offer is null || !item.TryGetProperty("product", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                    if (Dig(p, "baseItem") is { } bi && Str(bi, "gameType") is { } type && !type.Equals("BASE_GAME", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Dig(p, "gameProductUser") is { } gpu && gpu.TryGetProperty("ownershipMethods", out var methods) && methods.ValueKind == JsonValueKind.Array)
                    {
                        var all = methods.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString()!).ToList();
                        if (all.Count > 0 && all.All(SubscriptionOnly.Contains)) continue;
                    }
                    list.Add(new OwnedOffer(offer, Str(p, "name") ?? offer));
                }
            next = Str(products, "next");
            if (string.IsNullOrEmpty(next)) break;
        }
        return list;
    }

    /// <summary>Each offer's content id (what the EA app launches by) and the store's art, a hundred
    /// at a time. Public: asked without the token, as the EA app's own catalogue is.</summary>
    private static async Task<Dictionary<string, (string? ContentId, string? Cover, string? Backdrop)>> OffersAsync(List<string> offerIds, CancellationToken ct)
    {
        var map = new Dictionary<string, (string?, string?, string?)>(StringComparer.OrdinalIgnoreCase);
        var query = OffersQuery;
        foreach (var chunk in offerIds.Chunk(100))
        {
            using var doc = await QueryAsync(null, query, new { offerIds = chunk }, ct);
            if (doc is null) continue;
            if (Errors(doc) is { } why && query == OffersQuery && Dig(doc.RootElement, "data", "legacyOffers") is null)
            {
                Log.Info($"EA: offer query refused ({why}); asking without the art");
                query = OffersQueryPlain;
                using var plain = await QueryAsync(null, query, new { offerIds = chunk }, ct);
                if (plain is not null) Read(plain);
                continue;
            }
            Read(doc);
        }
        return map;

        void Read(JsonDocument doc)
        {
            var art = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
            if (Dig(doc.RootElement, "data", "gameProducts") is { } gp && gp.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var i in items.EnumerateArray())
                    if (Str(i, "originOfferId") is { } o)
                        art[o] = (Str(Dig(i, "baseItem", "packArt", "largestImage") ?? default, "path"),
                                  Str(Dig(i, "baseItem", "keyArt", "largestImage") ?? default, "path"));
            if (Dig(doc.RootElement, "data") is { } data && data.TryGetProperty("legacyOffers", out var offers) && offers.ValueKind == JsonValueKind.Array)
                foreach (var o in offers.EnumerateArray())
                    if (Str(o, "offerId") is { } id)
                    {
                        art.TryGetValue(id, out var a);
                        map[id] = (Str(o, "contentId"), Safe(a.Item1), Safe(a.Item2));
                    }
        }
    }

    /// <summary>The offer this game is owned through, from the entry itself or, for an installed
    /// game the account also lists, from the account's library.</summary>
    public string? OfferIdFor(Game g) =>
        g.EaOfferId ?? Cached()?.Games.FirstOrDefault(x => x.Id.Equals(g.Id, StringComparison.OrdinalIgnoreCase))?.EaOfferId;

    private static string? Safe(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? url : null;

    private List<Game> Report(OwnedCache? cache)
    {
        Status.Count = cache?.Games.Count ?? 0;
        Status.FetchedAt = cache?.FetchedAt;
        return (cache?.Games ?? new()).Select(EpicAccountClient.Clone).ToList();
    }

    private OwnedCache? Cached()
    {
        if (!_cacheLoaded) { _cacheLoaded = true; _cache = AccountStore.LoadCache(Store); }
        return _cache;
    }

    internal static JsonElement? Dig(JsonElement e, params string[] path)
    {
        foreach (var key in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var next) || next.ValueKind == JsonValueKind.Null) return null;
            e = next;
        }
        return e;
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
