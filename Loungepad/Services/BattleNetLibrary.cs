using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Loungepad.Models;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Loungepad.Services;

/// <summary>
/// Battle.net's games, the way Playnite's Battle.net library reads them.
///
/// Battle.net keeps no list of games anywhere a program can read with names. What it does leave is
/// an uninstall entry per game whose uninstaller is told the game's uid (`--uid=prometheus`), and
/// its agent's product.db, which lists every product it has installed with the folder. Both name
/// the game only by Blizzard's internal codes, so <see cref="Products"/> is the table that turns a
/// code into a game: the uid the uninstaller and product.db use, the product code the client
/// launches by, the title id the account pages list, and the name. A game missing from the table
/// is not shown -- product.db also lists the agent, the client and its components.
///
/// Launching goes through the client (`Battle.net.exe --exec="launch Pro"`), which needs the client
/// up and signed in first; see <see cref="StartAsync"/>. The "classic" games -- Diablo II and
/// Warcraft III from before the client -- are plain programs and start directly.
/// </summary>
public static class BattleNet
{
    public const string Platform = "Battle.net";
    public const string IdPrefix = "battlenet:";

    /// <summary>A Battle.net product. <paramref name="Code"/> is what the client launches and
    /// opens by, <paramref name="Uid"/> what its uninstaller and product.db call it (a regional
    /// suffix is added there: diablo3_enus), <paramref name="TitleId"/> what account.battle.net
    /// lists it under. A classic game has no client code; it is matched by its uninstall entry's
    /// name (<paramref name="Uid"/>) and started from <paramref name="ClassicExe"/>.</summary>
    public sealed record Product(string Code, string Uid, string Name, long TitleId, string? ClassicExe = null, string? Link = null)
    {
        public bool Classic => ClassicExe is not null;
        /// <summary>Found installed through the client's aggregate.json and not in the table: a
        /// game newer than this build. It starts by the client's own link for it.</summary>
        public bool Discovered => Link is not null;
    }

    /// <summary>
    /// From Playnite's BattleNetGames.cs (PlayniteExtensions, MIT), as of Jan 2026. Two products
    /// share a title id (Modern Warfare II and III both sit under the Call of Duty app); the first
    /// wins for an account's list. Order matters for the uid prefix match only through
    /// <see cref="ByUid"/>'s longest-match rule, not through this list.
    /// </summary>
    public static readonly IReadOnlyList<Product> Products = new Product[]
    {
        new("WoW", "wow", "World of Warcraft", 5730135),
        new("D3", "diablo3", "Diablo III", 17459),
        new("S2", "s2", "StarCraft II", 21298),
        new("S1", "s1", "StarCraft", 21297),
        new("WTCG", "hs_beta", "Hearthstone", 1465140039),
        new("Hero", "heroes", "Heroes of the Storm", 1214607983),
        new("Pro", "prometheus", "Overwatch 2", 5272175),
        new("VIPR", "viper", "Call of Duty: Black Ops 4", 1447645266),
        new("ODIN", "odin", "Call of Duty: Modern Warfare", 1329875278),
        new("W3", "w3", "Warcraft III: Reforged", 22323),
        new("LAZR", "lazarus", "Call of Duty: Modern Warfare 2 Campaign Remastered", 1279351378),
        new("ZEUS", "zeus", "Call of Duty: Black Ops Cold War", 1514493267),
        new("WLBY", "wlby", "Crash Bandicoot 4: It's About Time", 1464615513),
        new("OSI", "osi", "Diablo II: Resurrected", 5198665),
        new("RTRO", "rtro", "Blizzard Arcade Collection", 1381257807),
        new("FORE", "fore", "Call of Duty: Vanguard", 1179603525),
        new("ANBS", "anbs", "Diablo Immortal", 1095647827),
        new("AUKS", "auks", "Call of Duty: Modern Warfare II", 1096108883),
        new("PNTA", "pinta", "Call of Duty: Modern Warfare III", 1096108883),
        new("Fen", "fen", "Diablo IV", 4613486),
        new("D1", "d1", "Diablo", 1146246220),
        new("W1R", "w1r", "Warcraft: Remastered", 5714258),
        new("W2R", "w2r", "Warcraft II: Remastered", 5714514),
        new("W1", "w1", "Warcraft: Orcs & Humans", 1463898673),
        new("W2", "w2", "Warcraft II: Battle.net Edition", 1462911566),
        new("GRY", "gryphon", "Warcraft Rumble", 4674137),
        new("ARIS", "aris", "DOOM: The Dark Ages", 1095911763),
        new("SCOR", "scorpio", "Sea of Thieves", 1396920146),
        new("ARK", "arkansas", "The Outer Worlds 2", 4280907),
        new("LBRA", "libra", "Tony Hawk's Pro Skater 3 + 4", 1279414849),
        new("AQUA", "aqua", "Avowed", 1095849281),
        // The classics: an installer of their own, an uninstall entry named after the game, and no
        // client code to launch by.
        new("D2", "Diablo II", "Diablo II", 0, "Diablo II.exe"),
        new("D2X", "Diablo II", "Diablo II: Lord of Destruction", 0, "Diablo II.exe"),
        new("W3C", "Warcraft III", "Warcraft III: Reign of Chaos", 0, "Warcraft III.exe"),
        new("W3CX", "Warcraft III", "Warcraft III: The Frozen Throne", 0, "Frozen Throne.exe"),
    };

