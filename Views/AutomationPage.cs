using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Jembatan;

/// <summary>
/// Automation Center v5.3.0 — persis frame Figma: header Workflow
/// Dashboard + tombol + Workflow, kartu-kartu workflow, Workflow
/// Editor ber-node, dan Execution History. Kartu diisi rutinitas
/// ASLI yang tersimpan di PC (Routines) — klik kartu memilihnya
/// dan editor menampilkan langkah-langkahnya dari JSON aksi;
/// tombol Hapus sungguh menghapus. Bila belum ada rutinitas,
/// tampil tiga kartu contoh desain Figma berbadge "Contoh".
/// Tombol putar/jeda/stop, + Workflow, Simpan, dan Uji Jalan
/// masih contoh desain — eksekusi workflow berjalan lewat bridge
/// (fitur menyusul di halaman ini).
/// </summary>
public sealed class AutomationPage : UserControl, IRoomPage
{
    private sealed class StepInfo
    {
        public string Kind = "Action";
        public string Label = "";
        public string Color = "CrAccent";
    }

    private readonly WrapPanel _cards;
    private readonly StackPanel _nodes, _history;
    private readonly TextBlock _editorTitle, _editorNote;
    private string _selectedName;
    private Border _selectedCard;

    private static readonly (string Name, string Desc)[] Samples =
    {
        ("Streaming Starter Kit", "OBS + wPlay + TikTok LIVE Studio"),
        ("Backup Konfigurasi", "Salin profil OBS & MuseLink ke D:\\Backup"),
        ("Build & Rilis", "Push → CI → unduh installer → pasang"),
    };

    public AutomationPage()
    {
        var root = new StackPanel();

        // ---- Header ----
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = UiKit.SectionLabel("WORKFLOW DASHBOARD");
        title.FontSize = 10.5;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        var delBtn = UiKit.Btn("Hapus", Delete_Click);
        Grid.SetColumn(delBtn, 1);
        header.Children.Add(delBtn);
        var addBtn = UiKit.PrimaryBtn("＋ Workflow", null);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(addBtn, 2);
        header.Children.Add(addBtn);
        root.Children.Add(header);

        // ---- Kartu workflow ----
        _cards = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        root.Children.Add(_cards);

        // ---- Editor ----
        var edStack = new StackPanel();
        _nodes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        edStack.Children.Add(_nodes);
        _editorNote = UiKit.Muted("Pilih kartu workflow untuk melihat langkah-langkahnya.");
        _editorNote.FontSize = 11.5;
        _editorNote.Foreground = UiKit.Res("CrFaint");
        _editorNote.Margin = new Thickness(0, 14, 0, 0);
        edStack.Children.Add(_editorNote);
        var edBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        edBtns.Children.Add(UiKit.PrimaryBtn("Simpan", null));
        var testBtn = UiKit.Btn("Uji Jalan", null);
        testBtn.Margin = new Thickness(10, 0, 0, 0);
        edBtns.Children.Add(testBtn);
        edStack.Children.Add(edBtns);
        _editorTitle = UiKit.CardTitle("WORKFLOW EDITOR");
        var edInner = new StackPanel();
        edInner.Children.Add(_editorTitle);
        edInner.Children.Add(edStack);
        var edCard = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = edInner,
            Margin = new Thickness(0, 16, 0, 0),
        };
        root.Children.Add(edCard);

        // ---- Riwayat ----
        _history = new StackPanel();
        var histCard = UiKit.Card("EXECUTION HISTORY", _history);
        histCard.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(histCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void OnShown() { RefreshAll(); }
    public void OnHidden() { }

    private void RefreshAll()
    {
        _cards.Children.Clear();
        _selectedCard = null;
        List<KeyValuePair<string, int>> routines;
        try { routines = Routines.List(); }
        catch { routines = new List<KeyValuePair<string, int>>(); }

        if (routines.Count == 0)
        {
            foreach (var (name, desc) in Samples)
                _cards.Children.Add(WorkflowCard(name, desc, "Contoh", "CrGold", null));
            ShowSampleEditor();
        }
        else
        {
            foreach (var kv in routines)
                _cards.Children.Add(WorkflowCard(kv.Key, kv.Value + " langkah • tersimpan di PC ini",
                    "Siap", "CrGreen", kv.Key));
            SelectRoutine(routines[0].Key);
        }
        RefreshHistory();
    }

    private Border WorkflowCard(string name, string desc, string badge, string badgeColor, string routineName)
    {
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var d = UiKit.Muted(desc);
        d.FontSize = 11;
        d.Margin = new Thickness(0, 4, 0, 0);
        d.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(d);
        var b = UiKit.Badge(badge, badgeColor);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        b.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(b);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var play = UiKit.PrimaryBtn("▶", null);
        play.Padding = new Thickness(16, 5, 16, 5);
        btns.Children.Add(play);
        var pause = UiKit.Btn("❚❚", null);
        pause.Padding = new Thickness(16, 5, 16, 5);
        pause.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(pause);
        var stop = UiKit.DangerBtn("■", null);
        stop.Padding = new Thickness(16, 5, 16, 5);
        stop.Margin = new Thickness(8, 0, 0, 0);
        btns.Children.Add(stop);
        stack.Children.Add(btns);

        var card = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = stack,
            Width = 272,
            Margin = new Thickness(0, 6, 14, 6),
            Tag = routineName,
        };
        if (routineName != null)
        {
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonDown += (s, e) => SelectRoutine(routineName);
        }
        return card;
    }

