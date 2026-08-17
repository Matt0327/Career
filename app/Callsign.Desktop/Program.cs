using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Callsign.Host;
using Velopack;
using Velopack.Sources;

namespace Callsign.Desktop;

/// <summary>
/// The Callsign desktop app. Starts the whole web app in-process on a private loopback port and
/// shows it in a native window via WebView2 — one process, no browser, no visible localhost.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Velopack must run before anything else: it processes the install/update/uninstall hooks and,
        // when we apply an update below, relaunches into the new version. A no-op in a normal launch.
        VelopackApp.Build().Run();

        // On launch, silently check for and apply an update, then continue. Skips safely when the app
        // wasn't installed via Velopack (e.g. the portable build) or when the feed is unreachable.
        try { UpdateIfAvailable(); } catch { /* never let the updater block launch */ }

        // Per-user data folder for the SQLite save and the WebView2 cache. Deliberately "CallsignData",
        // NOT "Callsign": Velopack installs the app itself into %LocalAppData%\Callsign and manages that
        // folder on update — putting our data there collides with the installer. This lives outside it and
        // genuinely survives updates.
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CallsignData");
        Directory.CreateDirectory(dataDir);

        WebApplication? app = null;
        string? url = null;
        Exception? startupError = null;
        using var ready = new ManualResetEventSlim();

        // Build + start the web host OFF the UI thread. The ASP.NET host builder must not run on an
        // STA thread (WinForms requires [STAThread]); a default background thread is MTA.
        var hostThread = new Thread(() =>
        {
            try
            {
                app = CallsignWebApp.Build(new[]
                {
                    // Pin the content root to the app's own folder — never the process working
                    // directory, which may be a slow/odd path (a UNC share) that stalls startup.
                    $"--contentRoot={AppContext.BaseDirectory}",
                    $"--Db:Path={Path.Combine(dataDir, "callsign.db")}",
                    $"--Ui:Path={Path.Combine(AppContext.BaseDirectory, "wwwroot")}",
                    "--urls=http://127.0.0.1:0", // dynamic loopback port — nothing to clash with
                });
                app.Start();
                url = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            }
            catch (Exception ex)
            {
                startupError = ex;
            }
            finally
            {
                ready.Set();
            }
        })
        { IsBackground = true, Name = "Callsign-Host" };
        hostThread.Start();
        ready.Wait();

        if (url is null)
        {
            MessageBox.Show("Callsign couldn't start its engine.\n\n" + startupError,
                "Callsign — startup error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApplicationConfiguration.Initialize();
        using (var form = new MainForm(url, Path.Combine(dataDir, "WebView2")))
            Application.Run(form);

        app?.StopAsync().GetAwaiter().GetResult();
    }

    // The update feed lives in this GitHub repo's Releases — a public repo, so the app downloads updates
    // anonymously and there's no file-size limit on the packages. Override with the CALLSIGN_UPDATE_REPO env var.
    private static string UpdateRepo =>
        Environment.GetEnvironmentVariable("CALLSIGN_UPDATE_REPO")
        ?? "https://github.com/Matt0327/Career";

    private static void UpdateIfAvailable()
    {
        if (string.IsNullOrWhiteSpace(UpdateRepo)) return;
        var mgr = new UpdateManager(new GithubSource(UpdateRepo, null, false));
        if (!mgr.IsInstalled) return;                        // portable / dev run — nothing to update
        var update = mgr.CheckForUpdatesAsync().GetAwaiter().GetResult();
        if (update is null) return;                          // already current
        mgr.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
        mgr.ApplyUpdatesAndRestart(update);                  // relaunches into the new version; exits here
    }
}