    /// <summary>Installed games the table does not know, by product code, as the last scan found
    /// them in aggregate.json. Kept so a launch after the scan still knows how to start them.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Product> Found = new(StringComparer.OrdinalIgnoreCase);

    public static Product? ByCode(string code) =>
        Products.FirstOrDefault(p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
        ?? (Found.TryGetValue(code, out var found) ? found : null);

    /// <summary>The client game a uid names: exactly, else the longest uid it starts with --
    /// "diablo3_enus" is Diablo III, and "w1r" must find Warcraft: Remastered, not the "w1" that
    /// is also its start.</summary>
    public static Product? ByUid(string uid)
    {
        var clients = Products.Where(p => !p.Classic).ToList();
        return clients.FirstOrDefault(p => p.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase))
               ?? clients.Where(p => uid.StartsWith(p.Uid, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(p => p.Uid.Length).FirstOrDefault();
    }

    public static Product? For(Game g) =>
        g.Id.StartsWith(IdPrefix, StringComparison.Ordinal) ? ByCode(g.Id[IdPrefix.Length..]) : null;

    /// <summary>A game the client starts with --exec, as opposed to a classic one started by its
    /// own exe or a newer one started by the client's link for it (its LaunchUri).</summary>
    public static bool LaunchesThroughClient(Game g) => g.Platform == Platform && For(g) is { Classic: false, Discovered: false };

    // ---------- the client ----------

    /// <summary>Battle.net.exe: where its own uninstall entry says it lives, else where the
    /// battlenet: link handler points, else the default folder. Null when it is not there.</summary>
    public static string? ClientExe()
    {
        try
        {
            foreach (var e in UninstallEntries.All())
                if (e.UninstallString?.Contains("--uid=battle.net", StringComparison.OrdinalIgnoreCase) == true
                    && e.InstallLocation is { } loc && File.Exists(Path.Combine(loc, "Battle.net.exe")))
                    return Path.Combine(loc, "Battle.net.exe");
            using var k = Registry.ClassesRoot.OpenSubKey(@"battlenet\shell\open\command");
            if (k?.GetValue(null) is string cmd && Regex.Match(cmd, "^\"([^\"]+\\.exe)\"") is { Success: true } m && File.Exists(m.Groups[1].Value))
                return m.Groups[1].Value;
        }
        catch { /* fall through to the default */ }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Battle.net", "Battle.net.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    public static bool ClientInstalled() => ClientExe() is not null;

    private static Process[] ClientProcesses() => Process.GetProcessesByName("Battle.net");

    /// <summary>The client is up far enough to take a launch: a window of its own, or the five
    /// processes it settles at (Playnite waits for the second; the window comes first on a client
    /// that does not start minimised).</summary>
    private static bool ClientReady()
    {
        var ps = ClientProcesses();
        try { return ps.Length >= 5 || ps.Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }); }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    /// <summary>
    /// Ask the client to start the game. The client only takes a launch once it is running and
    /// signed in, and a cold `--exec` is dropped on the floor, so a client that is not running is
    /// started first and waited for -- up to 45 seconds for it to come up, then a few more for its
    /// sign-in, which it does after its window appears. Run off the session's thread: the launcher
    /// has stepped aside by then, so a client that wants a password is in front to ask for it.
    /// </summary>
    public static async Task StartAsync(Game game, CancellationToken ct)
    {
        var product = For(game) ?? throw new InvalidOperationException($"{game.Title} is not a Battle.net game Loungepad knows");
        var exe = ClientExe() ?? throw new FileNotFoundException("Battle.net is not installed");
        var dir = Path.GetDirectoryName(exe);
        var cold = ClientProcesses() is var running && running.Length == 0;
        foreach (var p in running) p.Dispose();
        if (cold)
        {
            Log.Info("Battle.net is not running; starting it before the game");
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
            var until = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < until && !ClientReady()) await Task.Delay(500, ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        Log.Info($"Battle.net: launch {product.Code} ({game.Title})");
        Process.Start(new ProcessStartInfo(exe, $"--exec=\"launch {product.Code}\"") { UseShellExecute = true, WorkingDirectory = dir })?.Dispose();
    }

    /// <summary>Opens the game's page in the client, where its Install button is. Lower case, the
    /// way the client writes its own links into aggregate.json.</summary>
    public static string InstallUri(Product p) => $"battlenet://game/{p.Code.ToLowerInvariant()}";

    // ---------- installed games ----------

    public static List<Game> ScanInstalled()
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        void Add(Product p, string dir, long sizeKb, string? exe = null)
        {
            var g = Entry(p);
            g.InstallDir = dir;
            g.Installed = true;
            g.SizeBytes = sizeKb * 1024;
            g.ExePath = exe;
            // Several regions or versions of one game can be installed; the first found is the one.
            games.TryAdd(g.Id, g);
        }

        var db = ReadProductDb();
        var aggregate = ReadAggregate();
        // A game the table does not know but the client lists is still a game: registered here,
        // under the client's own name and link, before the passes below look anything up.
        foreach (var a in aggregate)
            if (ByCode(a.Code) is null)
            {
                Found[a.Code] = new Product(a.Code, a.Code, Clean(a.Name) ?? a.Code, 0, Link: a.Link ?? $"battlenet://game/{a.Code}");
                Log.Info($"Battle.net: {a.Name} ({a.Code}) is not in the product table; it starts by the client's link");
            }
        // The uninstaller names a game by uid and the client's links by product code;
        // product.db holds both.
        Product? ForUid(string uid) => ByUid(uid)
            ?? db.Where(x => x.Uid.Equals(uid, StringComparison.OrdinalIgnoreCase) && x.Code is not null)
                 .Select(x => ByCode(x.Code!)).FirstOrDefault(p => p is not null);

        foreach (var e in UninstallEntries.All())
        {
            if (e.InstallLocation is not { } loc || !Directory.Exists(loc)) continue;
            var m = e.UninstallString is null ? null : Regex.Match(e.UninstallString, @"Battle\.net.*--uid=([^\s""]+)", RegexOptions.IgnoreCase);
            if (m is { Success: true })
            {
                var uid = m.Groups[1].Value;
                if (uid.Equals("battle.net", StringComparison.OrdinalIgnoreCase)) continue;
                // The public test realms install as games of their own and are not ones.
                if (e.DisplayName is { } dn && (dn.EndsWith("Test", StringComparison.OrdinalIgnoreCase)
                    || dn.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) || dn.EndsWith("PTR", StringComparison.OrdinalIgnoreCase))) continue;
                if (ForUid(uid) is { } product) Add(product, loc, e.EstimatedSizeKb ?? 0);
                else Log.Info($"Battle.net: no game known for uid {uid} ({e.DisplayName})");
            }
            else if (e.Publisher == "Blizzard Entertainment" && e.DisplayName is { } name)
            {
                foreach (var classic in Products.Where(p => p.Classic && p.Uid == name))
                {
                    var exe = Path.Combine(loc, classic.ClassicExe!);
                    if (File.Exists(exe)) Add(classic, loc, e.EstimatedSizeKb ?? 0, exe);
                }
            }
        }

        // The client does not write an uninstall entry for every game; its agent's database knows
        // them all.
        foreach (var (uid, code, path, installed) in db)
        {
            if (!installed || (ByUid(uid) ?? (code is null ? null : ByCode(code))) is not { } product
                || games.ContainsKey(IdPrefix + product.Code)) continue;
            var dir = UninstallEntries.Clean(path);
            if (dir is not null && Directory.Exists(dir)) Add(product, dir, 0);
        }

        // And what the client lists for the Xbox app, which names the folder through its icon.
        foreach (var a in aggregate)
        {
            if (ByCode(a.Code) is not { } product || games.ContainsKey(IdPrefix + product.Code)) continue;
            var dir = UninstallEntries.Clean(db.FirstOrDefault(x => x.Installed && a.Code.Equals(x.Code, StringComparison.OrdinalIgnoreCase)).Path)
                      ?? (a.Icon is { } icon ? UninstallEntries.Clean(Path.GetDirectoryName(icon)) : null);
            if (dir is not null && Directory.Exists(dir)) Add(product, dir, 0);
        }

        // The client's own box art, for the first paint and as the last fallback after Steam and
        // the service.
        foreach (var g in games.Values)
            if (aggregate.FirstOrDefault(a => (IdPrefix + a.Code).Equals(g.Id, StringComparison.OrdinalIgnoreCase)) is { BoxArt: { } art })
                g.RemoteCoverUrl = art;
        return games.Values.ToList();
    }

    /// <summary>The library entry every Battle.net game starts from, installed or owned.</summary>
    internal static Game Entry(Product p) => new()
    {
        Id = IdPrefix + p.Code,
        Title = p.Name,
        Platform = Platform,
        LaunchUri = p.Link,
        Installed = false,
    };

    private static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var t = name.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return t.Length == 0 ? null : t;
    }

