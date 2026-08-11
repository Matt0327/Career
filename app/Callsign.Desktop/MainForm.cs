using System.Drawing;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Callsign.Desktop;

/// <summary>The native window: a full-bleed WebView2 pointed at the in-process Callsign app.</summary>
internal sealed class MainForm : Form
{
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public MainForm(string url, string userDataFolder)
    {
        _url = url;
        _userDataFolder = userDataFolder;

        Text = "Callsign";
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 18, 22); // matches the app's dark shell so there's no white flash

        Controls.Add(_web);
        Shown += OnShown;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        try
        {
            // Keep the browser cache/profile in the per-user data folder (the exe dir may be read-only).
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Couldn't start the embedded browser (WebView2).\n\n" +
                "Install the Microsoft Edge WebView2 Runtime and try again.\n\n" + ex.Message,
                "Callsign", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }
}
