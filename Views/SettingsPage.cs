using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Settings v5.3.0 — persis frame Figma: daftar 9 kategori di
/// kiri, panel isi di kanan. Panel General mengikuti desain
/// (nama perangkat, bahasa, 4 baris saklar, pilihan tema, tombol
/// Simpan/Reset, footer versi): tiga saklar pertama SUNGGUH
/// berfungsi (Jalan otomatis, Cegah PC tidur, Mode Administrator
/// — penyimpanan &amp; penerapan yang sama seperti sebelumnya);
/// saklar Notifikasi, kolom bahasa, dan pilihan tema masih
/// contoh desain (fitur menyusul). Isi lama Permissions &amp;
/// Security + Connections dilipat ke kategori Desktop Bridge
/// (status bridge, Putuskan/Sambungkan, Emergency Stop, mode &amp;
/// lease — semua berfungsi), dan About memuat info versi asli.
/// Kategori lain tampil sebagai panel judul (isi menyusul).
/// </summary>
public sealed class SettingsPage : UserControl, IRoomPage
{
    private readonly ContentControl _host;
    private readonly Dictionary<string, UIElement> _panels = new Dictionary<string, UIElement>();
    private bool _loading = true;

    // Panel General
    private CheckBox _tgAuto, _tgAwake, _tgAdmin, _tgNotif;
    private TextBlock _genFooter;

    // Panel Desktop Bridge
    private TextBlock _brStatus, _brMode, _brLease, _brStop;
    private Button _brToggleBtn, _brStopBtn;
    private bool _brHalted;

    private static readonly string[] Categories =
    {
        "General", "Appearance", "Muse Connection", "Desktop Bridge",
        "Screen", "Performance", "Notifications", "Diagnostics", "About",
    };