    /// <summary>One installed game as the client lists it for the Xbox app.</summary>
    internal sealed record AggregateItem(string Code, string Name, string? Icon, string? BoxArt, string? Link);

    /// <summary>
    /// The agent's aggregate.json, which the client writes for the Xbox app's list of games from
    /// other launchers: every installed game with its product code, name, an exe for the icon,
    /// box art and the link that starts it (battlenet://game/pro). Newer than product.db's format
    /// and readable without a schema; only https art is taken.
    /// </summary>
    internal static List<AggregateItem> ReadAggregate(string? file = null)
    {
        var list = new List<AggregateItem>();
        file ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "aggregate.json");
        try
        {
            if (!File.Exists(file)) return list;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (!doc.RootElement.TryGetProperty("installed", out var installed) || installed.ValueKind != JsonValueKind.Array) return list;
            string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            foreach (var e in installed.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || S(e, "product_id") is not { } code || !Regex.IsMatch(code, "^[A-Za-z0-9_]{1,32}$")) continue;
                var art = S(e, "box_art_uri");
                var link = S(e, "launch_uri");
                list.Add(new AggregateItem(code, S(e, "name") ?? code, S(e, "icon_path"),
                    art is not null && art.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? art : null,
                    link is not null && link.StartsWith("battlenet://game/", StringComparison.OrdinalIgnoreCase) ? link : null));
            }
        }
        catch (Exception ex) { Log.Info($"Battle.net: aggregate.json not read ({ex.Message})"); }
        return list;
    }

    /// <summary>
    /// The agent's product.db: a protobuf of every product it manages. Each install is field 1 of
    /// the root -- { 1: uid, 2: product code, 3: { 1: install path }, 4: { 1: { 1: installed } } }
    /// -- the shape Playnite reads (and the open product.db schema the community keeps). An
    /// install whose state says it is not installed (a download under way) is skipped; one with
    /// no state at all is taken at its folder's word.
    /// </summary>
    internal static List<(string Uid, string? Code, string? Path, bool Installed)> ReadProductDb(string? file = null)
    {
        var list = new List<(string, string?, string?, bool)>();
        file ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "product.db");
        byte[] data;
        try
        {
            if (!File.Exists(file)) return list;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        catch (Exception ex) { Log.Info($"Battle.net: product.db not read ({ex.Message})"); return list; }

        foreach (var install in ProtoReader.Fields(data, 0, data.Length).Where(f => f.Number == 1 && f.IsBytes))
        {
            string? uid = null, code = null, path = null;
            var installed = true;
            foreach (var f in ProtoReader.Fields(data, install))
            {
                if (f.Number == 1 && f.IsBytes) uid = ProtoReader.Text(data, f);
                else if (f.Number == 2 && f.IsBytes) code = ProtoReader.Text(data, f);
                else if (f.Number == 3 && f.IsBytes)
                    path = ProtoReader.Fields(data, f).Where(x => x.Number == 1 && x.IsBytes).Select(x => ProtoReader.Text(data, x)).FirstOrDefault();
                else if (f.Number == 4 && f.IsBytes)
                {
                    var baseState = ProtoReader.Fields(data, f).FirstOrDefault(x => x.Number == 1 && x.IsBytes);
                    if (baseState.Length > 0 && ProtoReader.Fields(data, baseState).FirstOrDefault(x => x.Number == 1 && x.Wire == 0) is { Number: 1 } flag)
                        installed = flag.Value != 0;
                }
            }
            if (!string.IsNullOrEmpty(uid)) list.Add((uid, string.IsNullOrEmpty(code) ? null : code, path, installed));
        }
        return list;
    }
}

