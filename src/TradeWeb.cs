using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Poe2Verlay;

/// <summary>The trade site turned the request away; the message says what to do.</summary>
internal sealed class TradeException(string message) : Exception(message);

/// <summary>
/// The Edge WebView2 runtime shared by the trade browser overlay and the trade price checks. One profile in the data
/// folder: log in to pathofexile.com or pass a Cloudflare check in the browser once and the price checks use the same
/// cookies. Better Trading (the trade site extension) is loaded into it from the Chrome Web Store.
/// </summary>
internal static class TradeWeb
{
    public const string Site = "https://www.pathofexile.com";
    /// <summary>"Better PathOfExile Trading" on the Chrome Web Store.</summary>
    public const string BetterTradingStoreId = "fhlinfpmdlijegjlpgedcmglkakaghnk";

    static Task<CoreWebView2Environment>? _env;

    public static string ProfileDir => Path.Combine(AppSettings.Dir, "webview");
    static string ExtensionDir => Path.Combine(AppSettings.Dir, "extensions", BetterTradingStoreId);
    static string ExtensionIdFile => Path.Combine(AppSettings.Dir, "extensions", "better-trading.id");

    public static Task<CoreWebView2Environment> EnvironmentAsync() => _env ??= CreateAsync();

    static async Task<CoreWebView2Environment> CreateAsync()
    {
        try
        {
            Directory.CreateDirectory(ProfileDir);
            var options = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true,
                // The price check page lives in a window nobody sees; Chromium would otherwise freeze its scripts,
                // and with them the Cloudflare check that has to run there.
                AdditionalBrowserArguments = "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling " +
                                             "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows",
            };
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileDir, options);
            Log.Write($"webview2 {env.BrowserVersionString}, profile {ProfileDir}");
            return env;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _env = null;
            throw new TradeException("Microsoft Edge WebView2 Runtime is missing: install it from microsoft.com/edge/webview2");
        }
        catch
        {
            _env = null;
            throw;
        }
    }

    public static string SearchUrl(string league, string? searchId = null) =>
        $"{Site}/trade2/search/poe2/{Uri.EscapeDataString(league)}" + (searchId == null ? "" : "/" + searchId);

    /// <summary>
    /// Better Trading on (downloaded from the Chrome Web Store the first time) or off. Returns a status for the
    /// browser's title bar.
    /// </summary>
    public static async Task<string> SetBetterTradingAsync(CoreWebView2Profile profile, bool enabled, string browserVersion)
    {
        var installed = await profile.GetBrowserExtensionsAsync();
        var knownId = File.Exists(ExtensionIdFile) ? File.ReadAllText(ExtensionIdFile).Trim() : null;
        var ext = installed.FirstOrDefault(e => e.Id == knownId) ??
                  installed.FirstOrDefault(e => e.Name.Contains("Better", StringComparison.OrdinalIgnoreCase) &&
                                                e.Name.Contains("Trad", StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            if (ext is { IsEnabled: true }) await ext.EnableAsync(false);
            return "";
        }
        if (ext != null)
        {
            if (!ext.IsEnabled) await ext.EnableAsync(true);
            return "Better Trading";
        }

        if (!File.Exists(Path.Combine(ExtensionDir, "manifest.json"))) await DownloadExtensionAsync(BetterTradingStoreId, ExtensionDir, browserVersion);
        ext = await profile.AddBrowserExtensionAsync(ExtensionDir);
        File.WriteAllText(ExtensionIdFile, ext.Id);
        Log.Write($"better trading: added '{ext.Name}' ({ext.Id})");
        return "Better Trading";
    }

    /// <summary>Removes the downloaded copy so the next start of the browser fetches the current version.</summary>
    public static async Task UpdateBetterTradingAsync(CoreWebView2Profile profile)
    {
        var knownId = File.Exists(ExtensionIdFile) ? File.ReadAllText(ExtensionIdFile).Trim() : null;
        foreach (var e in await profile.GetBrowserExtensionsAsync())
            if (e.Id == knownId) await e.RemoveAsync();
        if (Directory.Exists(ExtensionDir)) Directory.Delete(ExtensionDir, true);
        if (File.Exists(ExtensionIdFile)) File.Delete(ExtensionIdFile);
    }

    static async Task DownloadExtensionAsync(string storeId, string folder, string browserVersion)
    {
        var major = browserVersion.Split('.')[0];
        var url = "https://clients2.google.com/service/update2/crx?response=redirect&acceptformat=crx2,crx3" +
                  $"&prodversion={major}.0&x=id%3D{storeId}%26installsource%3Dondemand%26uc";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(PriceService.UserAgent);
        var crx = await http.GetByteArrayAsync(url);

        // CRX = "Cr24", version, header (keys and signature), then a plain zip.
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            throw new TradeException("Better Trading download failed (not an extension package)");
        var version = BitConverter.ToInt32(crx, 4);
        var zipStart = version switch
        {
            3 => 12 + BitConverter.ToInt32(crx, 8),
            2 => 16 + BitConverter.ToInt32(crx, 8) + BitConverter.ToInt32(crx, 12),
            _ => throw new TradeException($"Better Trading download failed (package version {version})"),
        };

        var temp = folder + ".tmp";
        if (Directory.Exists(temp)) Directory.Delete(temp, true);
        using (var zip = new ZipArchive(new MemoryStream(crx, zipStart, crx.Length - zipStart), ZipArchiveMode.Read))
            zip.ExtractToDirectory(temp);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(temp, folder);
        Log.Write($"better trading: downloaded {crx.Length / 1024} KB to {folder}");
    }
}

