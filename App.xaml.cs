using System;
using System.Windows;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Host UI WPF untuk engine (v5.0.0): menyalurkan aksi engine ke
/// utas UI lewat Dispatcher dan meneruskan dua permintaan balik
/// (tema &amp; jangan-tidur) ke AppHost.
/// </summary>
public sealed class WpfUiHost : IUiHost
{
    private readonly Dispatcher _dispatcher;

    public WpfUiHost(Dispatcher dispatcher) { _dispatcher = dispatcher; }

    public bool InvokeRequired => !_dispatcher.CheckAccess();

    public void BeginInvoke(Action action) => _dispatcher.BeginInvoke(action);

    public void SetTheme(string choice)
    {
        // Control Room v5 gelap secara rancangan (brief: tema Dark) —
        // permintaan tema dari bridge dicatat, tampilan tetap gelap.
        UiThemeState.IsDark = true;
        App.Host?.Log("Tema diminta dari bridge: " + choice + " — Control Room tetap gelap.");
    }

    public void ApplyKeepAwake(bool on) => App.Host?.ApplyKeepAwake(on);
}

/// <summary>
/// Aplikasi WPF MuseLink (v5.0.0). Titik masuk tetap Program.Main
/// (mutex satu-instans + relaunch UAC); kelas ini hanya menghidupkan
/// AppHost (engine) dan jendela utama.
/// </summary>
public partial class App : Application
{
    public static AppHost Host { get; private set; }
    public static WpfUiHost UiHost { get; private set; }

    /// <summary>True hanya saat aplikasi benar-benar keluar (menu
    /// tray Keluar / buka ulang berhak admin) — tombol X jendela
    /// hanya menyembunyikan ke tray.</summary>
    public static bool Exiting { get; private set; }

    private TrayIcon _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        UiHost = new WpfUiHost(Dispatcher);
        Host = new AppHost(UiHost);
        Host.RequestShutdown += () => Dispatcher.BeginInvoke(new Action(ExitApp));
        var win = new MainWindow();
        MainWindow = win;
        win.Show();

        _tray = new TrayIcon
        {
            StateProvider = () => (Host.Server.IsRunning, Host.Server.InputHalted),
        };
        _tray.OpenRequested += () => win.ShowFromTray();
        _tray.ToggleServerRequested += () => Host.ToggleServer();
        _tray.ToggleStopRequested += () => Host.Server.SetHalt(!Host.Server.InputHalted, "tray");
        _tray.ExitRequested += ExitApp;

        Host.Start();
        Host.Log("MuseLink v5.0.0 dimulai (UI WPF — Control Room).");
        if (Program.ElevationDeclined)
            Host.Log("Mode Administrator ditolak — jalan sebagai pengguna biasa.");
    }

    /// <summary>Satu-satunya jalan keluar sungguhan dari aplikasi.</summary>
    public void ExitApp()
    {
        if (Exiting) return;
        Exiting = true;
        try { _tray?.Dispose(); } catch { /* abaikan */ }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Host?.Stop(); } catch { /* abaikan */ }
        base.OnExit(e);
    }
}
