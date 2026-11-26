using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Kerangka utama MuseLink — v5.7.0 Tray-First: sidebar 5
/// halaman (Home, Activity, AI, Settings, About), bar atas
/// ramping (judul + dua lencana status + tombol jendela), TANPA
/// panel konteks kanan (duplikat Overview/Activity — dibuang
/// atas prompt penyederhanaan user 26 Nov 2026). Server,
/// endpoint &amp; protokol tidak berubah sama sekali.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, string> _titles = new Dictionary<string, string>
    {
        ["home"] = "Home",
        ["activity"] = "Activity",
        ["ai"] = "AI",
        ["settings"] = "Settings",
        ["about"] = "About",
    };

    private string _currentPage = "";
    private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
    private IRoomPage _currentRoomPage;

    public MainWindow()
    {
        InitializeComponent();

        // Perbaikan maximized (v5.6.0, dipertahankan): jendela
        // tanpa bingkai + WindowChrome memenuhi LAYAR PENUH saat
        // maximized, sehingga isi bawah aplikasi tertutup taskbar
        // Windows. WM_GETMINMAXINFO dibelokkan agar batas
        // maximized = AREA KERJA monitor (di luar taskbar).
        SourceInitialized += (s, e) =>
        {
            var src = (HwndSource)PresentationSource.FromVisual(this);
            if (src != null) src.AddHook(WndProcHook);
        };
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "v" + (v != null ? v.ToString(3) : "5.7.0");
        NavHome.IsChecked = true;

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

        // Startup otomatis (--auto): jendela TIDAK ditampilkan
        // sama sekali (v5.7.0, instruksi Tray-First user) —
        // App memang tidak memanggil Show() untuk mode ini,
        // jadi blok sembunyi-otomatis lama tidak diperlukan.
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

    /// <summary>Navigasi programatik (dipakai tombol Lihat Detail dsb.).</summary>
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
                "home" => new HomePage(),
                "activity" => new ActivityPage(),
                "ai" => new AiPage(),
                "settings" => new SettingsPage(),
                "about" => new AboutPage(),
                _ => new HomePage(),
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
