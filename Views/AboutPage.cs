using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Jembatan;

/// <summary>
/// About v5.7.0 (Tray-First) — halaman info kecil yang dulu
/// menjadi kategori di dalam Settings: versi aplikasi,
/// keadaan bridge, dan perangkat. Semua nilai asli, dibaca
/// ulang setiap halaman ditampilkan.
/// </summary>
public sealed class AboutPage : UserControl, IRoomPage
{
    private readonly TextBlock _bridgeStatus, _bridgeUptime;

    public AboutPage()
    {
        var stack = new StackPanel();

        var appStack = new StackPanel();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        AddRow(appStack, "Versi", "MuseLink v" + (v != null ? v.ToString(3) : "5.7.0"));
        AddRow(appStack, "Antarmuka", "WPF (Control Room) — Tray-First");
        AddRow(appStack, "Proses", Settings.IsElevated() ? "Administrator" : "Pengguna biasa");
        AddRow(appStack, "Perangkat", Environment.MachineName);
        stack.Children.Add(UiKit.Card("APLIKASI", appStack));

        var brStack = new StackPanel();
        _bridgeStatus = AddRow(brStack, "Status", "…");
        AddRow(brStack, "Port", AgentServer.Port + " (HTTP lokal, via Tailscale)");
        AddRow(brStack, "Alamat", "100.126.240.47 (Tailscale)");
        _bridgeUptime = AddRow(brStack, "Uptime sesi", "…");
        var brCard = UiKit.Card("BRIDGE", brStack);
        brCard.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(brCard);

        var note = UiKit.Muted("MuseLink adalah jembatan kendali desktop untuk Muse: "
            + "aplikasi ini sebagian besar waktunya berjalan tersembunyi di system tray. "
            + "Tombol tutup (X) menyembunyikan jendela ke tray; keluar sungguhan hanya "
            + "lewat menu tray → Keluar. Pengaturan tersimpan di registry "
            + "HKCU\\Software\\Jembatan.");
        note.Margin = new Thickness(2, 18, 0, 0);
        stack.Children.Add(note);

        Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static TextBlock AddRow(StackPanel parent, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = UiKit.Res("CrMuted"),
        });
        var val = new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(val, 1);
        row.Children.Add(val);
        parent.Children.Add(row);
        return val;
    }

    public void OnShown()
    {
        try
        {
            var server = App.Host?.Server;
            if (server == null) return;
            _bridgeStatus.Text = server.IsRunning ? "Mendengarkan" : "Berhenti";
            var health = server.BuildHealth();
            long up = UiKit.GetLong(health, "uptimeSec");
            _bridgeUptime.Text = TimeSpan.FromSeconds(up).ToString(@"hh\:mm\:ss");
        }
        catch { /* pelengkap */ }
    }

    public void OnHidden() { }
}
