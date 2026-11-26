using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Home v5.7.0 (Tray-First) — penyederhanaan Overview lama:
/// 5 kartu metrik (Muse, Bridge, CPU, GPU, Disk), panel
/// aktivitas AI saat ini, dan aktivitas terakhir. Quick
/// Actions &amp; System Overview dibuang (duplikat/tidak
/// penting untuk aplikasi jembatan). Semua angka dari data
/// asli (BuildHealth, SystemMetrics, nvidia-smi, operasi
/// bridge terakhir) — sama seperti sebelumnya.
/// </summary>
public sealed class HomePage : UserControl, IRoomPage
{
    private readonly TextBlock _museVal, _museSub, _bridgeVal, _bridgeSub;
    private readonly TextBlock _cpuVal, _cpuSub, _gpuVal, _gpuSub, _diskVal, _diskSub;
    private readonly TextBlock _actTitle, _actDesc, _actPct;
    private readonly Grid _actBar;
    private readonly StackPanel _actSteps, _recentList;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public HomePage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Baris metrik (5 kartu) ----
        var metrics = new Grid();
        for (int i = 0; i < 5; i++)
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.Children.Add(MetricCard(0, 5, "MUSE STATUS", "CrSage", out _museVal, out _museSub));
        metrics.Children.Add(MetricCard(1, 5, "DESKTOP BRIDGE", "CrGreen", out _bridgeVal, out _bridgeSub));
        metrics.Children.Add(MetricCard(2, 5, "CPU LOAD", "CrAccent", out _cpuVal, out _cpuSub));
        metrics.Children.Add(MetricCard(3, 5, "GPU / VRAM", "CrGreen", out _gpuVal, out _gpuSub));
        metrics.Children.Add(MetricCard(4, 5, "DISK", "CrGold", out _diskVal, out _diskSub));
        root.Children.Add(metrics);

