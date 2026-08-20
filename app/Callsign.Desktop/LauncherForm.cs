using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Velopack;
using Velopack.Sources;

namespace Callsign.Desktop;

/// <summary>
/// The launcher: a branded, frameless window (WebView2 → launcher.html) that shows the update happening
/// live — check, download-with-progress, apply+restart — alongside a news strip, then hands off to the app
/// when it's current and the engine is ready. Modelled on a game launcher (Battle.net / Epic / Riot).
/// </summary>
internal sealed class LauncherForm : Form
{
    private readonly Task<string> _hostUrlTask;   // resolves to the in-process app URL once the engine is up
    private readonly string _userDataFolder;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _startupRun;

    /// <summary>Set to the app URL when the player clicks Play; null if they closed the launcher instead.</summary>
    public string? LaunchUrl { get; private set; }

    public LauncherForm(Task<string> hostUrlTask, string userDataFolder)
    {
        _hostUrlTask = hostUrlTask;
        _userDataFolder = userDataFolder;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 600);
        MinimumSize = new Size(980, 600);
        BackColor = Color.FromArgb(15, 18, 22); // matches the launcher bg so there's no white flash
        ShowInTaskbar = true;
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "callsign.ico")); } catch { /* dev run without the icon next to the exe */ }

        Controls.Add(_web);
        Shown += OnShown;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;

            // Serve the bundled launcher folder from a virtual host, so launcher.html can load its assets
            // (news.json, the splash image) and fetch works over https.
            var launcherDir = Path.Combine(AppContext.BaseDirectory, "launcher");
            core.SetVirtualHostNameToFolderMapping("launcher.callsign", launcherDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += OnWebMessage;
            _web.NavigationCompleted += async (_, ev) =>
            {
                // If launcher.html itself failed to load, don't drive a dead page (a frameless window with no
                // buttons and no OS chrome would be unclosable) — fall straight through to the app.
                if (!ev.IsSuccess) { LaunchUrl = await SafeHostUrlAsync(); Close(); return; }
                await RunStartupAsync();
            };
            core.Navigate("https://launcher.callsign/launcher.html");
        }
        catch
        {
            // WebView2 runtime missing or launcher failed to init — don't strand the player on a dead window.
            // Fall straight through to the app (MainForm guides them to the WebView2 download if that's the cause);
            // if the engine ALSO failed there's nothing to show, so say so rather than exiting silently.
            LaunchUrl = await SafeHostUrlAsync();
            if (LaunchUrl is null)
                MessageBox.Show(
                    "Callsign couldn't start: the display component (WebView2) and the engine both failed to start.\n\n" +
                    "Install or repair the Microsoft Edge WebView2 runtime, then relaunch Callsign.",
                    "Callsign — startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    // ── the startup sequence the launcher shows off ──────────────────────────────
    private async Task RunStartupAsync()
    {
        if (_startupRun) return; // NavigationCompleted can fire more than once — the sequence runs just once
        _startupRun = true;

        // Tell the page who it is: version, hero image, and an optional live news feed URL.
        string? version = null;
        try { version = new UpdateManager(new GithubSource(UpdateRepo, null, false)).CurrentVersion?.ToString(); } catch { }
        PostJson(new { type = "init", version, bg = "https://launcher.callsign/splash.png", newsUrl = NewsUrl });

        // The update is best-effort — a throw anywhere in it (a malformed install, a flaky feed) must never
        // crash the launcher (this runs from an async-void handler), it just means "carry on with what we have".
        try { await RunUpdateAsync(); } catch { /* fall through to launch the current build */ }
        await AwaitEngineAsync();        // enable Play once the in-process app is listening
    }

    private async Task RunUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(UpdateRepo)) return;
        UpdateManager mgr;
        try { mgr = new UpdateManager(new GithubSource(UpdateRepo, null, false)); }
        catch { return; }
        if (!mgr.IsInstalled) return; // dev / portable build — nothing to update

        PostStatus("checking", "Checking for updates…", null, "Checking…");
        UpdateInfo? update;
        try { update = await mgr.CheckForUpdatesAsync(); }
        catch { return; }               // feed unreachable — carry on to the current build
        if (update is null) return;     // already current

        var ver = update.TargetFullRelease.Version.ToString();
        PostJson(new { type = "notes", title = $"Update {ver} is ready", body = "Downloading the latest Callsign now — it installs and relaunches on its own." });
        PostStatus("downloading", $"Downloading update {ver}…", 0, "Updating…");
        try
        {
            await mgr.DownloadUpdatesAsync(update, pct => PostStatus("downloading", $"Downloading update {ver}… {pct}%", pct, $"Updating {pct}%"));
        }
        catch
        {
            PostStatus("error", "Couldn't download the update — starting the current version.", null, "Play");
            return;
        }
        PostStatus("downloading", "Installing update — Callsign will relaunch…", -1, "Updating…");
        try
        {
            // Apply SILENTLY: ApplyUpdatesAndRestart pops Velopack's own native "Installing update…" progress
            // window on top of our branded launcher — the whole update belongs in the launcher only. The
            // lower-level call with silent:true swaps the files with no UI; its helper waits for THIS process to
            // exit first (it can't replace running files), so we exit ourselves exactly as
            // ApplyUpdatesAndRestart would, and it relaunches into the new version.
            mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true);
            Environment.Exit(0);
        }
        catch { PostStatus("error", "Couldn't install the update — starting the current version.", null, "Play"); }
    }

    private async Task AwaitEngineAsync()
    {
        PostStatus("starting", "Starting the engine…", null, "Starting…");
        try
        {
            await _hostUrlTask;
            PostStatus("ready", "Ready to fly.", null, "Play");
        }
        catch
        {
            // The engine failed — the app can't run this session. Closing the launcher exits (LaunchUrl stays null).
            PostStatus("error", "The engine didn't start cleanly — please relaunch Callsign.", null, "Close");
        }
    }

    // ── messages from the page ───────────────────────────────────────────────────
    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? s = null;
        try { s = e.TryGetWebMessageAsString(); } catch { /* it's a JSON object, handled below */ }

        switch (s)
        {
            case "play":
                if (_hostUrlTask.IsCompletedSuccessfully) { LaunchUrl = _hostUrlTask.Result; Close(); }
                else if (_hostUrlTask.IsFaulted) { LaunchUrl = null; Close(); } // engine failed — closing quits
                else { LaunchUrl = await SafeHostUrlAsync(); Close(); }         // still starting — wait then go
                return;
            case "minimize":
                WindowState = FormWindowState.Minimized;
                return;
            case "quit":
                LaunchUrl = null;
                Close();
                return;
            case "drag":
                BeginNativeDrag();
                return;
        }

        // Object messages, e.g. { cmd: "open", url: "https://…" } from a news card.
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("cmd", out var cmd) && cmd.GetString() == "open"
                && root.TryGetProperty("url", out var urlProp) && urlProp.GetString() is { } url
                && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
            }
        }
        catch { /* ignore malformed messages */ }
    }

    private async Task<string?> SafeHostUrlAsync()
    {
        try { return await _hostUrlTask; } catch { return null; }
    }

    // ── posting to the page ──────────────────────────────────────────────────────
    private void PostStatus(string phase, string text, int? pct, string play)
        => PostJson(new { type = "status", phase, text, pct, play });

    private void PostJson(object payload)
    {
        // Serialize off whichever thread we're on (a Velopack progress callback runs on a background thread),
        // but only touch the WebView2 control on the UI thread — reading/posting to it off-thread is illegal.
        string json;
        try { json = JsonSerializer.Serialize(payload); }
        catch { return; }
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(() => TryPost(json));
            else TryPost(json);
        }
        catch { /* handle destroyed while a background callback was in flight */ }
    }

    private void TryPost(string json)
    {
        try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch { /* window closing */ }
    }

    // ── frameless window drag (grab the custom title bar → run the OS move loop) ──
    private void BeginNativeDrag()
    {
        try
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        catch { /* not fatal — the window just won't drag */ }
    }

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // The update feed — the GitHub Releases of this repo (public, anonymous download). Override via env.
    private static string UpdateRepo =>
        Environment.GetEnvironmentVariable("CALLSIGN_UPDATE_REPO") ?? "https://github.com/Matt0327/Career";

    // Optional live news feed (JSON). If unset or unreachable, the launcher shows its bundled news.json.
    private static string? NewsUrl => Environment.GetEnvironmentVariable("CALLSIGN_NEWS_URL");
}
