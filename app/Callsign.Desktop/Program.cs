using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Callsign.Host;
using Velopack;

namespace Callsign.Desktop;

/// <summary>
/// The Callsign desktop app. A launcher window comes up first — it shows the update happening live and a
/// news strip, then hands off to the app itself (the whole web app running in-process on a private loopback
/// port, shown in a native window via WebView2 — one process, no browser, no visible localhost).
/// </summary>
internal static class Program
{
    // A fixed high loopback port, so the app's origin is stable between launches (browser storage persists).
    // Fall back to an OS-assigned port only if it's already in use — rare, and only that session's storage resets.
    private static int PickStableLoopbackPort()
    {
        const int preferred = 52765;
        try
        {
            using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, preferred);
            probe.Start();
            probe.Stop();
            return preferred;
        }
        catch { return 0; }
    }

    [STAThread]
    private static void Main()
    {
        // Velopack must run before anything else: it processes the install/update/uninstall hooks and,
        // after the launcher applies an update, relaunches into the new version. A no-op in a normal launch.
        VelopackApp.Build().Run();

        // Per-user data folder for the SQLite save and the WebView2 cache. Deliberately "BentoFlyData",
        // NOT "BentoFly": Velopack installs the app itself into %LocalAppData%\BentoFly and manages that
        // folder on update — putting our data there collides with the installer. This lives outside it and
        // genuinely survives updates.
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = Path.Combine(localApp, "BentoFlyData");
        // One-time rename from the pre-rebrand "CallsignData" folder, so an existing save (its SQLite db and its
        // WebView2 cache) moves across whole rather than being orphaned. Best-effort: if the move can't complete
        // (e.g. a file is locked), fall back to the old folder so the player never loses their career.
        var legacyDataDir = Path.Combine(localApp, "CallsignData");
        if (!Directory.Exists(dataDir) && Directory.Exists(legacyDataDir))
        {
            try { Directory.Move(legacyDataDir, dataDir); }
            catch { dataDir = legacyDataDir; }
        }
        Directory.CreateDirectory(dataDir);

        // The SQLite save file, renamed from the pre-rebrand "callsign.db". This runs BEFORE the host opens the
        // DB (the host thread starts below), so it's a safe file move — and we carry any WAL/journal sidecars with
        // it so no committed data is left behind. Best-effort: if it can't move, keep using the old file.
        var dbPath = Path.Combine(dataDir, "bentofly.db");
        var legacyDb = Path.Combine(dataDir, "callsign.db");
        if (!File.Exists(dbPath) && File.Exists(legacyDb))
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                try { if (File.Exists(legacyDb + suffix)) File.Move(legacyDb + suffix, dbPath + suffix); } catch { }
            if (!File.Exists(dbPath)) dbPath = legacyDb;
        }

        // Build + start the web host OFF the UI thread, exposing its loopback URL through a Task the launcher
        // awaits to enable Play. The ASP.NET host builder must not run on an STA thread (WinForms requires
        // [STAThread]); a default background thread is MTA.
        WebApplication? app = null;
        var hostReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostThread = new Thread(() =>
        {
            try
            {
                app = CallsignWebApp.Build(new[]
                {
                    // Pin the content root to the app's own folder — never the process working
                    // directory, which may be a slow/odd path (a UNC share) that stalls startup.
                    $"--contentRoot={AppContext.BaseDirectory}",
                    $"--Db:Path={dbPath}",
                    $"--Ui:Path={Path.Combine(AppContext.BaseDirectory, "wwwroot")}",
                    // A STABLE loopback port, so the WebView2 origin (which includes the port) is the SAME every
                    // launch. With a dynamic port, browser storage — the theme choice and the "tutorial seen"
                    // flags — is keyed to a new origin each run and looks reset on every startup.
                    $"--urls=http://127.0.0.1:{PickStableLoopbackPort()}",
                });
                app.Start();
                var url = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
                hostReady.TrySetResult(url);
            }
            catch (Exception ex)
            {
                hostReady.TrySetException(ex);
            }
        })
        { IsBackground = true, Name = "Callsign-Host" };
        hostThread.Start();

        ApplicationConfiguration.Initialize();
        var webView2Dir = Path.Combine(dataDir, "WebView2");

        // The launcher: shows the update + news, and returns the app URL once the player clicks Play (or null
        // if they closed it, or if an update relaunched the process before we got here).
        string? launchUrl;
        using (var launcher = new LauncherForm(hostReady.Task, webView2Dir))
        {
            Application.Run(launcher);
            launchUrl = launcher.LaunchUrl;
        }

        if (launchUrl is not null)
            using (var form = new MainForm(launchUrl, webView2Dir))
                Application.Run(form);

        app?.StopAsync().GetAwaiter().GetResult();
    }
}