/// <summary>
/// The games a Battle.net account owns, through account.battle.net the way Playnite reads them: the
/// user signs in on Blizzard's own page, and the account site's own API answers for the session
/// (`/api/games-and-subs`, `/api/classic-games`). The session lives in the sign-in window's
/// browser profile and is renewed by loading the site again in the same window run hidden, which
/// is how Blizzard's pages renew it in any browser (Playnite does the same with an offscreen view).
/// Nothing is kept but the answer and the fact of the sign-in; there is no token to store.
/// </summary>
public class BattleNetAccountClient : IStoreAccount
{
    private const string AccountHome = "https://account.battle.net/";
    /// <summary>Playnite's renewal: the account site's own authorisation route, which refreshes the
    /// session from the long-lived login cookie and lands back on the site.</summary>
    private const string RenewUrl = "https://account.battle.net/oauth2/authorization/account-settings";
    private const string GamesUrl = "https://account.battle.net/api/games-and-subs";
    private const string ClassicUrl = "https://account.battle.net/api/classic-games";

    public string Store => "battlenet";
    public string DisplayName => "Battle.net";
    public StoreStatus Status { get; } = new();

    public class Session
    {
        public DateTime SignedInAt { get; set; }
    }

    private Session? _session;
    private OwnedCache? _cache;
    private bool _cacheLoaded;
    private readonly SemaphoreSlim _fetch = new(1, 1);