/// <summary>One answer of the trade API.</summary>
internal sealed record TradeResponse(int Status, string Body, Dictionary<string, string> Headers);

/// <summary>
/// The trade API, called with fetch() from a hidden page of pathofexile.com (same origin, the browser's cookies), so
/// Cloudflare sees a browser. Keeps to the rate limits the site announces in its X-Rate-Limit headers.
/// </summary>
internal sealed class TradeApi : IDisposable
{
    readonly HostForm _host = new();
    readonly WebView2 _view = new() { Dock = DockStyle.Fill };
    readonly Dictionary<string, TaskCompletionSource<TradeResponse>> _pending = new();
    readonly TradeRateLimiter _limits = new();
    Task? _ready;

    public TradeApi()
    {
        _host.Controls.Add(_view);
        _host.Show(); // off screen; a WebView2 needs a live window
    }

    /// <summary>Loads the site once; again after a failure.</summary>
    public Task EnsureReadyAsync()
    {
        if (_ready == null || _ready.IsFaulted || _ready.IsCanceled) _ready = InitAsync();
        return _ready;
    }

    async Task InitAsync()
    {
        if (_view.CoreWebView2 == null)
        {
            await _view.EnsureCoreWebView2Async(await TradeWeb.EnvironmentAsync());
            _view.CoreWebView2!.WebMessageReceived += OnMessage;
            _view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        }

        // A small JSON page of the site: the fetch() calls below run from its origin. A fresh profile first gets
        // Cloudflare's check page, which solves itself in a few seconds and reloads the real page.
        var core = _view.CoreWebView2;
        var events = new Queue<CoreWebView2NavigationCompletedEventArgs>();
        var signal = new SemaphoreSlim(0);
        void Completed(object? s, CoreWebView2NavigationCompletedEventArgs e) { events.Enqueue(e); signal.Release(); }
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(TradeWeb.Site + "/api/trade2/data/leagues");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            int? lastStatus = null;
            while (DateTime.UtcNow < deadline)
            {
                if (!await signal.WaitAsync(deadline - DateTime.UtcNow)) break;
                var e = events.Dequeue();
                lastStatus = e.HttpStatusCode;
                if (e.IsSuccess && e.HttpStatusCode == 200 && core.Source.Contains("/api/trade2/", StringComparison.Ordinal)) return;
                if (!e.IsSuccess && e.HttpStatusCode is not (403 or 503) && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    throw new TradeException($"pathofexile.com unreachable ({e.WebErrorStatus})");
            }
            Log.Write($"trade page: still {lastStatus?.ToString(CultureInfo.InvariantCulture) ?? "no answer"} after 30 s, title '{core.DocumentTitle}'");
            throw new TradeException(lastStatus is 403 or 503
                ? "pathofexile.com wants a browser check: open the trade browser once"
                : "pathofexile.com did not answer (30 s)");
        }
        finally
        {
            core.NavigationCompleted -= Completed;
        }
    }

    void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString() ?? "";
            if (!_pending.Remove(id, out var tcs)) return;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in root.GetProperty("headers").EnumerateObject()) headers[h.Name] = h.Value.GetString() ?? "";
            tcs.TrySetResult(new TradeResponse(root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? "", headers));
        }
        catch (Exception ex)
        {
            Log.Write("trade message: " + ex.Message);
        }
    }

    public Task<TradeResponse> GetAsync(string kind, string path) => SendAsync(kind, "GET", path, null);
    public Task<TradeResponse> PostAsync(string kind, string path, string json) => SendAsync(kind, "POST", path, json);

    /// <param name="kind">Rate limit bucket: "search", "fetch" or "data".</param>
    async Task<TradeResponse> SendAsync(string kind, string method, string path, string? body)
    {
        await EnsureReadyAsync();
        await _limits.WaitAsync(kind);

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<TradeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var bodyJs = body == null ? "undefined" : JsonSerializer.Serialize(body);
        var script = $$"""
            (async () => {
              const id = {{JsonSerializer.Serialize(id)}};
              try {
                const r = await fetch({{JsonSerializer.Serialize(path)}}, { method: {{JsonSerializer.Serialize(method)}},
                  credentials: 'include', headers: { 'Content-Type': 'application/json' }, body: {{bodyJs}} });
                const headers = {};
                r.headers.forEach((v, k) => { if (k.startsWith('x-rate-limit') || k === 'retry-after') headers[k] = v; });
                window.chrome.webview.postMessage({ id, status: r.status, headers, body: await r.text() });
              } catch (e) {
                window.chrome.webview.postMessage({ id, status: 0, headers: {}, body: String(e) });
              }
            })();
            """;
        await _view.CoreWebView2.ExecuteScriptAsync(script);
        var finished = await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        _limits.Sent(kind);
        if (finished != tcs.Task)
        {
            _pending.Remove(id);
            throw new TradeException("the trade site did not answer (30 s)");
        }

        var response = tcs.Task.Result;
        _limits.Update(kind, response.Headers);
        if (response.Status == 429)
            throw new TradeException($"trade rate limit: try again in {_limits.SecondsBlocked(kind)} s");
        if (response.Status is 403 or 503 && !response.Body.TrimStart().StartsWith('{'))
        {
            _ready = null; // the next call loads the page again (after the check is passed in the browser)
            throw new TradeException("pathofexile.com wants a browser check: open the trade browser once");
        }
        return response;
    }

    public void Dispose()
    {
        _view.Dispose();
        _host.Dispose();
    }

    /// <summary>Never seen: off screen, no taskbar entry, no focus, not in screen captures.</summary>
    sealed class HostForm : Form
    {
        public HostForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Size = new Size(400, 300);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }
    }
}

