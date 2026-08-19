using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Callsign.Desktop;

/// <summary>
/// The native app window: a frameless, resizable host with a full-bleed WebView2 pointed at the in-process
/// Callsign app. The window chrome (the CALL·SIGN top bar, minimize / maximize / close, drag) lives in the
/// web UI and talks back here over the WebView2 message bridge — so the desktop matches the launcher.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly string _url;
    private readonly string _userDataFolder;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private const int EdgeGrip = 6; // px grab zone at the edges; the app's dark ground makes it read as no border

    public MainForm(string url, string userDataFolder)
    {
        _url = url;
        _userDataFolder = userDataFolder;

        Text = "Callsign";
        FormBorderStyle = FormBorderStyle.None;   // the top bar is drawn by the web UI (like the launcher)
        MinimumSize = new Size(960, 640);
        ClientSize = new Size(1280, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(15, 18, 22);   // matches the app's dark shell (and the thin resize border)
        Padding = new Padding(EdgeGrip);            // the form keeps a thin edge the WebView2 doesn't cover, so it hit-tests for resize
        try { Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "callsign.ico")); } catch { }

        Controls.Add(_web);
        Shown += OnShown;
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Settings.AreDevToolsEnabled = false; // it's an app, not a browser tab
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            // Almost always: the WebView2 runtime isn't installed. Point the user at the free download.
            var choice = MessageBox.Show(
                "Callsign needs the Microsoft Edge WebView2 runtime, which doesn't seem to be installed.\n\n" +
                "Click OK to open the free download page, then run Callsign again.\n\n(" + ex.Message + ")",
                "Callsign — WebView2 required", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (choice == DialogResult.OK)
                try { Process.Start(new ProcessStartInfo("https://developer.microsoft.com/microsoft-edge/webview2/") { UseShellExecute = true }); }
                catch { /* the user can search for it manually */ }
            Close();
        }
    }

    // The web title bar posts these to drive the (frameless) window.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? s = null;
        try { s = e.TryGetWebMessageAsString(); } catch { return; }
        switch (s)
        {
            case "win:drag":
                try { ReleaseCapture(); SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero); } catch { }
                break;
            case "win:minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "win:maximize":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "win:close":
                Close();
                break;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // EdgeGrip a frameless window: report the edge/corner zones so Windows runs its own resize loop.
        if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
                int w = ClientSize.Width, h = ClientSize.Height;
                bool l = p.X <= EdgeGrip, r = p.X >= w - EdgeGrip, t = p.Y <= EdgeGrip, b = p.Y >= h - EdgeGrip;
                m.Result = (IntPtr)(
                    t && l ? HTTOPLEFT : t && r ? HTTOPRIGHT : b && l ? HTBOTTOMLEFT : b && r ? HTBOTTOMRIGHT :
                    l ? HTLEFT : r ? HTRIGHT : t ? HTTOP : b ? HTBOTTOM : HTCLIENT);
            }
            return;
        }

        // A frameless window maximizes over the taskbar unless we clamp it to the monitor's work area.
        if (m.Msg == WM_GETMINMAXINFO)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var mon = MonitorFromWindow(Handle, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                var work = mi.rcWork; var area = mi.rcMonitor;
                mmi.ptMaxPosition = new POINT { x = work.left - area.left, y = work.top - area.top };
                mmi.ptMaxSize = new POINT { x = work.right - work.left, y = work.bottom - work.top };
                mmi.ptMinTrackSize = new POINT { x = MinimumSize.Width, y = MinimumSize.Height };
                Marshal.StructureToPtr(mmi, m.LParam, false);
            }
            return;
        }

        base.WndProc(ref m);
    }

    // ── Win32 ────────────────────────────────────────────────────────────────────
    private const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_GETMINMAXINFO = 0x0024;
    private const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
}
