using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Kerangka Control Room WPF — v5.3.0: tata letak dibangun ulang
/// persis desain Figma "MuseLink — Futuristic AI Control Center":
/// sidebar 240 (label MENU + kartu status dua baris), bar atas
/// dengan badge status + chip pengguna, panel konteks kanan 300px
/// (aktivitas Muse, aplikasi aktif, tugas, kejadian, info sistem)
/// yang hidup dari data bridge/sensor asli. Server &amp; endpoint
/// tidak berubah.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, string> _titles = new Dictionary<string, string>
    {
        ["overview"] = "Overview",
        ["console"] = "Muse Console",
        ["desktop"] = "Desktop Control",
        ["live"] = "Live Screen",
        ["activity"] = "Activity Center",
        ["automation"] = "Automation Center",
        ["monitor"] = "System Monitor",
        ["integrations"] = "Integrations",
        ["settings"] = "Settings",
    };

    private string _currentPage = "";
    private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
    private IRoomPage _currentRoomPage;
    private int _tick;
    private bool _panelBusy;

    public MainWindow()
    {
        InitializeComponent();

        // Perbaikan maximized (v5.6.0): jendela tanpa bingkai +
        // WindowChrome memenuhi LAYAR PENUH saat maximized,
        // sehingga isi bawah aplikasi tertutup taskbar Windows
        // (terbukti di audit tampilan 26 Nov: tombol Kirim
        // Console, kartu Active Window, tombol Ekspor Activity
        // semuanya kepotong). WM_GETMINMAXINFO dibelokkan agar
        // batas maximized = AREA KERJA monitor (di luar taskbar).
        SourceInitialized += (s, e) =>
        {
            var src = (HwndSource)PresentationSource.FromVisual(this);
            if (src != null) src.AddHook(WndProcHook);
        };
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "v" + (v != null ? v.ToString(3) : "5.3.0");
        NavOverview.IsChecked = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (s, e) => RefreshStatus();
        timer.Start();
        RefreshStatus();

        // Perilaku MuseLink sejak lama: tombol X hanya
        // menyembunyikan ke tray; keluar hanya lewat menu tray.
        Closing += (s, e) =>
        {
            if (!App.Exiting)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // Jalan otomatis (--auto): jendela tampil sebentar lalu
        // sembunyi sendiri ke tray sesudah 6 detik.
        if (Program.StartedAuto)
        {
            var autoHide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            autoHide.Tick += (s, e) =>
            {
                autoHide.Stop();
                if (IsVisible) Hide();
            };
            autoHide.Start();
        }
    }

    // ---- WM_GETMINMAXINFO: hormati area kerja saat maximized ----
    private const int WmGetMinMaxInfo = 0x0024;
    private const int MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectL { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointL Reserved;
        public PointL MaxSize;
        public PointL MaxPosition;
        public PointL MinTrackSize;
        public PointL MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public RectL Monitor;
        public RectL WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo info);

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfo)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (mon != IntPtr.Zero)
            {
                var mi = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(mon, ref mi))
                {
                    mmi.MaxPosition = new PointL
                    {
                        X = mi.WorkArea.Left - mi.Monitor.Left,
                        Y = mi.WorkArea.Top - mi.Monitor.Top,
                    };
                    mmi.MaxSize = new PointL
                    {
                        X = mi.WorkArea.Right - mi.WorkArea.Left,
                        Y = mi.WorkArea.Bottom - mi.WorkArea.Top,
                    };
                    mmi.MaxTrackSize = mmi.MaxSize;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Dipanggil ikon tray: tampilkan lagi jendela.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Navigasi programatik (dipakai Quick Actions dsb.).</summary>
    public void NavigateTo(string key)
    {
        foreach (var child in NavPanel.Children)
        {
            if (child is RadioButton rb && rb.Tag is string tag && tag == key)
            {
                rb.IsChecked = true;
                return;
            }
        }
        ShowPage(key);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string key)
            ShowPage(key);
    }

    private void ShowPage(string key)
    {
        if (key == _currentPage) return;
        _currentRoomPage?.OnHidden();
        _currentPage = key;
        string title = _titles.TryGetValue(key, out var t) ? t : key;
        PageTitle.Text = title;
        CrumbText.Text = "MuseLink / " + title;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "overview" => new OverviewPage(),
                "console" => new ConsolePage(),
                "desktop" => new DesktopPage(),
                "live" => new LivePage(),
                "activity" => new ActivityPage(),
                "automation" => new AutomationPage(),
                "monitor" => new MonitorPage(),
                "integrations" => new IntegrationsPage(),
                "settings" => new SettingsPage(),
                _ => new OverviewPage(),
            };
            _pages[key] = page;
        }
        PageHost.Content = page;
        _currentRoomPage = page as IRoomPage;
        _currentRoomPage?.OnShown();
    }

    private void RefreshStatus()
    {
        var host = App.Host;
        if (host == null) return;
        var server = host.Server;

        bool running = server.IsRunning;
        BridgeDot.Fill = (Brush)FindResource(running ? "CrGreen" : "CrRed");
        SideBridgeText.Text = running ? "Bridge Connected" : "Bridge Offline";
        BridgeBadgeDot.Fill = (Brush)FindResource(running ? "CrGreen" : "CrRed");
        BridgeBadgeText.Text = running ? "Bridge Online" : "Bridge Offline";

        bool halted = server.InputHalted;
        ClaudeDot.Fill = (Brush)FindResource(halted ? "CrRed" : "CrSage");
        SideClaudeText.Text = halted ? "Muse Terhenti (Stop)" : "Muse Online";
        MuseBadgeText.Text = halted ? "Emergency Stop" : "Muse Connected";
        HaltBanner.Visibility = halted ? Visibility.Visible : Visibility.Collapsed;

        _tick++;
        if (_tick % 4 == 1) _ = RefreshPanelAsync();
    }

    /// <summary>Isi panel konteks kanan dari data asli: operasi
    /// bridge terakhir, jendela depan, dan sensor sistem. Pembacaan
    /// berat (metrik ±250ms, nvidia-smi) di utas latar.</summary>
    private async Task RefreshPanelAsync()
    {
        if (_panelBusy) return;
        _panelBusy = true;
        try
        {
            var server = App.Host?.Server;
            if (server == null) return;
            var snap = await Task.Run(() =>
            {
                var health = server.BuildHealth();
                var metrics = NativeInput.SystemMetrics();
                string gpu = SysSensors.GpuText();
                var ops = server.RecentOpsSnapshot(4);
                int winCount = 0;
                try { winCount = NativeInput.ListWindows().Count; } catch { }
                return new { health, metrics, gpu, ops, winCount };
            });

            var health = snap.health;
            long uiOps = UiKit.GetLong(health, "uiOpsRunning");
            bool halted = UiKit.GetBool(health, "inputHalted");
            var ops = snap.ops;

            if (ops.Count > 0)
            {
                var last = ops[0];
                RpActTitle.Text = UiKit.FriendlyOp(last[1], last[2]);
                RpActSub.Text = last[1] + " " + last[2] + " • " + last[3] + " • " + last[4] + " ms";
            }
            else
            {
                RpActTitle.Text = "Menunggu perintah";
                RpActSub.Text = "Bridge • port " + AgentServer.Port;
            }
            RpActDot.Fill = (Brush)FindResource(halted ? "CrRed" : uiOps > 0 ? "CrAccent" : "CrGreen");
            SetBar(RpActTrack, RpActFill, halted ? 100 : uiOps > 0 ? 58 : ops.Count > 0 ? 100 : 0);

            string fg = UiKit.GetStr(health, "foreground");
            RpAppTitle.Text = string.IsNullOrEmpty(fg) ? "—" : fg;
            RpAppSub.Text = snap.winCount + " jendela terbuka di desktop";

            RpTaskTitle.Text = uiOps > 0
                ? uiOps + " operasi UI sedang berjalan"
                : "Tidak ada tugas berjalan";
            long uptime = UiKit.GetLong(health, "uptimeSec");
            RpTaskSub.Text = halted
                ? "Emergency Stop aktif — input ditahan"
                : "Bridge siaga • uptime " + TimeSpan.FromSeconds(uptime).ToString(@"hh\:mm\:ss");

            RpEvents.Children.Clear();
            foreach (var op in ops)
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7,
                    Fill = (Brush)FindResource(UiKit.StatusColor(op[3])),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(dot);
                var label = new TextBlock
                {
                    Text = UiKit.FriendlyOp(op[1], op[2]),
                    FontSize = 11.5,
                    Margin = new Thickness(8, 0, 6, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(label, 1);
                row.Children.Add(label);
                var ago = new TextBlock
                {
                    Text = op[0],
                    FontSize = 10.5,
                    Foreground = (Brush)FindResource("CrFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(ago, 2);
                row.Children.Add(ago);
                RpEvents.Children.Add(row);
            }
            if (ops.Count == 0)
                RpEvents.Children.Add(new TextBlock
                {
                    Text = "Belum ada kejadian.",
                    FontSize = 11.5,
                    Foreground = (Brush)FindResource("CrMuted"),
                });

            var m = snap.metrics;
            RpCpuVal.Text = m.CpuPercent >= 0 ? m.CpuPercent + "%" : "—";
            SetBar(RpCpuTrack, RpCpuFill, m.CpuPercent >= 0 ? m.CpuPercent : 0);
            RpRamVal.Text = m.RamPercent >= 0 ? m.RamPercent + "%" : "—";
            SetBar(RpRamTrack, RpRamFill, m.RamPercent >= 0 ? m.RamPercent : 0);
            int gpuPct = ParseGpuUtil(snap.gpu);
            RpGpuVal.Text = gpuPct >= 0 ? gpuPct + "%" : "—";
            SetBar(RpGpuTrack, RpGpuFill, gpuPct >= 0 ? gpuPct : 0);
        }
        catch { /* panel konteks pelengkap — gagal diam */ }
        finally { _panelBusy = false; }
    }

    private static int ParseGpuUtil(string gpuText)
    {
        // Format SysSensors.GpuText(): "41°C · 28%".
        if (string.IsNullOrEmpty(gpuText)) return -1;
        int dot = gpuText.IndexOf('·');
        if (dot < 0) return -1;
        string tail = gpuText.Substring(dot + 1).Trim().TrimEnd('%').Trim();
        return int.TryParse(tail, out int v) ? v : -1;
    }

    private static void SetBar(Grid track, FrameworkElement fill, double pct)
    {
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        track.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        track.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        fill.Visibility = pct <= 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ReleaseStop_Click(object sender, RoutedEventArgs e)
    {
        // Pelepasan memang hanya boleh dari aplikasi (aturan server).
        App.Host?.Server.SetHalt(false, "ui");
        RefreshStatus();
    }

    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            try { DragMove(); } catch { /* abaikan */ }
        }
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
