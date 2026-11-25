using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Kerangka Control Room WPF (v5.0.0): sidebar persisten 232px,
/// 9 navigasi, bar atas dengan Emergency Stop, spanduk halt, dan
/// host halaman. Halaman sungguhan diisi tahap berikutnya — selama
/// transisi halaman menampilkan penanda jujur.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<string, string> _titles = new Dictionary<string, string>
    {
        ["overview"] = "Overview",
        ["command"] = "Command Center",
        ["activity"] = "Activity Monitor",
        ["desktop"] = "Desktop Control",
        ["automation"] = "Automation",
        ["security"] = "Permissions & Security",
        ["connections"] = "Connections",
        ["logs"] = "System Logs",
        ["settings"] = "Settings",
    };

    private string _currentPage = "";
    private readonly Dictionary<string, UserControl> _pages = new Dictionary<string, UserControl>();
    private IRoomPage _currentRoomPage;

    public MainWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "Control Room • v" + (v != null ? v.ToString(3) : "5.0.0");
        NavOverview.IsChecked = true;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (s, e) => RefreshStatus();
        timer.Start();
        RefreshStatus();
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
        PageTitle.Text = _titles.TryGetValue(key, out var t) ? t : key;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "overview" => new OverviewPage(),
                "command" => new CommandPage(),
                "activity" => new ActivityPage(),
                "desktop" => new DesktopPage(),
                "automation" => new AutomationPage(),
                "security" => new SecurityPage(),
                "connections" => new ConnectionsPage(),
                "logs" => new LogsPage(),
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
        BridgeStateText.Text = running ? "Bridge: Mendengarkan" : "Bridge: Berhenti";
        ToggleServerButton.Content = running ? "Putuskan Akses" : "Sambungkan Akses";

        bool halted = server.InputHalted;
        HaltBanner.Visibility = halted ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = !halted;

        ActivityText.Text = host.LastActivity == DateTime.MinValue
            ? ""
            : "Aktivitas terakhir " + host.LastActivity.ToString("HH:mm:ss");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        App.Host?.Server.SetHalt(true, "ui");
        RefreshStatus();
    }

    private void ReleaseStop_Click(object sender, RoutedEventArgs e)
    {
        // Pelepasan memang hanya boleh dari aplikasi (aturan server).
        App.Host?.Server.SetHalt(false, "ui");
        RefreshStatus();
    }

    private void ToggleServer_Click(object sender, RoutedEventArgs e)
    {
        App.Host?.ToggleServer();
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