        // ---- Aktivitas AI saat ini ----
        var actStack = new StackPanel();
        var actHead = new StackPanel { Orientation = Orientation.Horizontal };
        actHead.Children.Add(UiKit.Dot("CrAccent", 9));
        _actTitle = new TextBlock
        {
            Text = "Menunggu perintah",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        actHead.Children.Add(_actTitle);
        actStack.Children.Add(actHead);
        _actDesc = UiKit.Muted("Belum ada operasi bridge pada sesi ini.");
        _actDesc.Margin = new Thickness(1, 8, 0, 0);
        actStack.Children.Add(_actDesc);
        var barRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _actBar = UiKit.Bar("CrAccent", 6);
        barRow.Children.Add(_actBar);
        _actPct = new TextBlock
        {
            Text = "",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Res("CrAccent"),
            Margin = new Thickness(10, -3, 0, 0),
        };
        Grid.SetColumn(_actPct, 1);
        barRow.Children.Add(_actPct);
        actStack.Children.Add(barRow);
        _actSteps = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        actStack.Children.Add(_actSteps);
        var actBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var detailBtn = UiKit.Btn("Lihat Detail", (s, e) =>
            (Application.Current.MainWindow as MainWindow)?.NavigateTo("activity"));
        actBtns.Children.Add(detailBtn);
        var haltBtn = UiKit.DangerBtn("Hentikan", (s, e) =>
        {
            App.Host?.Server.SetHalt(true, "ui");
            _ = RefreshAsync();
        });
        haltBtn.Margin = new Thickness(10, 0, 0, 0);
        actBtns.Children.Add(haltBtn);
        actStack.Children.Add(actBtns);
        var actCard = UiKit.Card("CURRENT AI ACTIVITY", actStack);
        actCard.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(actCard, 1);
        root.Children.Add(actCard);

        // ---- Aktivitas terakhir ----
        _recentList = new StackPanel();
        var recentScroll = new ScrollViewer
        {
            Content = _recentList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var recentCard = UiKit.Card("RECENT ACTIVITY", recentScroll);
        recentCard.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(recentCard, 2);
        root.Children.Add(recentCard);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (s, e) => _ = RefreshAsync();
    }

    private Border MetricCard(int col, int total, string title, string colorKey, out TextBlock val, out TextBlock sub)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(UiKit.Dot(colorKey, 8));
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Res("CrMuted"),
            Margin = new Thickness(8, 0, 0, 0),
        });
        stack.Children.Add(head);
        val = new TextBlock
        {
            Text = "…",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 0),
        };
        stack.Children.Add(val);
        sub = UiKit.Muted("");
        sub.FontSize = 11;
        sub.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(sub);
        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = stack,
            Margin = new Thickness(col == 0 ? 0 : 7, 0, col == total - 1 ? 0 : 7, 0),
        };
        Grid.SetColumn(card, col);
        return card;
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
            var snap = await Task.Run(() =>
            {
                var health = server.BuildHealth();
                var metrics = NativeInput.SystemMetrics();
                string gpu = SysSensors.GpuText();
                var ops = server.RecentOpsSnapshot(6);
                return new { health, metrics, gpu, ops };
            });
            var health = snap.health;
            var m = snap.metrics;
            bool running = UiKit.GetBool(health, "listener");
            bool halted = UiKit.GetBool(health, "inputHalted");
            long uiOps = UiKit.GetLong(health, "uiOpsRunning");

            _museVal.Text = halted ? "Terhenti" : running ? "Connected" : "Offline";
            long respMs = 0;
            if (health.TryGetValue("ui", out var uiObj) && uiObj is System.Collections.Generic.Dictionary<string, object> ui)
                respMs = UiKit.GetLong(ui, "responsiveMs");
            _museSub.Text = "Model aktif • respons UI " + respMs + " ms";
            _bridgeVal.Text = running ? "Online" : "Offline";
            _bridgeSub.Text = "Port " + AgentServer.Port + " • Tailscale relay";
            _cpuVal.Text = m.CpuPercent >= 0 ? m.CpuPercent + "%" : "—";
            _cpuSub.Text = "i3-12100F • beban sistem";
            ParseGpu(snap.gpu, out string temp, out int util);
            _gpuVal.Text = temp.Length > 0 ? temp + "°C" : "—";
            _gpuSub.Text = "GTX 1660 Super • " + (util >= 0 ? util + "%" : "—");
            double diskPct = m.DiskTotalGb > 0
                ? (m.DiskTotalGb - m.DiskFreeGb) * 100.0 / m.DiskTotalGb : -1;
            _diskVal.Text = diskPct >= 0 ? (int)Math.Round(diskPct) + "%" : "—";
            _diskSub.Text = m.DiskTotalGb > 0
                ? "SSD • bebas " + (int)Math.Round(m.DiskFreeGb) + " GB"
                : "SSD";

            // Panel aktivitas
            if (snap.ops.Count > 0)
            {
                var last = snap.ops[0];
                _actTitle.Text = UiKit.FriendlyOp(last[1], last[2]);
                _actDesc.Text = last[1] + " " + last[2] + " — status " + last[3]
                    + ", selesai dalam " + last[4] + " ms (pukul " + last[0] + ").";
            }
            double pct = halted ? 100 : uiOps > 0 ? 58 : snap.ops.Count > 0 ? 100 : 0;
            UiKit.SetBar(_actBar, pct);
            _actPct.Text = snap.ops.Count > 0 || uiOps > 0 ? (int)pct + "%" : "";
            _actSteps.Children.Clear();
            int shown = 0;
            foreach (var op in snap.ops)
            {
                if (shown >= 4) break;
                bool ok = UiKit.StatusColor(op[3]) == "CrGreen";
                var step = new TextBlock
                {
                    Text = (ok ? "✓ " : "✕ ") + op[2],
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = UiKit.Res(ok ? "CrGreen" : "CrRed"),
                    Margin = new Thickness(shown == 0 ? 0 : 16, 0, 0, 0),
                };
                _actSteps.Children.Add(step);
                shown++;
            }
            if (shown == 0)
                _actSteps.Children.Add(UiKit.Muted("Belum ada langkah tercatat."));

            // Linimasa aktivitas terakhir
            _recentList.Children.Clear();
            foreach (var op in snap.ops)
            {
                var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(UiKit.Dot(UiKit.StatusColor(op[3]), 9));
                var texts = new StackPanel { Margin = new Thickness(12, -3, 8, 0) };
                texts.Children.Add(new TextBlock
                {
                    Text = UiKit.FriendlyOp(op[1], op[2]),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                });
                texts.Children.Add(new TextBlock
                {
                    Text = op[1] + " " + op[2] + " • status " + op[3] + " • " + op[4] + " ms"
                        + (op[6].Length > 0 ? " • " + op[6] : ""),
                    FontSize = 11,
                    Foreground = UiKit.Res("CrMuted"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                Grid.SetColumn(texts, 1);
                row.Children.Add(texts);
                string jam = op[0].Length >= 5 ? op[0].Substring(0, 5) : op[0];
                var when = new TextBlock
                {
                    Text = "pukul " + jam,
                    FontSize = 10.5,
                    Foreground = UiKit.Res("CrFaint"),
                };
                Grid.SetColumn(when, 2);
                row.Children.Add(when);
                _recentList.Children.Add(row);
            }
            if (snap.ops.Count == 0)
                _recentList.Children.Add(UiKit.Muted("Belum ada operasi bridge pada sesi ini."));
        }
        catch { /* halaman pelengkap — gagal diam */ }
        finally { _busy = false; }
    }

    private static void ParseGpu(string gpuText, out string temp, out int util)
    {
        temp = ""; util = -1;
        if (string.IsNullOrEmpty(gpuText)) return;
        int deg = gpuText.IndexOf('°');
        if (deg > 0) temp = gpuText.Substring(0, deg).Trim();
        int dot = gpuText.IndexOf('·');
        if (dot >= 0)
        {
            string tail = gpuText.Substring(dot + 1).Trim().TrimEnd('%').Trim();
            if (int.TryParse(tail, out int v)) util = v;
        }
    }
}
