using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Poe2Verlay;

/// <summary>
/// The trade site (pathofexile.com/trade2) as an overlay over the game: no frame, no taskbar / alt-tab entry, always on
/// top, dragged by its bar and resized from its edges. Unlike the other overlays it takes the keyboard (searches get
/// typed); hiding it gives the game its focus back. Better Trading runs in it, and it shares its cookies (login,
/// Cloudflare check) with the price checks.
/// </summary>
internal sealed class TradeBrowserForm : Form
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = DarkUi.Back };
    readonly Panel _bar;
    readonly Label _title;
    AppSettings _settings;
    Task? _init;
    IntPtr _game;
    string _extension = "";

    /// <summary>Raised when the window is moved or resized, to be remembered.</summary>
    public event Action<Rectangle>? BoundsSaved;
    /// <summary>The trade page of the current league.</summary>
    public Func<string>? Home;

    public TradeBrowserForm(AppSettings settings)
    {
        _settings = settings;
        DarkUi.StyleWindow(this, "poe2verlay – trade");
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = DarkUi.Header; // the padding shows as a thin frame, and is where it gets resized
        Padding = new Padding(5);
        MinimumSize = new Size(480, 320);

        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, BackColor = DarkUi.Back, WrapContents = false };
        nav.Controls.AddRange(new Control[]
        {
            BarButton("◀", "Back", () => _web.CoreWebView2?.GoBack()),
            BarButton("▶", "Forward", () => _web.CoreWebView2?.GoForward()),
            BarButton("⟳", "Reload", () => _web.CoreWebView2?.Reload()),
            BarButton("⌂", "Trade search for the current league", () => Navigate(Home?.Invoke())),
        });
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, BackColor = DarkUi.Back, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        actions.Controls.AddRange(new Control[]
        {
            BarButton("✕", "Hide (back to the game)", HideToGame),
            BarButton("⧉", "Open this page in your normal browser", OpenOutside),
        });
        _title = new Label { Dock = DockStyle.Fill, ForeColor = DarkUi.Dim, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(8, 0, 0, 0) };
        _bar = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(36), BackColor = DarkUi.Back, Padding = new Padding(6, 5, 6, 3) };
        _bar.Controls.Add(_title);
        _bar.Controls.Add(nav);
        _bar.Controls.Add(actions);

        Controls.Add(_web);
        Controls.Add(_bar);

        // No title bar: drag it by its bar.
        foreach (var c in new Control[] { _bar, _title, nav, actions }) c.MouseDown += DragWindow;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; // keep the page and the login, just hide
            HideToGame();
        };
        ResizeEnd += (_, _) => BoundsSaved?.Invoke(Bounds);

        DarkUi.PlaceWindow(this, settings.TradeBrowserBounds, 120, 60, 1180, 860);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Like the price labels: never in the screenshots that get OCR'd.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>The frame (form padding) resizes the window.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTCLIENT = 1;
        base.WndProc(ref m);
        if (m.Msg != WM_NCHITTEST || (int)m.Result != HTCLIENT) return;
        var lp = m.LParam.ToInt64();
        var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        var grip = Padding.Left + 2;
        bool l = p.X < grip, r = p.X >= ClientSize.Width - grip, t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
        m.Result = (IntPtr)(t && l ? 13 : t && r ? 14 : b && l ? 16 : b && r ? 17 : l ? 10 : r ? 11 : t ? 12 : b ? 15 : HTCLIENT);
    }

    void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, (IntPtr)2 /* HTCAPTION */, IntPtr.Zero);
    }

    Button BarButton(string text, string tip, Action onClick)
    {
        var b = DarkUi.Button(text);
        b.Margin = new Padding(0, 0, 4, 0);
        b.Padding = new Padding(6, 0, 6, 0);
        b.Click += (_, _) => onClick();
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    /// <summary>Shows the browser on top of the game with the keyboard, on <paramref name="url"/> when given.</summary>
    public async Task ShowBrowserAsync(string? url)
    {
        var game = GameWindow.ForegroundPoe();
        if (game != IntPtr.Zero) _game = game;
        if (!Visible) Show();
        Native.ForceForeground(Handle);
        Activate();
        try
        {
            await (_init ??= InitAsync());
            if (url != null) Navigate(url);
            else if (_web.Source == null || _web.Source.AbsoluteUri == "about:blank") Navigate(Home?.Invoke());
            _web.Focus();
        }
        catch (Exception ex)
        {
            _init = null; // try again next time
            _title.Text = ex.Message;
            _title.ForeColor = DarkUi.Down;
            Log.Write("trade browser: " + ex);
        }
    }

    /// <summary>Hides it and gives the keyboard back to the game.</summary>
    public void HideToGame()
    {
        Hide();
        if (_game != IntPtr.Zero) Native.SetForegroundWindow(_game);
    }

    async Task InitAsync()
    {
        var env = await TradeWeb.EnvironmentAsync();
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2!;
        core.Settings.IsStatusBarEnabled = false;
        // Links that want a new tab: the trade site stays in here, anything else goes to the normal browser.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsTradeSite(e.Uri)) core.Navigate(e.Uri);
            else OpenUrl(e.Uri);
        };
        core.DocumentTitleChanged += (_, _) => UpdateTitle();

        try
        {
            _extension = await TradeWeb.SetBetterTradingAsync(core.Profile, _settings.TradeBetterTrading, env.BrowserVersionString);
        }
        catch (Exception ex)
        {
            _extension = "Better Trading not loaded";
            Log.Write("better trading: " + ex);
        }
        UpdateTitle();
    }

    /// <summary>Settings changed: Better Trading on or off.</summary>
    public async Task ApplySettingsAsync(AppSettings settings)
    {
        var changed = settings.TradeBetterTrading != _settings.TradeBetterTrading;
        _settings = settings;
        if (!changed || _web.CoreWebView2 is not { } core) return;
        try
        {
            _extension = await TradeWeb.SetBetterTradingAsync(core.Profile, _settings.TradeBetterTrading, core.Environment.BrowserVersionString);
            core.Reload();
        }
        catch (Exception ex)
        {
            Log.Write("better trading: " + ex);
        }
        UpdateTitle();
    }

    void UpdateTitle()
    {
        var page = _web.CoreWebView2?.DocumentTitle ?? "";
        _title.ForeColor = DarkUi.Dim;
        _title.Text = string.Join("   ·   ", new[] { page, _extension }.Where(t => t.Length > 0));
    }

    void Navigate(string? url)
    {
        if (string.IsNullOrEmpty(url) || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.Navigate(url);
    }

    void OpenOutside()
    {
        var url = _web.Source?.AbsoluteUri;
        if (string.IsNullOrEmpty(url) || url == "about:blank") return;
        OpenUrl(url);
        HideToGame();
    }

    static bool IsTradeSite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("pathofexile.com", StringComparison.OrdinalIgnoreCase);

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open url: " + ex.Message); }
    }
}
