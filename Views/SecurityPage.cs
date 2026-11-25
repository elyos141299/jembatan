using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Permissions &amp; Security: mode kendali proses, lease input,
/// Emergency Stop dua arah dari dalam aplikasi, dan kartu catatan
/// jujur — persetujuan tindakan (kartu Allow) milik runtime Muse
/// dan berada di luar aplikasi ini.
/// </summary>
public sealed class SecurityPage : UserControl, IRoomPage
{
    private readonly TextBlock _modeVal, _modeSub, _leaseVal, _leaseSub, _stopVal;
    private readonly Button _stopBtn;
    private readonly DispatcherTimer _timer;
    private bool _busy, _halted;

    public SecurityPage()
    {
        var root = new StackPanel();

        _modeVal = UiKit.Value("…"); _modeSub = UiKit.Muted("");
        var modeStack = new StackPanel();
        modeStack.Children.Add(_modeVal); modeStack.Children.Add(_modeSub);
        root.Children.Add(UiKit.Card("MODE KENDALI", modeStack));

        _leaseVal = UiKit.Value("…"); _leaseSub = UiKit.Muted("");
        var leaseStack = new StackPanel();
        leaseStack.Children.Add(_leaseVal); leaseStack.Children.Add(_leaseSub);
        var leaseCard = UiKit.Card("LEASE INPUT (SATPAM INPUT)", leaseStack);
        leaseCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(leaseCard);

        _stopVal = UiKit.Body("…");
        _stopBtn = UiKit.Btn("Aktifkan Emergency Stop", StopToggle_Click);
        _stopBtn.Margin = new Thickness(0, 10, 0, 0);
        var stopStack = new StackPanel();
        stopStack.Children.Add(_stopVal); stopStack.Children.Add(_stopBtn);
        var stopCard = UiKit.Card("EMERGENCY STOP", stopStack);
        stopCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(stopCard);

        var noteCard = UiKit.Card("IZIN TINDAKAN",
            UiKit.Body("Persetujuan tindakan (kartu Allow) diberikan lewat runtime Muse di layar pengguna — "
                + "aplikasi ini tidak bisa menyetujui atau melewatinya. Yang bisa dilakukan di sini: "
                + "menghentikan semua input dengan Emergency Stop dan memutus listener bridge."));
        noteCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(noteCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (s, e) => _ = RefreshAsync();
    }

    public void OnShown() { _timer.Start(); _ = RefreshAsync(); }
    public void OnHidden() { _timer.Stop(); }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var server = App.Host?.Server;
            if (server == null) return;
            var health = await Task.Run(() => server.BuildHealth());

            _modeVal.Text = Settings.IsElevated() ? "Administrator" : "Pengguna biasa";
            _modeSub.Text = Settings.AdminMode
                ? "Mode Administrator tersimpan AKTIF di pengaturan."
                : "Mode Administrator tersimpan MATI di pengaturan.";

            string lease = UiKit.GetStr(health, "inputLease");
            _leaseVal.Text = string.IsNullOrEmpty(lease) ? "Bebas" : lease;
            _leaseSub.Text = "Satu tugas memegang kendali input pada satu waktu; tugas lain menunggu atau ditolak (429).";

            _halted = UiKit.GetBool(health, "inputHalted");
            _stopVal.Text = _halted
                ? "AKTIF — semua input dari bridge ditolak (423) sampai stop dilepas dari aplikasi ini."
                : "Tidak aktif — bridge boleh mengirim input.";
            _stopBtn.Content = _halted ? "Lepas Emergency Stop" : "Aktifkan Emergency Stop";
        }
        catch { }
        finally { _busy = false; }
    }

    private void StopToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Host?.Server.SetHalt(!_halted, "ui");
        _ = RefreshAsync();
    }
}
