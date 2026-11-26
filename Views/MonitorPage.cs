using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// System Monitor v5.3.0 (halaman BARU, persis frame Figma):
/// 5 kartu metrik (CPU/RAM/GPU/Disk/Network), Performance Chart
/// dua garis (CPU cyan, RAM violet — riwayat 60 cuplikan asli),
/// dan Running Processes. Data dari NativeInput.SystemMetrics,
/// SysSensors (RAM/GPU/suhu CPU via LibreHardwareMonitor), dan
/// enumerasi proses Windows; persen CPU per proses dihitung dari
/// selisih TotalProcessorTime antar cuplikan.
/// </summary>
public sealed class MonitorPage : UserControl, IRoomPage
{
    private readonly TextBlock[] _vals = new TextBlock[5];
    private readonly TextBlock[] _subs = new TextBlock[5];
    private readonly Grid[] _bars = new Grid[5];
    private readonly Canvas _chart;
    private readonly StackPanel _procRows;
    private readonly Queue<int> _cpuHist = new Queue<int>();
    private readonly Queue<int> _ramHist = new Queue<int>();
    private readonly Dictionary<int, TimeSpan> _prevCpu = new Dictionary<int, TimeSpan>();
    private DateTime _prevWall = DateTime.MinValue;
    private readonly DispatcherTimer _timer;
    private CpuTempSensor _cpuTemp;
    private bool _busy;

