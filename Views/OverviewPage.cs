using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Overview (brief: status Muse AI, Bridge, sesi Desktop,
/// Permission, Active Task, Recent Activity, Desktop Context,
/// System Health, Emergency Stop). Semua data asli dari
/// BuildHealth() + RecentOpsSnapshot + SysSensors — BuildHealth
/// dipanggil di utas latar karena probenya ke utas UI.
/// </summary>
public sealed class OverviewPage : UserControl, IRoomPage
{
    private readonly TextBlock _bridgeVal, _bridgeSub, _deskVal, _deskSub,
        _permVal, _permSub, _healthVal, _healthSub, _taskVal;
    private readonly ListBox _recent;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public OverviewPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var cards = new Grid();
        for (int i = 0; i < 4; i++)
            cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _bridgeVal = UiKit.Value("…"); _bridgeSub = UiKit.Muted("");
        _deskVal = UiKit.Value("…"); _deskSub = UiKit.Muted("");
        _permVal = UiKit.Value("…"); _permSub = UiKit.Muted("");
        _healthVal = UiKit.Value("…"); _healthSub = UiKit.Muted("");

        // Judul jendela depan bisa sangat panjang — potong rapi
        // dengan ellipsis, jangan membungkus berantakan (v5.1).
        _deskVal.TextWrapping = TextWrapping.NoWrap;
        _deskVal.TextTrimming = TextTrimming.CharacterEllipsis;

        AddCard(cards, 0, "BRIDGE & Muse", _bridgeVal, _bridgeSub, "CrGreen");
        AddCard(cards, 1, "SESI DESKTOP", _deskVal, _deskSub, "CrAccent");
        AddCard(cards, 2, "PERMISSION & STOP", _permVal, _permSub, "CrGold");
        AddCard(cards, 3, "SYSTEM HEALTH", _healthVal, _healthSub, "CrSage");
        Grid.SetRow(cards, 0);
        root.Children.Add(cards);

        var bottom = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _recent = UiKit.List();
        var recentCard = UiKit.Card("RECENT ACTIVITY — operasi bridge terakhir", _recent);
        Grid.SetColumn(recentCard, 0);
        bottom.Children.Add(recentCard);

        _taskVal = UiKit.Body("…");
        var taskCard = UiKit.Card("ACTIVE TASK", _taskVal);
        taskCard.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(taskCard, 1);
        bottom.Children.Add(taskCard);

        Grid.SetRow(bottom, 1);
        root.Children.Add(bottom);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (s, e) => _ = RefreshAsync();
    }

    private static void AddCard(Grid parent, int col, string title, TextBlock val, TextBlock sub, string accentKey)
    {
        var stack = new StackPanel();
        stack.Children.Add(val);
        stack.Children.Add(sub);
        var card = UiKit.Card(title, stack, accentKey);
        if (col > 0) card.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(card, col);
        parent.Children.Add(card);
    }

    public void OnShown() { _timer.Start(); _ = RefreshAsync(); }
    public void OnHidden() { _timer.Stop(); }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var host = App.Host;
            if (host == null) return;
            var server = host.Server;

            var data = await Task.Run(() =>
            {
                var h = server.BuildHealth();
                string ram = "", gpu = "";
                try { ram = SysSensors.RamText(); } catch { }
                try { gpu = SysSensors.GpuText(); } catch { }
                int winCount = 0;
                try { winCount = NativeInput.ListWindows().Count; } catch { }
                var ops = server.RecentOpsSnapshot(6);
                return (h, ram, gpu, winCount, ops);
            });
            var health = data.h;

            // Bridge & Muse
            bool running = UiKit.GetBool(health, "listener");
            _bridgeVal.Text = running ? "Terhubung" : "Berhenti";
            _bridgeSub.Text = "Port " + AgentServer.Port + " • v" + UiKit.GetStr(health, "version")
                + " • uptime " + FormatUptime(UiKit.GetLong(health, "uptimeSec"));

            // Sesi desktop
            string fg = UiKit.GetStr(health, "foreground");
            _deskVal.Text = string.IsNullOrEmpty(fg) ? "(tidak ada jendela depan)" : fg;
            var pb = NativeInput.PrimaryBounds();
            _deskSub.Text = data.winCount + " jendela • monitor utama " + pb.Width + "×" + pb.Height;

            // Permission & stop
            bool halted = UiKit.GetBool(health, "inputHalted");
            string lease = UiKit.GetStr(health, "inputLease");
            _permVal.Text = halted ? "EMERGENCY STOP AKTIF" : "Input terbuka";
            _permSub.Text = string.IsNullOrEmpty(lease)
                ? "Lease input: bebas • Mode: " + (Settings.IsElevated() ? "Administrator" : "Pengguna biasa")
                : "Lease input: " + lease;

            // Kesehatan sistem
            var ui = health.TryGetValue("ui", out var uio) ? uio as Dictionary<string, object> : null;
            long probe = UiKit.GetLong(ui, "responsiveMs");
            _healthVal.Text = "UI " + (probe >= 0 ? probe + " ms" : "tidak terjangkau");
            _healthSub.Text = "RAM " + data.ram + " • " + data.gpu;

            // Tugas aktif + aktivitas terakhir
            long running2 = UiKit.GetLong(health, "uiOpsRunning");
            _taskVal.Text = running2 > 0
                ? running2 + " operasi UI sedang berjalan."
                : "Tidak ada operasi UI yang berjalan. Tugas agen terlihat lewat operasi bridge di samping.";

            _recent.Items.Clear();
            foreach (var row in data.ops)
                _recent.Items.Add(string.Join("  ", row));
            if (data.ops.Count == 0) _recent.Items.Add("(belum ada operasi tercatat)");
        }
        catch { /* halaman tetap menampilkan data terakhir */ }
        finally { _busy = false; }
    }

    private static string FormatUptime(long sec)
    {
        if (sec < 60) return sec + " dtk";
        if (sec < 3600) return (sec / 60) + " mnt";
        return (sec / 3600) + " j " + ((sec % 3600) / 60) + " m";
    }
}
