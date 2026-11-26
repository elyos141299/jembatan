using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Jembatan;

/// <summary>
/// Halaman AI v5.7.0 (Tray-First) — gabungan Muse Console
/// lama (tanpa bilah percakapan contoh yang display-only)
/// dengan status mesin AI &amp; integrasi:
/// - Kiri: kartu MESIN AI (keadaan sidecar/GPU/model dari
///   AiBridge — label jujur: sidecar "siaga" karena memang
///   menyala malas saat dipakai) dan kartu INTEGRASI (status
///   aplikasi terdeteksi di PC, logika yang sama seperti
///   halaman Integrations lama; tanpa tombol Configure palsu).
/// - Kanan: konsol perintah PowerShell yang SUNGGUH berjalan
///   di PC ini, hasil sebagai kartu eksekusi — fungsi Command
///   Center lama yang dipertahankan utuh.
/// </summary>
public sealed class AiPage : UserControl, IRoomPage
{
    private readonly StackPanel _chat;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _stopBtn;
    private CancellationTokenSource _cts;
    private bool _running;

    private readonly TextBlock _aiSidecar, _aiDevice, _aiModel, _aiInstalled;
    private readonly StackPanel _integList;

    private sealed class ExecCard
    {
        public Ellipse Dot;
        public Border Badge;
        public TextBlock Output;
        public TextBlock Footer;
    }

    private sealed class IntegApp
    {
        public string Name = "";
        public string ProcessName = "";   // "" = khusus (GitHub via gh)
    }

    private static readonly IntegApp[] IntegApps =
    {
        new IntegApp { Name = "Chrome", ProcessName = "chrome" },
        new IntegApp { Name = "OBS Studio", ProcessName = "obs64" },
        new IntegApp { Name = "Figma", ProcessName = "Figma" },
        new IntegApp { Name = "GitHub", ProcessName = "" },
        new IntegApp { Name = "Spotify", ProcessName = "Spotify" },
        new IntegApp { Name = "WhatsApp", ProcessName = "WhatsApp" },
    };

    public AiPage()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Kolom kiri: status mesin AI + integrasi ----
        var leftStack = new StackPanel();

        var aiStack = new StackPanel();
        _aiInstalled = AddStatusRow(aiStack, "Paket AI", "…");
        _aiSidecar = AddStatusRow(aiStack, "Sidecar", "…");
        _aiDevice = AddStatusRow(aiStack, "Perangkat", "…");
        _aiModel = AddStatusRow(aiStack, "Model deteksi", "…");
        var aiNote = UiKit.Muted("OCR & deteksi berjalan di GPU (EasyOCR + YOLO); "
            + "transkripsi suara memakai Whisper. Semua lewat sidecar lokal — tanpa API key.");
        aiNote.FontSize = 11;
        aiNote.Margin = new Thickness(0, 10, 0, 0);
        aiStack.Children.Add(aiNote);
        leftStack.Children.Add(UiKit.Card("MESIN AI", aiStack));

        _integList = new StackPanel();
        var integCard = UiKit.Card("INTEGRASI", _integList);
        integCard.Margin = new Thickness(0, 16, 0, 0);
        leftStack.Children.Add(integCard);