    public MonitorPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- 5 kartu metrik ----
        var metrics = new Grid();
        string[] labels = { "CPU", "RAM", "GPU", "DISK", "NETWORK" };
        string[] colors = { "CrAccent", "CrSage", "CrGreen", "CrGold", "CrAccent" };
        for (int i = 0; i < 5; i++)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = labels[i],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiKit.Res("CrMuted"),
            });
            _vals[i] = new TextBlock
            {
                Text = "…",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 8, 0, 0),
            };
            stack.Children.Add(_vals[i]);
            _bars[i] = UiKit.Bar(colors[i], 5);
            _bars[i].Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(_bars[i]);
            _subs[i] = new TextBlock
            {
                Text = "",
                FontSize = 9.5,
                Foreground = UiKit.Res("CrFaint"),
                Margin = new Thickness(0, 8, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            stack.Children.Add(_subs[i]);
            var card = new Border
            {
                Style = (Style)Application.Current.FindResource("CrCardStyle"),
                Child = stack,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == 4 ? 0 : 6, 0),
            };
            Grid.SetColumn(card, i);
            metrics.Children.Add(card);
        }
        root.Children.Add(metrics);

        // ---- Grafik kinerja ----
        var chartStack = new StackPanel();
        _chart = new Canvas { Height = 190, ClipToBounds = true };
        _chart.SizeChanged += (s, e) => DrawChart();
        chartStack.Children.Add(_chart);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        legend.Children.Add(UiKit.Dot("CrAccent", 8));
        legend.Children.Add(new TextBlock { Text = "CPU", FontSize = 11, Foreground = UiKit.Res("CrMuted"), Margin = new Thickness(6, 0, 0, 0) });
        legend.Children.Add(new Border { Width = 18 });
        legend.Children.Add(UiKit.Dot("CrSage", 8));
        legend.Children.Add(new TextBlock { Text = "RAM", FontSize = 11, Foreground = UiKit.Res("CrMuted"), Margin = new Thickness(6, 0, 0, 0) });
        chartStack.Children.Add(legend);
        var chartCard = UiKit.Card("PERFORMANCE CHART — 60 CUPLIKAN TERAKHIR", chartStack);
        chartCard.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(chartCard, 1);
        root.Children.Add(chartCard);

        // ---- Proses berjalan ----
        _procRows = new StackPanel();
        var procScroll = new ScrollViewer
        {
            Content = _procRows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var procCard = UiKit.Card("RUNNING PROCESSES", procScroll);
        procCard.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(procCard, 2);
        root.Children.Add(procCard);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (s, e) => _ = RefreshAsync();
    }

    public void OnShown()
    {
        _timer.Start();
        _ = RefreshAsync();
        if (_cpuTemp == null)
        {
            var sensor = new CpuTempSensor();
            _cpuTemp = sensor;
            _ = Task.Run(() => sensor.TryInit());
        }
    }

    public void OnHidden() { _timer.Stop(); }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var server = App.Host?.Server;
            var snap = await Task.Run(() =>
            {
                var metrics = NativeInput.SystemMetrics();
                string gpu = SysSensors.GpuText();
                string ram = SysSensors.RamText();
                int? cpuTemp = null;
                try { cpuTemp = _cpuTemp != null && !_cpuTemp.Failed ? _cpuTemp.Read() : null; } catch { }
                long respMs = 0;
                if (server != null)
                {
                    var health = server.BuildHealth();
                    if (health.TryGetValue("ui", out var uiObj)
                        && uiObj is Dictionary<string, object> ui)
                        respMs = UiKit.GetLong(ui, "responsiveMs");
                }
                var procs = TopProcesses();
                return new { metrics, gpu, ram, cpuTemp, respMs, procs };
            });
            var m = snap.metrics;

            _vals[0].Text = m.CpuPercent >= 0 ? m.CpuPercent + "%" : "—";
            UiKit.SetBar(_bars[0], m.CpuPercent >= 0 ? m.CpuPercent : 0);
            _subs[0].Text = "i3-12100F • 3,3 GHz" + (snap.cpuTemp.HasValue ? " • " + snap.cpuTemp.Value + "°C" : "");

            _vals[1].Text = m.RamPercent >= 0 ? m.RamPercent + "%" : "—";
            UiKit.SetBar(_bars[1], m.RamPercent >= 0 ? m.RamPercent : 0);
            _subs[1].Text = snap.ram + " • XMP 3200";

            ParseGpu(snap.gpu, out string gTemp, out int gUtil);
            _vals[2].Text = gUtil >= 0 ? gUtil + "%" : "—";
            UiKit.SetBar(_bars[2], gUtil >= 0 ? gUtil : 0);
            _subs[2].Text = "GTX 1660 Super" + (gTemp.Length > 0 ? " • " + gTemp + "°C" : "");

            double diskPct = m.DiskTotalGb > 0
                ? (m.DiskTotalGb - m.DiskFreeGb) * 100.0 / m.DiskTotalGb : -1;
            _vals[3].Text = diskPct >= 0 ? (int)Math.Round(diskPct) + "%" : "—";
            UiKit.SetBar(_bars[3], diskPct >= 0 ? diskPct : 0);
            _subs[3].Text = "SSD " + m.DiskTotalGb + " GB • bebas " + m.DiskFreeGb + " GB";

            _vals[4].Text = snap.respMs > 0 ? snap.respMs + " ms" : "Relay";
            UiKit.SetBar(_bars[4], snap.respMs > 0 ? Math.Max(4, 100 - snap.respMs / 10.0) : 0);
            _subs[4].Text = "Tailscale relay • respons UI";

            if (m.CpuPercent >= 0) PushHist(_cpuHist, m.CpuPercent);
            if (m.RamPercent >= 0) PushHist(_ramHist, m.RamPercent);
            DrawChart();

            _procRows.Children.Clear();
            foreach (var p in snap.procs)
            {
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.Children.Add(new TextBlock
                {
                    Text = p.Name,
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var pid = new TextBlock { Text = "PID " + p.Pid, FontSize = 11.5, Foreground = UiKit.Res("CrMuted") };
                Grid.SetColumn(pid, 1);
                row.Children.Add(pid);
                var cpu = new TextBlock
                {
                    Text = p.CpuPct >= 0 ? p.CpuPct.ToString("0.0").Replace('.', ',') + "%" : "—",
                    FontSize = 11.5,
                    Foreground = UiKit.Res("CrMuted"),
                };
                Grid.SetColumn(cpu, 2);
                row.Children.Add(cpu);
                var ram = new TextBlock
                {
                    Text = p.RamMb + " MB",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                Grid.SetColumn(ram, 3);
                row.Children.Add(ram);
                _procRows.Children.Add(row);
            }
        }
        catch { /* halaman pelengkap — gagal diam */ }
        finally { _busy = false; }
    }

    private sealed class ProcRow
    {
        public string Name = "";
        public int Pid;
        public double CpuPct = -1;
        public long RamMb;
    }

    private List<ProcRow> TopProcesses()
    {
        var rows = new List<ProcRow>();
        try
        {
            var now = DateTime.UtcNow;
            double wallMs = _prevWall == DateTime.MinValue ? 0 : (now - _prevWall).TotalMilliseconds;
            var current = new Dictionary<int, TimeSpan>();
            var all = new List<ProcRow>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var row = new ProcRow
                    {
                        Name = p.ProcessName + ".exe",
                        Pid = p.Id,
                        RamMb = p.WorkingSet64 / 1048576,
                    };
                    var cpu = p.TotalProcessorTime;
                    current[p.Id] = cpu;
                    if (wallMs > 0 && _prevCpu.TryGetValue(p.Id, out var prev))
                    {
                        double usedMs = (cpu - prev).TotalMilliseconds;
                        row.CpuPct = usedMs / wallMs / Environment.ProcessorCount * 100.0;
                        if (row.CpuPct < 0) row.CpuPct = 0;
                    }
                    all.Add(row);
                }
                catch { /* proses sistem tak bisa dibaca — lewati */ }
                finally { p.Dispose(); }
            }
            _prevCpu.Clear();
            foreach (var kv in current) _prevCpu[kv.Key] = kv.Value;
            _prevWall = now;
            all.Sort((a, b) => b.RamMb.CompareTo(a.RamMb));
            for (int i = 0; i < all.Count && i < 6; i++) rows.Add(all[i]);
        }
        catch { /* abaikan */ }
        return rows;
    }

    private static void PushHist(Queue<int> q, int v)
    {
        q.Enqueue(v);
        while (q.Count > 60) q.Dequeue();
    }

    private void DrawChart()
    {
        if (_chart == null) return;
        double w = _chart.ActualWidth;
        double h = _chart.ActualHeight;
        if (w < 40 || h < 40) return;
        _chart.Children.Clear();
        for (int i = 0; i < 5; i++)
        {
            double y = 10 + i * ((h - 20) / 4);
            var line = new Line
            {
                X1 = 0, X2 = w, Y1 = y, Y2 = y,
                Stroke = UiKit.Res("CrBorder"),
                StrokeThickness = 1,
            };
            _chart.Children.Add(line);
        }
        AddLine(_cpuHist, "CrAccent", w, h);
        AddLine(_ramHist, "CrSage", w, h);
    }

    private void AddLine(Queue<int> hist, string colorKey, double w, double h)
    {
        if (hist.Count < 2) return;
        var pts = new PointCollection();
        int i = 0;
        foreach (var v in hist)
        {
            double x = w * i / 59.0;
            double y = (h - 14) - (v / 100.0) * (h - 28);
            pts.Add(new Point(x, y));
            i++;
        }
        _chart.Children.Add(new Polyline
        {
            Points = pts,
            Stroke = UiKit.Res(colorKey),
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        });
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
