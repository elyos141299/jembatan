using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Jembatan;

/// <summary>
/// Muse Console v5.3.0 — tata letak persis frame Figma: bilah
/// percakapan kiri (tombol Percakapan Baru, cari, daftar), area
/// chat dengan bubble pengguna, teks Muse, dan kartu COMMAND
/// EXECUTION; bar input bawah (+, kolom tulis, Stop, Kirim).
/// Isi daftar percakapan masih contoh desain (fitur percakapan
/// menyusul); kolom input SUNGGUH menjalankan PowerShell di PC
/// ini — fungsi Command Center lama yang dipertahankan — dan
/// hasilnya tampil sebagai kartu eksekusi.
/// </summary>
public sealed class ConsolePage : UserControl, IRoomPage
{
    private readonly StackPanel _chat;
    private readonly ScrollViewer _scroll;
    private readonly TextBox _input;
    private readonly Button _stopBtn;
    private CancellationTokenSource _cts;
    private bool _running;

    private sealed class ExecCard
    {
        public Ellipse Dot;
        public Border Badge;
        public TextBlock Output;
        public TextBlock Footer;
    }

    public ConsolePage()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Bilah percakapan ----
        var sideStack = new StackPanel { Margin = new Thickness(16) };
        var newBtn = UiKit.PrimaryBtn("＋ Percakapan Baru", (s, e) => ResetChat());
        sideStack.Children.Add(newBtn);
        var search = new Border
        {
            Background = UiKit.Res("CrElevated"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 12, 0, 0),
            Child = new TextBlock
            {
                Text = "⌕  Cari percakapan…",
                FontSize = 11.5,
                Foreground = UiKit.Res("CrFaint"),
            },
        };
        sideStack.Children.Add(search);
        var chats = new[]
        {
            ("Desain Figma MuseLink", "06:01", true),
            ("Rombak UI v5 WPF", "04:44", false),
            ("Audit kecepatan #48", "Kemarin", false),
            ("Setup OBS MLBB Pro", "Kemarin", false),
            ("Bedah iPocket MF01", "25 Nov", false),
        };
        foreach (var (title, ago, active) in chats)
        {
            var item = new Border
            {
                Background = active ? UiKit.Res("CrElevated") : new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var st = new StackPanel();
            st.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = UiKit.Res(active ? "CrText" : "CrMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            st.Children.Add(new TextBlock
            {
                Text = ago,
                FontSize = 10.5,
                Foreground = UiKit.Res("CrFaint"),
                Margin = new Thickness(0, 2, 0, 0),
            });
            item.Child = st;
            sideStack.Children.Add(item);
        }
        var side = new Border
        {
            Background = UiKit.Res("CrPanel"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = sideStack,
        };
        root.Children.Add(side);

        // ---- Area chat ----
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
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var plusBtn = UiKit.Btn("＋", null);
        plusBtn.Height = 44;
        plusBtn.FontSize = 16;
        inputGrid.Children.Add(plusBtn);
        _input = UiKit.Input("");
        _input.Height = 44;
        _input.Margin = new Thickness(10, 0, 0, 0);
        _input.VerticalContentAlignment = VerticalAlignment.Center;
        _input.FontFamily = new FontFamily("Consolas");
        _input.ToolTip = "Tulis perintah PowerShell, lalu Enter atau Kirim.";
        _input.KeyDown += Input_KeyDown;
        Grid.SetColumn(_input, 1);
        inputGrid.Children.Add(_input);
        _stopBtn = UiKit.DangerBtn("Stop", (s, e) => _cts?.Cancel());
        _stopBtn.Height = 44;
        _stopBtn.Margin = new Thickness(10, 0, 0, 0);
        _stopBtn.IsEnabled = false;
        Grid.SetColumn(_stopBtn, 2);
        inputGrid.Children.Add(_stopBtn);
        var sendBtn = UiKit.PrimaryBtn("Kirim", (s, e) => Send());
        sendBtn.Height = 44;
        sendBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(sendBtn, 3);
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

    public void OnShown() { _input.Focus(); }
    public void OnHidden() { }

    private void ResetChat()
    {
        _chat.Children.Clear();
        AddMuseText("Siap. Tulis perintah PowerShell di kolom bawah — perintah berjalan di PC ini "
            + "dan hasilnya tampil sebagai kartu eksekusi, sama seperti Command Center sebelumnya. "
            + "Daftar percakapan di kiri masih contoh desain; fitur percakapan penuh menyusul.");
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