        var leftScroll = new ScrollViewer
        {
            Content = leftStack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        root.Children.Add(leftScroll);

        // ---- Kolom kanan: konsol perintah ----
        var chatGrid = new Grid();
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12, 18, 12),
        };
        var headRow = new StackPanel { Orientation = Orientation.Horizontal };
        headRow.Children.Add(UiKit.Dot("CrGreen", 9));
        headRow.Children.Add(new TextBlock
        {
            Text = "Muse",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(8, 0, 0, 0),
        });
        headRow.Children.Add(new TextBlock
        {
            Text = "• Online — siap mengeksekusi",
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
            Margin = new Thickness(6, 1, 0, 0),
        });
        header.Child = headRow;
        chatGrid.Children.Add(header);

        _chat = new StackPanel { Margin = new Thickness(18) };
        _scroll = new ScrollViewer
        {
            Content = _chat,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_scroll, 1);
        chatGrid.Children.Add(_scroll);

        var inputBorder = new Border
        {
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14),
        };
        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = UiKit.Input("");
        _input.Height = 44;
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.FontFamily = new FontFamily("Consolas");
        _input.ToolTip = "Tulis perintah PowerShell, lalu Enter atau Kirim.";
        _input.KeyDown += Input_KeyDown;
        inputGrid.Children.Add(_input);
        _stopBtn = UiKit.DangerBtn("Stop", (s, e) => _cts?.Cancel());
        _stopBtn.Height = 44;
        _stopBtn.Margin = new Thickness(10, 0, 0, 0);
        _stopBtn.IsEnabled = false;
        Grid.SetColumn(_stopBtn, 1);
        inputGrid.Children.Add(_stopBtn);
        var sendBtn = UiKit.PrimaryBtn("Kirim", (s, e) => Send());
        sendBtn.Height = 44;
        sendBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(sendBtn, 2);
        inputGrid.Children.Add(sendBtn);
        inputBorder.Child = inputGrid;
        Grid.SetRow(inputBorder, 2);
        chatGrid.Children.Add(inputBorder);

        var chatPanel = new Border
        {
            Background = UiKit.Res("CrPanel"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = chatGrid,
            Margin = new Thickness(16, 0, 0, 0),
        };
        Grid.SetColumn(chatPanel, 1);
        root.Children.Add(chatPanel);

        Content = root;
        ResetChat();
    }

    private static TextBlock AddStatusRow(StackPanel parent, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = UiKit.Res("CrMuted"),
        });
        var val = new TextBlock
        {
            Text = value,
            FontSize = 12,
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
        _input.Focus();
        RefreshSideStatus();
    }

    public void OnHidden() { }

    private void RefreshSideStatus()
    {
        try
        {
            var info = AiBridge.HealthInfo();
            _aiInstalled.Text = UiKit.GetBool(info, "installed") ? "Terpasang" : "Tidak ditemukan";
            string sc = UiKit.GetStr(info, "sidecar");
            // Jujur: sidecar menyala malas — "offline" di sini
            // berarti siaga, bukan rusak.
            _aiSidecar.Text = sc == "online" ? "Online" : "Siaga (menyala saat dipakai)";
            string dev = UiKit.GetStr(info, "device");
            _aiDevice.Text = dev == "cuda" ? "GPU (CUDA)" : (dev.Length > 0 ? dev : "—");
            string model = UiKit.GetStr(info, "model");
            _aiModel.Text = model.Length > 0 ? model : "Belum dimuat";
        }
        catch { /* pelengkap */ }

        _integList.Children.Clear();
        foreach (var app in IntegApps)
        {
            bool on = CheckConnected(app);
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(UiKit.Dot(on ? "CrGreen" : "CrFaint", 8));
            var name = new TextBlock
            {
                Text = app.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(10, -2, 0, 0),
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var st = new TextBlock
            {
                Text = on ? "Terhubung" : "Belum",
                FontSize = 11,
                Foreground = UiKit.Res(on ? "CrGreen" : "CrFaint"),
            };
            Grid.SetColumn(st, 2);
            row.Children.Add(st);
            _integList.Children.Add(row);
        }
    }

    private static bool CheckConnected(IntegApp app)
    {
        try
        {
            if (app.Name == "GitHub")
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    if (dir.Length == 0) continue;
                    if (File.Exists(Path.Combine(dir.Trim(), "gh.exe"))) return true;
                }
                return false;
            }
            return Process.GetProcessesByName(app.ProcessName).Length > 0;
        }
        catch { return false; }
    }

    // ---------- Konsol perintah (dari ConsolePage, utuh) ----------

    private void ResetChat()
    {
        _chat.Children.Clear();
        AddMuseText("Siap. Tulis perintah PowerShell di kolom bawah — perintah berjalan di PC ini "
            + "dan hasilnya tampil sebagai kartu eksekusi, sama seperti Command Center sebelumnya. "
            + "Panel kiri menampilkan keadaan mesin AI & integrasi di PC ini.");
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Send();
        }
    }

    private void Send()
    {
        string cmd = _input.Text.Trim();
        if (cmd.Length == 0 || _running) return;
        _input.Text = "";
        AddUserBubble(cmd);
        _ = RunCommandAsync(cmd);
    }

    private void AddUserBubble(string text)
    {
        var bubble = new Border
        {
            Background = UiKit.Res("CrElevated"),
            BorderBrush = UiKit.Res("CrAccent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 14, 10),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
            },
        };
        _chat.Children.Add(bubble);
        _chat.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("HH:mm"),
            FontSize = 10,
            Foreground = UiKit.Res("CrFaint"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 3, 2, 10),
        });
        ScrollToEnd();
    }

    private void AddMuseText(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        row.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = UiKit.Res("CrSage"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4, 5, 10, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
        });
        _chat.Children.Add(row);
        ScrollToEnd();
    }

    private ExecCard AddExecCard(string cmd)
    {
        var card = new ExecCard();
        var stack = new StackPanel { Margin = new Thickness(14) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Dot = new Ellipse
        {
            Width = 8, Height = 8,
            Fill = UiKit.Res("CrAccent"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(card.Dot);
        var label = UiKit.SectionLabel("COMMAND EXECUTION");
        label.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(label, 1);
        head.Children.Add(label);
        card.Badge = UiKit.Badge("Berjalan", "CrAccent");
        Grid.SetColumn(card.Badge, 2);
        head.Children.Add(card.Badge);
        stack.Children.Add(head);

        string shown = cmd.Length > 90 ? cmd.Substring(0, 90) + "…" : cmd;
        stack.Children.Add(new TextBlock
        {
            Text = "❯  " + shown,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });
        card.Output = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = UiKit.Res("CrMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(card.Output);
        card.Footer = new TextBlock
        {
            Text = "powershell • jembatan • berjalan…",
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = UiKit.Res("CrFaint"),
            Margin = new Thickness(0, 10, 0, 0),
        };
        stack.Children.Add(card.Footer);

        var border = new Border
        {
            Background = UiKit.Res("CrBg"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = stack,
            Margin = new Thickness(0, 2, 0, 12),
        };
        _chat.Children.Add(border);
        ScrollToEnd();
        return card;
    }

    private async Task RunCommandAsync(string cmd)
    {
        _running = true;
        _stopBtn.IsEnabled = true;
        _cts = new CancellationTokenSource();
        var card = AddExecCard(cmd);
        var sw = Stopwatch.StartNew();
        string status = "Selesai";
        string badgeColor = "CrGreen";
        string output = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -Command " + cmd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                while (!p.WaitForExit(120))
                {
                    if (_cts.IsCancellationRequested)
                    {
                        try { p.Kill(true); } catch { /* abaikan */ }
                        break;
                    }
                }
                string so = await outTask;
                string se = await errTask;
                output = (so + se).Trim();
                if (_cts.IsCancellationRequested) { status = "Dihentikan"; badgeColor = "CrGold"; }
                else if (p.ExitCode != 0) { status = "Gagal"; badgeColor = "CrRed"; }
            }
        }
        catch (Exception ex)
        {
            status = "Gagal";
            badgeColor = "CrRed";
            output = ex.Message;
        }
        sw.Stop();
        if (output.Length > 3000) output = output.Substring(0, 3000) + "\n… (dipotong)";
        card.Output.Text = output.Length > 0 ? output : "(tidak ada keluaran)";
        card.Dot.Fill = UiKit.Res(badgeColor);
        var old = card.Badge;
        var parent = old.Parent as Grid;
        var nb = UiKit.Badge(status, badgeColor);
        if (parent != null)
        {
            int idx = parent.Children.IndexOf(old);
            parent.Children.RemoveAt(idx);
            Grid.SetColumn(nb, 2);
            parent.Children.Insert(idx, nb);
            card.Badge = nb;
        }
        card.Footer.Text = "powershell • jembatan • selesai dalam " + sw.ElapsedMilliseconds + " ms";
        _running = false;
        _stopBtn.IsEnabled = false;
        ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(new Action(() => _scroll.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Background);
    }
}