    public BattleNetAccountClient()
    {
        _session = AccountStore.LoadSecret<Session>(Store);
        Status.SignedIn = _session is not null;
    }

    private sealed record Answer(string Games, string? Classic);

    public async Task<bool> SignInAsync(MainWindow owner)
    {
        var (answer, _) = await RunAsync(owner, visible: true);
        if (answer is null) return false;
        _session = new Session { SignedInAt = DateTime.UtcNow };
        AccountStore.SaveSecret(Store, _session);
        Status.SignedIn = true;
        Status.Error = null;
        // The sign-in already read the library; keep it, so the scan that follows does not open
        // the window a second time.
        Remember(Parse(answer));
        Log.Info("Battle.net: signed in");
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

        await _fetch.WaitAsync(ct);
        try
        {
            var (answer, signedOut) = await StoreLoginWindow.OnUiThread(() => RunAsync(null, visible: false));
            if (answer is null)
            {
                Status.Error = signedOut
                    ? "The Battle.net sign-in has expired. Sign in again"
                    : cached is not null ? $"Could not reach Battle.net; showing the library from {cached.FetchedAt.ToLocalTime():d MMM}" : "Could not reach Battle.net";
                Log.Info($"Battle.net: library fetch {(signedOut ? "found the session signed out" : "did not answer")}");
                return Report(cached);
            }
            var games = Parse(answer);
            Remember(games);
            Status.Error = null;
            Log.Info($"Battle.net: {games.Count} game(s) owned");
            return Report(_cache);
        }
        catch (Exception ex)
        {
            Status.Error = "Could not reach Battle.net";
            Log.Info($"Battle.net: library fetch failed ({ex.Message})");
            return Report(cached);
        }
        finally { _fetch.Release(); }
    }

