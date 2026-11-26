using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jembatan;

/// <summary>
/// Activity Center v5.3.0 — persis frame Figma: kolom cari + chip
/// filter (Semua/Perintah/Mouse/Keyboard/Sistem/Error), Event
/// Timeline berbadge, dan kartu Event Details. Datanya gabungan
/// ASLI dua sumber aplikasi: operasi bridge (RecentOpsSnapshot)
/// dan log kejadian desktop (EventLog — fokus jendela &amp; proses).
/// Baris bisa diklik untuk detail; Salin Detail menyalin ke
/// clipboard; Ekspor Log menulis berkas teks ke folder Downloads.
/// (Penggabungan ini menyerap halaman System Logs lama.)
/// </summary>
public sealed class ActivityPage : UserControl, IRoomPage
{
    private sealed class Item
    {
        public string Time = "";
        public string Title = "";
        public string Detail = "";
        public string Category = "Sistem";
        public string BadgeText = "Info";
        public string BadgeColor = "CrAccent";
        public string DotColor = "CrAccent";
        public string Meta = "";
    }

    private readonly TextBox _search;
    private readonly StackPanel _timeline;
    private readonly Dictionary<string, Button> _chips = new Dictionary<string, Button>();
    private readonly TextBlock _detTitle, _detBody, _detMeta;
    private string _filter = "Semua";
    private List<Item> _items = new List<Item>();
    private Border _selectedRow;
    private Item _selectedItem;