    public SettingsPage()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Daftar kategori ----
        var catStack = new StackPanel { Margin = new Thickness(10) };
        RadioButton firstRb = null;
        for (int i = 0; i < Categories.Length; i++)
        {
            var rb = new RadioButton
            {
                Style = (Style)Application.Current.FindResource("CrNavButton"),
                GroupName = "SettingsCat",
                Tag = Categories[i],
                Content = new TextBlock { Text = Categories[i], FontSize = 12.5 },
            };
            rb.Checked += Cat_Checked;
            catStack.Children.Add(rb);
            if (i == 0) firstRb = rb;
        }
        var catPanel = new Border
        {
            Background = UiKit.Res("CrPanel"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = catStack,
        };
        root.Children.Add(catPanel);

        _host = new ContentControl { Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(_host, 1);
        root.Children.Add(_host);

        Content = root;
        // Dicentang terakhir: Checked memicu ShowCategory yang
        // butuh _host sudah ada.
        if (firstRb != null) firstRb.IsChecked = true;
        if (_host.Content == null) ShowCategory("General");
    }

    public void OnShown()
    {
        RefreshGeneral();
        if (_panels.ContainsKey("Desktop Bridge")) _ = RefreshBridgeAsync();
    }

    public void OnHidden() { }

    private void Cat_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string cat) ShowCategory(cat);
    }

    private void ShowCategory(string cat)
    {
        if (!_panels.TryGetValue(cat, out var panel))
        {
            panel = cat switch
            {
                "General" => BuildGeneral(),
                "Desktop Bridge" => BuildBridge(),
                "About" => BuildAbout(),
                "Appearance" => BuildAppearance(),
                _ => BuildPlaceholder(cat),
            };
            _panels[cat] = panel;
        }
        _host.Content = panel;
        if (cat == "General") RefreshGeneral();
        if (cat == "Desktop Bridge") _ = RefreshBridgeAsync();
    }

    // ---------- Panel pembungkus ----------

    private static Border PanelShell(UIElement content)
        => new Border
        {
            Background = UiKit.Res("CrPanel"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
        };

    // ---------- General ----------

    private UIElement BuildGeneral()
    {
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(UiKit.SectionLabel("GENERAL"));

        var fields = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameStack = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        nameStack.Children.Add(UiKit.Muted("Nama perangkat"));
        var nameBox = UiKit.Input(Environment.MachineName);
        nameBox.Margin = new Thickness(0, 6, 0, 0);
        nameStack.Children.Add(nameBox);
        fields.Children.Add(nameStack);
        var langStack = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        langStack.Children.Add(UiKit.Muted("Bahasa"));
        var langBox = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("CrComboBox"),
            Margin = new Thickness(0, 6, 0, 0),
        };
        langBox.Items.Add("Indonesia");
        langBox.Items.Add("English");
        langBox.SelectedIndex = 0;
        langStack.Children.Add(langBox);
        Grid.SetColumn(langStack, 1);
        fields.Children.Add(langStack);
        stack.Children.Add(fields);

        _tgAuto = AddToggleRow(stack, "Jalan otomatis saat Windows menyala",
            "MuseLink ikut menyala sesudah login (mengikuti Mode Administrator).",
            (s, e) => { if (!_loading) App.Host?.ApplyAutoStart(true); },
            (s, e) => { if (!_loading) App.Host?.ApplyAutoStart(false); });
        _tgAwake = AddToggleRow(stack, "Cegah PC tidur",
            "PC dicegah tidur selama MuseLink berjalan agar bridge selalu bisa dijangkau.",
            (s, e) => { if (!_loading) App.Host?.ApplyKeepAwake(true); },
            (s, e) => { if (!_loading) App.Host?.ApplyKeepAwake(false); });
        _tgAdmin = AddToggleRow(stack, "Mode Administrator",
            "Dibutuhkan untuk mengendalikan aplikasi yang berjalan sebagai admin.",
            (s, e) => { if (!_loading) App.Host?.ApplyAdminMode(true); },
            (s, e) => { if (!_loading) App.Host?.ApplyAdminMode(false); });
        _tgNotif = AddToggleRow(stack, "Notifikasi aktivitas",
            "Tampilkan pemberitahuan saat tugas besar selesai.", null, null);

        stack.Children.Add(new Border
        {
            Height = 1,
            Background = UiKit.Res("CrBorder"),
            Margin = new Thickness(0, 22, 0, 0),
        });

        stack.Children.Add(new TextBlock
        {
            Text = "APPEARANCE",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Res("CrMuted"),
            Margin = new Thickness(0, 18, 0, 0),
        });
        stack.Children.Add(ThemeCards());

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
        btns.Children.Add(UiKit.PrimaryBtn("Simpan", null));
        var resetBtn = UiKit.Btn("Reset", (s, e) => RefreshGeneral());
        resetBtn.Margin = new Thickness(10, 0, 0, 0);
        btns.Children.Add(resetBtn);
        stack.Children.Add(btns);

        _genFooter = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = UiKit.Res("CrFaint"),
            Margin = new Thickness(0, 20, 0, 0),
        };
        stack.Children.Add(_genFooter);

        return PanelShell(stack);
    }

    private CheckBox AddToggleRow(StackPanel parent, string title, string sub,
        RoutedEventHandler onChecked, RoutedEventHandler onUnchecked)
    {
        var grid = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var st = UiKit.Muted(sub);
        st.FontSize = 11;
        st.Margin = new Thickness(0, 3, 0, 0);
        texts.Children.Add(st);
        grid.Children.Add(texts);
        var toggle = UiKit.Toggle(false);
        if (onChecked != null) toggle.Checked += onChecked;
        if (onUnchecked != null) toggle.Unchecked += onUnchecked;
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        parent.Children.Add(grid);
        return toggle;
    }

    private StackPanel ThemeCards()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var themes = new[]
        {
            ("Futuristic Dark", "#0B0E13", true),
            ("Control Room", "#131316", false),
            ("Midnight Violet", "#17122B", false),
        };
        foreach (var (name, hex, selected) in themes)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new Border
            {
                Width = 174,
                Height = 64,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = UiKit.Res(selected ? "CrAccent" : "CrBorder"),
                BorderThickness = new Thickness(selected ? 2 : 1),
            });
            item.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 11.5,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = UiKit.Res(selected ? "CrText" : "CrMuted"),
                Margin = new Thickness(2, 8, 0, 0),
            });
            row.Children.Add(item);
        }
        return row;
    }

    private void RefreshGeneral()
    {
        if (_tgAuto == null) return;
        _loading = true;
        try
        {
            _tgAuto.IsChecked = App.Host?.AutoStartEnabled ?? false;
            _tgAwake.IsChecked = Settings.KeepAwake;
            _tgAdmin.IsChecked = Settings.AdminMode;
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            _genFooter.Text = "MuseLink v" + (v != null ? v.ToString(3) : "5.3.0")
                + " • UI WPF (Control Room) • Bridge port " + AgentServer.Port;
        }
        finally { _loading = false; }
    }

    // ---------- Desktop Bridge (isi lama Connections + Security) ----------

    private UIElement BuildBridge()
    {
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(UiKit.SectionLabel("DESKTOP BRIDGE"));

        _brStatus = UiKit.Value("…");
        var statusStack = new StackPanel();
        statusStack.Children.Add(_brStatus);
        statusStack.Children.Add(UiKit.Muted("Listener HTTP di port " + AgentServer.Port
            + " • Tailscale • 100.126.240.47 — keamanan bertumpu pada jaringan Tailscale."));
        stack.Children.Add(CardWrap(statusStack, 14));

        _brToggleBtn = UiKit.Btn("Putuskan Akses", (s, e) =>
        {
            App.Host?.ToggleServer();
            _ = RefreshBridgeAsync();
        });
        _brToggleBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _brToggleBtn.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(_brToggleBtn);

        _brMode = UiKit.Value("…");
        _brLease = UiKit.Muted("");
        var modeStack = new StackPanel();
        modeStack.Children.Add(_brMode);
        modeStack.Children.Add(_brLease);
        stack.Children.Add(CardWrap(modeStack, 18));

        _brStop = UiKit.Body("…");
        _brStopBtn = UiKit.Btn("Aktifkan Emergency Stop", (s, e) =>
        {
            App.Host?.Server.SetHalt(!_brHalted, "ui");
            _ = RefreshBridgeAsync();
        });
        _brStopBtn.HorizontalAlignment = HorizontalAlignment.Left;
        _brStopBtn.Margin = new Thickness(0, 10, 0, 0);
        var stopStack = new StackPanel();
        stopStack.Children.Add(_brStop);
        stopStack.Children.Add(_brStopBtn);
        stack.Children.Add(CardWrap(stopStack, 18));

        var note = UiKit.Body("Persetujuan tindakan (kartu Allow) diberikan lewat runtime Muse "
            + "di layar pengguna — aplikasi ini tidak bisa menyetujui atau melewatinya. Yang bisa "
            + "dilakukan di sini: menghentikan semua input dengan Emergency Stop dan memutus listener bridge.");
        note.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(CardWrap(note, 18));

        return PanelShell(stack);
    }

    private static Border CardWrap(UIElement content, double topMargin)
    {
        var b = new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = content,
            Margin = new Thickness(0, topMargin, 0, 0),
        };
        return b;
    }

    private async Task RefreshBridgeAsync()
    {
        try
        {
            var server = App.Host?.Server;
            if (server == null || _brStatus == null) return;
            var health = await Task.Run(() => server.BuildHealth());
            bool running = server.IsRunning;
            _brStatus.Text = running ? "Bridge: Mendengarkan" : "Bridge: Berhenti";
            _brToggleBtn.Content = running ? "Putuskan Akses" : "Sambungkan Akses";
            _brMode.Text = Settings.IsElevated() ? "Mode kendali: Administrator" : "Mode kendali: Pengguna biasa";
            string lease = UiKit.GetStr(health, "inputLease");
            _brLease.Text = "Lease input: " + (lease.Length > 0 ? lease : "Bebas")
                + " — satu tugas memegang kendali input pada satu waktu.";
            _brHalted = UiKit.GetBool(health, "inputHalted");
            _brStop.Text = _brHalted
                ? "Emergency Stop AKTIF — semua input dari bridge ditolak (423) sampai stop dilepas dari aplikasi ini."
                : "Emergency Stop tidak aktif — bridge boleh mengirim input.";
            _brStopBtn.Content = _brHalted ? "Lepas Emergency Stop" : "Aktifkan Emergency Stop";
        }
        catch { /* pelengkap */ }
    }

    // ---------- Appearance / placeholder / About ----------

    private UIElement BuildAppearance()
    {
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(UiKit.SectionLabel("APPEARANCE"));
        stack.Children.Add(ThemeCards());
        var note = UiKit.Muted("Tema aktif: Futuristic Dark (dari desain Figma). "
            + "Mengganti tema dari sini masih menyusul — pilihan lain tampil sebagai contoh desain.");
        note.Margin = new Thickness(0, 16, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(note);
        return PanelShell(stack);
    }

    private UIElement BuildPlaceholder(string cat)
    {
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(UiKit.SectionLabel(cat.ToUpperInvariant()));
        var note = UiKit.Muted("Panel " + cat + " tampil sesuai daftar kategori Figma; "
            + "isinya menyusul pada ronde berikutnya.");
        note.Margin = new Thickness(0, 12, 0, 0);
        note.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(note);
        return PanelShell(stack);
    }

    private UIElement BuildAbout()
    {
        var stack = new StackPanel { Margin = new Thickness(24) };
        stack.Children.Add(UiKit.SectionLabel("ABOUT"));
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var info = UiKit.Body(
            "MuseLink v" + (v != null ? v.ToString(3) : "5.3.0") + " • UI WPF (Control Room)\n"
            + "Proses: " + (Settings.IsElevated() ? "Administrator" : "Pengguna biasa")
            + " • Bridge port " + AgentServer.Port + "\n"
            + "Tata letak v5.3.0 mengikuti desain Figma “MuseLink — Futuristic AI Control Center”.\n"
            + "Pengaturan tersimpan di registry HKCU\\Software\\Jembatan.");
        info.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(info);
        return PanelShell(stack);
    }
}
