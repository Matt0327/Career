using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Callsign.Host;

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
        // Per-user data folder for the SQLite save and the WebView2 cache (survives app updates).
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Callsign");
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
}