    public ActivityPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- Baris cari + chip ----
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _search = UiKit.Input("");
        _search.Height = 38;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.ToolTip = "Cari kejadian, perintah, atau task id…";
        _search.TextChanged += (s, e) => RenderTimeline();
        top.Children.Add(_search);
        var chipRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var cat in new[] { "Semua", "Perintah", "Mouse", "Keyboard", "Sistem", "Error" })
        {
            var chip = new Button
            {
                Content = cat,
                Style = (Style)Application.Current.FindResource(cat == "Semua" ? "CrChipActive" : "CrChip"),
                Margin = new Thickness(6, 0, 0, 0),
                Tag = cat,
            };
            chip.Click += Chip_Click;
            _chips[cat] = chip;
            chipRow.Children.Add(chip);
        }
        Grid.SetColumn(chipRow, 1);
        top.Children.Add(chipRow);
        root.Children.Add(top);

        // ---- Linimasa ----
        _timeline = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _timeline,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var tlCard = UiKit.Card("EVENT TIMELINE", scroll);
        tlCard.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(tlCard, 1);
        root.Children.Add(tlCard);

        // ---- Detail kejadian ----
        var detStack = new StackPanel();
        _detTitle = new TextBlock
        {
            Text = "Pilih kejadian di linimasa",
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
        };
        detStack.Children.Add(_detTitle);
        _detBody = UiKit.Body("Detail kejadian yang dipilih tampil di sini.");
        _detBody.Margin = new Thickness(0, 8, 0, 0);
        detStack.Children.Add(_detBody);
        _detMeta = new TextBlock
        {
            Text = "",
            FontSize = 11.5,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = UiKit.Res("CrFaint"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        detStack.Children.Add(_detMeta);
        var detBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        detBtns.Children.Add(UiKit.Btn("Salin Detail", (s, e) =>
        {
            if (_selectedItem != null)
            {
                try
                {
                    Clipboard.SetText(_selectedItem.Title + " • " + _selectedItem.Time
                        + "\n" + _selectedItem.Detail + "\n" + _selectedItem.Meta);
                    _detBody.Text = "Detail tersalin ke clipboard.";
                }
                catch { /* clipboard sibuk — abaikan */ }
            }
        }));
        var exportBtn = UiKit.Btn("Ekspor Log", Export_Click);
        exportBtn.Margin = new Thickness(10, 0, 0, 0);
        detBtns.Children.Add(exportBtn);
        detStack.Children.Add(detBtns);
        var detCard = UiKit.Card("EVENT DETAILS", detStack);
        detCard.Margin = new Thickness(0, 14, 0, 0);
        Grid.SetRow(detCard, 2);
        root.Children.Add(detCard);

        Content = root;
    }

    public void OnShown() { _ = LoadAsync(); }
    public void OnHidden() { }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string cat)
        {
            _filter = cat;
            foreach (var kv in _chips)
                kv.Value.Style = (Style)Application.Current.FindResource(
                    kv.Key == cat ? "CrChipActive" : "CrChip");
            RenderTimeline();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            var server = App.Host?.Server;
            var items = await Task.Run(() =>
            {
                var list = new List<Item>();
                if (server != null)
                {
                    foreach (var op in server.RecentOpsSnapshot(60))
                    {
                        string status = op[3];
                        string badge = "Info"; string bc = "CrAccent";
                        if (int.TryParse(status, out int s))
                        {
                            if (s >= 200 && s < 300) { badge = "Sukses"; bc = "CrGreen"; }
                            else if (s >= 400 && s < 500) { badge = "Peringatan"; bc = "CrGold"; }
                            else if (s >= 500) { badge = "Error"; bc = "CrRed"; }
                        }
                        list.Add(new Item
                        {
                            Time = op[0],
                            Title = UiKit.FriendlyOp(op[1], op[2]),
                            Detail = op[1] + " " + op[2] + " • status " + status + " • " + op[4] + " ms"
                                + (op[6].Length > 0 ? " • " + op[6] : ""),
                            Category = bc == "CrRed" ? "Error" : "Perintah",
                            BadgeText = badge,
                            BadgeColor = bc,
                            DotColor = UiKit.StatusColor(status),
                            Meta = "Task ID: " + (op[5].Length > 0 ? op[5] : "—") + " • Sumber: bridge " + op[2],
                        });
                    }
                }
                foreach (var ev in EventLog.Since(Math.Max(0, EventLog.LastSeq - 200), 200))
                {
                    string kind = ev.Kind ?? "";
                    string cat = "Sistem"; string badge = "Info"; string bc = "CrSage"; string dc = "CrSage";
                    string title = kind;
                    if (kind.IndexOf("key", StringComparison.OrdinalIgnoreCase) >= 0) cat = "Keyboard";
                    else if (kind.IndexOf("click", StringComparison.OrdinalIgnoreCase) >= 0
                        || kind.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0) cat = "Mouse";
                    if (kind == "process-start") { title = "Proses mulai berjalan"; badge = "Sukses"; bc = "CrGreen"; dc = "CrGreen"; }
                    else if (kind == "process-stop") { title = "Proses berhenti"; badge = "Peringatan"; bc = "CrGold"; dc = "CrGold"; }
                    else if (kind == "focus") { title = "Fokus jendela berubah"; }
                    else if (kind.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                    { cat = "Error"; badge = "Error"; bc = "CrRed"; dc = "CrRed"; }
                    list.Add(new Item
                    {
                        Time = ev.Time ?? "",
                        Title = title,
                        Detail = ev.Detail ?? "",
                        Category = cat,
                        BadgeText = badge,
                        BadgeColor = bc,
                        DotColor = dc,
                        Meta = "Seq #" + ev.Seq
                            + (ev.Pid > 0 ? " • PID " + ev.Pid : "")
                            + (ev.Hwnd != 0 ? " • hwnd " + ev.Hwnd : "")
                            + " • Sumber: EventLog desktop",
                    });
                }
                list.Sort((a, b) => string.Compare(b.Time, a.Time, StringComparison.Ordinal));
                return list;
            });
            _items = items;
            RenderTimeline();
            // Pilihan awal: kejadian error pertama, kalau tidak ada
            // ya yang paling atas — sama seperti contoh Figma.
            Item first = null;
            foreach (var it in _items) if (it.BadgeText == "Error") { first = it; break; }
            if (first == null && _items.Count > 0) first = _items[0];
            if (first != null) SelectItem(first, null);
        }
        catch { /* halaman pelengkap — gagal diam */ }
    }

    private void RenderTimeline()
    {
        _timeline.Children.Clear();
        string query = (_search.Text ?? "").Trim();
        int shown = 0;
        foreach (var item in _items)
        {
            if (_filter != "Semua" && item.Category != _filter) continue;
            if (query.Length > 0
                && item.Title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && item.Detail.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                && item.Meta.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _timeline.Children.Add(BuildRow(item));
            shown++;
        }
        if (shown == 0)
        {
            var empty = UiKit.Muted("Tidak ada kejadian yang cocok.");
            empty.Margin = new Thickness(0, 6, 0, 6);
            _timeline.Children.Add(empty);
        }
    }

    private Border BuildRow(Item item)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UiKit.Dot(item.DotColor, 9));
        var texts = new StackPanel { Margin = new Thickness(12, -3, 8, 0) };
        texts.Children.Add(new TextBlock
        {
            Text = item.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        texts.Children.Add(new TextBlock
        {
            Text = item.Detail,
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var badge = UiKit.Badge(item.BadgeText, item.BadgeColor);
        badge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);
        var when = new TextBlock
        {
            Text = item.Time,
            FontSize = 10.5,
            Foreground = UiKit.Res("CrFaint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(when, 3);
        grid.Children.Add(when);

        var row = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 7, 8, 7),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Colors.Transparent),
            Cursor = Cursors.Hand,
            Tag = item,
        };
        row.MouseLeftButtonDown += (s, e) => SelectItem(item, row);
        return row;
    }

    private void SelectItem(Item item, Border row)
    {
        _selectedItem = item;
        if (_selectedRow != null)
            _selectedRow.Background = new SolidColorBrush(Colors.Transparent);
        _selectedRow = row;
        if (row != null) row.Background = UiKit.Res("CrElevated");
        _detTitle.Text = item.Title + " • " + item.Time;
        _detTitle.Foreground = UiKit.Res(item.BadgeColor == "CrRed" ? "CrRed" : "CrText");
        _detBody.Text = item.Detail;
        _detMeta.Text = item.Meta;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("MuseLink — Ekspor Log Aktivitas — " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine(new string('=', 60));
            foreach (var item in _items)
                sb.AppendLine(item.Time + "  [" + item.Category + "/" + item.BadgeText + "]  "
                    + item.Title + " — " + item.Detail + "  (" + item.Meta + ")");
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string file = Path.Combine(dir, "MuseLink-Log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            _detTitle.Text = "Log diekspor";
            _detBody.Text = "Tersimpan di " + file;
            _detMeta.Text = _items.Count + " kejadian";
        }
        catch (Exception ex)
        {
            _detBody.Text = "Ekspor gagal: " + ex.Message;
        }
    }
}