    private void Remember(List<Game> games)
    {
        _cache = new OwnedCache { FetchedAt = DateTime.UtcNow, Games = games };
        _cacheLoaded = true;
        AccountStore.SaveCache(Store, _cache);
    }

    /// <summary>
    /// The window, visible for a sign-in or hidden for a renewal. Both end on the account site with
    /// the library read from inside the page, so the request carries the session exactly as the
    /// site's own scripts do. A hidden run that arrives at a sign-in form stops at once: nobody is
    /// there to type a password.
    /// </summary>
    private static async Task<(Answer? Answer, bool SignedOut)> RunAsync(System.Windows.Window? owner, bool visible)
    {
        Answer? captured = null;
        var signedOut = false;
        var ok = await StoreLoginWindow.RunAsync(owner, "Sign in to Battle.net", AccountStore.ProfileDir("battlenet"),
            visible ? AccountHome : RenewUrl,
            async core =>
            {
                if (!Uri.TryCreate(core.Source ?? "", UriKind.Absolute, out var u)
                    || !u.Host.EndsWith("battle.net", StringComparison.OrdinalIgnoreCase)) return false;
                if (u.AbsolutePath.Contains("/login", StringComparison.OrdinalIgnoreCase) && !u.AbsolutePath.Contains("oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    if (visible) return false;
                    var form = await StoreLoginWindow.EvalStringAsync(core,
                        "(function(){return document.querySelector('input[type=password], #accountName, input[name=accountName]') ? 'form' : '';})()");
                    if (form != "form") return false;
                    signedOut = true;
                    return true;
                }
                if (!u.Host.Equals("account.battle.net", StringComparison.OrdinalIgnoreCase)) return false;
                var games = await StoreLoginWindow.FetchTextAsync(core, GamesUrl);
                if (games is not { Status: 200 } g || !g.Body.Contains("gameAccounts", StringComparison.Ordinal)) return false;
                var classic = await StoreLoginWindow.FetchTextAsync(core, ClassicUrl);
                captured = new Answer(g.Body, classic is { Status: 200 } c ? c.Body : null);
                return true;
            }, visible);
        return (ok ? captured : null, signedOut);
    }

    /// <summary>
    /// The account's games: every game account the site lists, by its title id, and the classics by
    /// their CD keys -- one key for Warcraft III or Diablo II is the game, a second is its expansion
    /// (Playnite's reading of the same page). A title the table does not know is logged and left
    /// out, as Playnite does: there is no code to launch or install it by.
    /// </summary>
    private static List<Game> Parse(Answer answer)
    {
        var games = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        void Add(BattleNet.Product? p)
        {
            if (p is null) return;
            var g = BattleNet.Entry(p);
            if (!p.Classic) g.InstallUri = BattleNet.InstallUri(p);
            games.TryAdd(g.Id, g);
        }

        using (var doc = JsonDocument.Parse(answer.Games))
        {
            if (doc.RootElement.TryGetProperty("gameAccounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
                foreach (var a in accounts.EnumerateArray())
                {
                    var titleId = JsonNum.Long(a, "titleId");
                    var product = titleId is null ? null : BattleNet.Products.FirstOrDefault(p => p.TitleId == titleId);
                    if (product is null)
                    {
                        var name = a.TryGetProperty("localizedGameName", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "?";
                        Log.Info($"Battle.net: the account has {name} (title {titleId}), which Loungepad has no product code for");
                        continue;
                    }
                    Add(product);
                }
        }

        if (answer.Classic is { Length: > 0 } classicJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(classicJson);
                if (doc.RootElement.TryGetProperty("classicGames", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    var icons = list.EnumerateArray()
                        .Select(c => c.TryGetProperty("regionalGameFranchiseIconFilename", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "")
                        .ToList();
                    void Classic(string franchise, string game, string expansion)
                    {
                        var n = icons.Count(i => i.Contains(franchise, StringComparison.OrdinalIgnoreCase));
                        if (n >= 1) Add(BattleNet.ByCode(game));
                        if (n >= 2) Add(BattleNet.ByCode(expansion));
                    }
                    Classic("warcraft-iii", "W3C", "W3CX");
                    Classic("diablo-ii", "D2", "D2X");
                }
            }
            catch (JsonException) { /* the classics are a bonus */ }
        }
        return games.Values.ToList();
    }

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
}
