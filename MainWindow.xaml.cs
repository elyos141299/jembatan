using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Jendela MuseLink v5.8.0 — kembali ke tampilan Jembatan v1
/// (perintah user 26 Nov 2026: "persis seperti pertama kali,
/// tanpa WebView2"): SATU jendela gelap berisi judul, baris
/// status, baris port, log aktivitas monospace real-time, dan
/// tombol PUTUSKAN/SAMBUNGKAN selebar jendela. Tanpa sidebar,
/// tanpa halaman, tanpa panel. Shell Control Room v5.0–5.7
/// dipensiunkan; backend &amp; tray tidak berubah.
/// Warna disampel dari screenshot asli v1: latar #0D1117,
/// panel log #161B22, hijau #2EA043, merah tombol #B9372D.
/// Judul jendela &amp; judul besar: "MuseLink" (ketetapan user
/// saat approv rencana, 26 Nov 14:28).
/// </summary>
public partial class MainWindow : Window
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));

    private readonly DispatcherTimer _timer;
    private int _logShown;      // baris log cincin yang sudah ditampilkan
    private bool _logPrimed;    // pengisian awal selesai?

    public MainWindow()
    {
        InitializeComponent();
        PortText.Text = "Port " + AgentServer.Port
            + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.";
        Closing += (s, e) =>
        {
            // X = sembunyi ke tray; keluar sungguhan hanya lewat
            // menu tray (App.ExitApp) — perilaku sejak v2.3.1.
            if (!App.Exiting) { e.Cancel = true; Hide(); }
        };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (s, e) => RefreshAll();
        _timer.Start();
        RefreshAll();
    }

    /// <summary>Tampilkan jendela dari tray (berlaku juga walau
    /// belum pernah ditampilkan — start --auto v5.7.0).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshAll();
    }

    private void ToggleBtn_Click(object sender, RoutedEventArgs e)
        => App.Host?.ToggleServer();

    private void RefreshAll()
    {
        try
        {
            var host = App.Host;
            if (host == null) return;
            var server = host.Server;
            bool running = server != null && server.IsRunning;
            bool halted = server != null && server.InputHalted;

            if (halted)
            {
                StatusBox.Fill = Amber;
                StatusText.Text = "Emergency Stop aktif — Muse terhubung, masukan diblokir";
            }
            else if (running)
            {
                StatusBox.Fill = Green;
                string last = host.LastActivity == DateTime.MinValue
                    ? "—" : host.LastActivity.ToString("HH:mm:ss");
                StatusText.Text = "Muse terhubung • aktivitas terakhir " + last;
            }
            else
            {
                StatusBox.Fill = Red;
                StatusText.Text = "Muse terputus — bridge berhenti";
            }

            ToggleBtn.Content = running ? "PUTUSKAN" : "SAMBUNGKAN";
            ToggleBtn.Style = (Style)FindResource(running ? "BtnRed" : "BtnGreen");

            RefreshLog(host);
        }
        catch { /* pelengkap tampilan — jangan jatuhkan aplikasi */ }
    }

    private void RefreshLog(AppHost host)
    {
        var lines = host.RecentLogLines(300);
        if (!_logPrimed || lines.Count < _logShown)
        {
            // Isi awal / cincin terpotong: tulis ulang seluruhnya.
            LogBox.Clear();
            foreach (var line in lines) LogBox.AppendText(Render(line));
            _logPrimed = true;
        }
        else
        {
            for (int i = _logShown; i < lines.Count; i++)
                LogBox.AppendText(Render(lines[i]));
        }
        _logShown = lines.Count;
        // Ikut turun hanya bila user memang sedang di bawah.
        if (LogBox.ExtentHeight <= LogBox.ViewportHeight
            || LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 48)
            LogBox.ScrollToEnd();
    }

    /// <summary>Baris cincin "HH:mm:ss\tisi" → "[HH:mm:ss] isi".</summary>
    private static string Render(string line)
    {
        int tab = line.IndexOf('\t');
        if (tab > 0)
            return "[" + line.Substring(0, tab) + "] " + line.Substring(tab + 1) + "\n";
        return line + "\n";
    }
}