    private void SelectRoutine(string name)
    {
        _selectedName = name;
        foreach (var child in _cards.Children)
        {
            if (child is Border card)
            {
                bool sel = card.Tag is string t && t == name;
                card.BorderBrush = UiKit.Res(sel ? "CrAccent" : "CrBorder");
                if (sel) _selectedCard = card;
            }
        }
        if (_editorTitle != null)
            _editorTitle.Text = "WORKFLOW EDITOR — " + (name ?? "").ToUpperInvariant();
        if (name != null && Routines.TryGet(name, out string json))
        {
            var steps = ParseSteps(json);
            RenderNodes(steps);
            _editorNote.Text = steps.Count + " langkah dari rutinitas tersimpan • " + name
                + " — editor visual masih contoh desain; mengubah langkah menyusul.";
        }
    }

    private void ShowSampleEditor()
    {
        _selectedName = null;
        if (_editorTitle != null) _editorTitle.Text = "WORKFLOW EDITOR — STREAMING STARTER KIT";
        RenderNodes(new List<StepInfo>
        {
            new StepInfo { Kind = "Trigger", Label = "Jadwal 19:00", Color = "CrAccent" },
            new StepInfo { Kind = "Action", Label = "Buka OBS Studio", Color = "CrSage" },
            new StepInfo { Kind = "Condition", Label = "Scene MLBB Pro?", Color = "CrGold" },
            new StepInfo { Kind = "Action", Label = "Mulai Streaming", Color = "CrGreen" },
        });
        _editorNote.Text = "Contoh editor dari desain Figma (Streaming Starter Kit). "
            + "Rutinitas sungguhan yang dibuat lewat bridge akan tampil sebagai kartu di atas.";
    }

    private void RenderNodes(List<StepInfo> steps)
    {
        _nodes.Children.Clear();
        int shown = 0;
        foreach (var step in steps)
        {
            if (shown >= 6) break;
            if (shown > 0)
                _nodes.Children.Add(new TextBlock
                {
                    Text = "→",
                    FontSize = 16,
                    Foreground = UiKit.Res("CrFaint"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                });
            var inner = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            var kind = UiKit.SectionLabel(step.Kind.ToUpperInvariant());
            kind.Foreground = UiKit.Res(step.Color);
            inner.Children.Add(kind);
            inner.Children.Add(new TextBlock
            {
                Text = step.Label,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
                MaxWidth = 150,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            _nodes.Children.Add(new Border
            {
                Background = UiKit.Res("CrElevated"),
                BorderBrush = UiKit.Res(step.Color),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = inner,
            });
            shown++;
        }
        if (steps.Count > 6)
            _nodes.Children.Add(new TextBlock
            {
                Text = "+" + (steps.Count - 6) + " langkah",
                FontSize = 11.5,
                Foreground = UiKit.Res("CrFaint"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            });
        if (steps.Count == 0)
            _nodes.Children.Add(UiKit.Muted("(rutinitas kosong)"));
    }

    private static List<StepInfo> ParseSteps(string json)
    {
        var steps = new List<StepInfo>();
        string[] colors = { "CrAccent", "CrSage", "CrGold", "CrGreen" };
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string action = "";
                        if (el.ValueKind == JsonValueKind.Object
                            && el.TryGetProperty("action", out var a))
                            action = a.GetString() ?? "";
                        else if (el.ValueKind == JsonValueKind.String)
                            action = el.GetString() ?? "";
                        string kind = "Action";
                        if (action.StartsWith("wait")) kind = "Trigger";
                        else if (action.StartsWith("if")) kind = "Condition";
                        steps.Add(new StepInfo
                        {
                            Kind = kind,
                            Label = action.Length > 0 ? action : "langkah " + (i + 1),
                            Color = colors[i % colors.Length],
                        });
                        i++;
                    }
                }
            }
        }
        catch { /* JSON rusak — editor tampil kosong */ }
        return steps;
    }

    private void RefreshHistory()
    {
        _history.Children.Clear();
        try
        {
            var server = App.Host?.Server;
            if (server == null) return;
            int shown = 0;
            foreach (var op in server.RecentOpsSnapshot(40))
            {
                if (op[2].IndexOf("routines", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(UiKit.Dot(UiKit.StatusColor(op[3]), 8));
                var name = new TextBlock
                {
                    Text = op[6].Length > 0 ? op[6] : UiKit.FriendlyOp(op[1], op[2]),
                    FontSize = 12.5,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(10, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetColumn(name, 1);
                row.Children.Add(name);
                var mid = new TextBlock
                {
                    Text = "status " + op[3] + " • " + op[4] + " ms",
                    FontSize = 11.5,
                    Foreground = UiKit.Res("CrMuted"),
                };
                Grid.SetColumn(mid, 2);
                row.Children.Add(mid);
                var when = new TextBlock
                {
                    Text = op[0],
                    FontSize = 11,
                    Foreground = UiKit.Res("CrFaint"),
                    Margin = new Thickness(10, 0, 0, 0),
                };
                Grid.SetColumn(when, 3);
                row.Children.Add(when);
                _history.Children.Add(row);
                shown++;
            }
            if (shown == 0)
                _history.Children.Add(UiKit.Muted("(belum ada riwayat eksekusi workflow tercatat)"));
        }
        catch { /* pelengkap */ }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedName != null && Routines.Delete(_selectedName))
        {
            App.Host?.Log("Rutinitas dihapus dari Automation Center: " + _selectedName);
            _selectedName = null;
            RefreshAll();
        }
    }
}