/// <summary>
/// The trade site's rate limits: "X-Rate-Limit-Ip: 8:10:60,15:60:120" = 8 requests per 10 s (else 60 s timeout),
/// 15 per 60 s... and "-State" with the current counts. Waits before a request would go over a limit, stays one
/// under it, and stops while the site says we are timed out.
/// </summary>
internal sealed class TradeRateLimiter
{
    sealed class Bucket
    {
        public List<(int Hits, int Seconds)> Rules = new();
        public readonly List<DateTime> Sent = new();
        public DateTime BlockedUntil;
    }

    readonly Dictionary<string, Bucket> _buckets = new();

    Bucket Get(string kind)
    {
        if (!_buckets.TryGetValue(kind, out var b))
        {
            // Until the site has told us its limits: conservative guesses.
            b = new Bucket { Rules = kind == "search" ? new() { (4, 10), (12, 60) } : new() { (8, 10) } };
            _buckets[kind] = b;
        }
        return b;
    }

    public async Task WaitAsync(string kind)
    {
        var b = Get(kind);
        while (true)
        {
            var now = DateTime.UtcNow;
            var wait = b.BlockedUntil - now;
            foreach (var (hits, seconds) in b.Rules)
            {
                var window = b.Sent.Where(t => (now - t).TotalSeconds < seconds).OrderBy(t => t).ToList();
                var allowed = Math.Max(1, hits - 1);
                if (window.Count >= allowed)
                {
                    var until = window[window.Count - allowed].AddSeconds(seconds) - now;
                    if (until > wait) wait = until;
                }
            }
            if (wait <= TimeSpan.Zero) return;
            if (wait > TimeSpan.FromSeconds(20)) throw new TradeException($"trade rate limit: try again in {(int)wait.TotalSeconds + 1} s");
            await Task.Delay(wait + TimeSpan.FromMilliseconds(50));
        }
    }

    public void Sent(string kind)
    {
        var b = Get(kind);
        b.Sent.Add(DateTime.UtcNow);
        b.Sent.RemoveAll(t => (DateTime.UtcNow - t).TotalHours > 1);
    }

    public int SecondsBlocked(string kind) => Math.Max(1, (int)Math.Ceiling((Get(kind).BlockedUntil - DateTime.UtcNow).TotalSeconds));

    public void Update(string kind, Dictionary<string, string> headers)
    {
        var b = Get(kind);
        if (headers.TryGetValue("retry-after", out var retry) && int.TryParse(retry, out var s))
            b.BlockedUntil = DateTime.UtcNow.AddSeconds(s);
        if (!headers.TryGetValue("x-rate-limit-rules", out var names)) return;

        var rules = new List<(int, int)>();
        foreach (var name in names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (headers.TryGetValue($"x-rate-limit-{name}", out var spec))
            {
                foreach (var rule in spec.Split(','))
                {
                    var p = rule.Split(':');
                    if (p.Length >= 2 && int.TryParse(p[0], out var hits) && int.TryParse(p[1], out var period)) rules.Add((hits, period));
                }
            }
            if (headers.TryGetValue($"x-rate-limit-{name}-state", out var state))
            {
                foreach (var rule in state.Split(','))
                {
                    var p = rule.Split(':');
                    if (p.Length >= 3 && int.TryParse(p[2], out var restricted) && restricted > 0)
                    {
                        var until = DateTime.UtcNow.AddSeconds(restricted);
                        if (until > b.BlockedUntil) b.BlockedUntil = until;
                    }
                }
            }
        }
        if (rules.Count > 0 && !rules.SequenceEqual(b.Rules))
        {
            b.Rules = rules;
            Log.Write($"trade rate limits ({kind}): " + string.Join(", ", rules.Select(r => $"{r.Item1} per {r.Item2} s")));
        }
    }
}
