using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Connections: keadaan bridge (listener + tombol putus/sambung),
/// relay Tailscale, dan ringkasan kesehatan dari BuildHealth().
/// Semua pembacaan berat berjalan di utas latar.
/// </summary>
public sealed class ConnectionsPage : UserControl, IRoomPage
{
    private readonly TextBlock _bridgeVal, _bridgeSub, _relayVal, _healthVal;
    private readonly Button _toggleBtn;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public ConnectionsPage()
    {
        var root = new StackPanel();

        _bridgeVal = UiKit.Value("…"); _bridgeSub = UiKit.Muted("");
        _toggleBtn = UiKit.Btn("Putuskan Akses", (s, e) => { App.Host?.ToggleServer(); _ = RefreshAsync(); });
        _toggleBtn.Margin = new Thickness(0, 10, 0, 0);
        var bridgeStack = new StackPanel();
        bridgeStack.Children.Add(_bridgeVal); bridgeStack.Children.Add(_bridgeSub); bridgeStack.Children.Add(_toggleBtn);
        root.Children.Add(UiKit.Card("BRIDGE (LISTENER)", bridgeStack));

        _relayVal = UiKit.Body("…");
        var relayCard = UiKit.Card("TAILSCALE", _relayVal);
        relayCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(relayCard);

        _healthVal = UiKit.Body("…");
        var healthCard = UiKit.Card("KESEHATAN SISTEM", _healthVal);
        healthCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(healthCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
            var data = await Task.Run(() =>
            {
                var h = server.BuildHealth();
                string relay = "";
                try { relay = AgentServer.TailscaleRelay(); } catch { }
                return (h, relay);
            });
            var health = data.h;

            bool running = UiKit.GetBool(health, "listener");
            _bridgeVal.Text = running ? "Mendengarkan" : "Berhenti";
            _bridgeSub.Text = "Port " + AgentServer.Port + " • hanya bisa dijangkau lewat jaringan Tailscale pengguna.";
            _toggleBtn.Content = running ? "Putuskan Akses" : "Sambungkan Akses";

            _relayVal.Text = string.IsNullOrEmpty(data.relay) ? "(relay tidak terdeteksi)" : data.relay;

            var ui = health.TryGetValue("ui", out var uio) ? uio as System.Collections.Generic.Dictionary<string, object> : null;
            var sb = new StringBuilder();
            sb.AppendLine("Versi: " + UiKit.GetStr(health, "version"));
            sb.AppendLine("UI responsif: " + UiKit.GetLong(ui, "responsiveMs") + " ms");
            sb.AppendLine("Operasi UI berjalan: " + UiKit.GetLong(health, "uiOpsRunning"));
            sb.AppendLine("Shell perintah hidup: " + (UiKit.GetBool(health, "shellAlive") ? "ya" : "tidak"));
            sb.AppendLine("Kejadian terakhir (seq): " + UiKit.GetLong(health, "eventsLastSeq"));
            sb.Append("Emergency Stop: " + (UiKit.GetBool(health, "inputHalted") ? "AKTIF" : "tidak aktif"));
            _healthVal.Text = sb.ToString();
        }
        catch { }
        finally { _busy = false; }
    }
}
