using System.Windows;
using System.Windows.Controls;

namespace Jembatan;

/// <summary>
/// Settings: saklar-saklar yang dulu tinggal di SettingsForm
/// WinForms — Mode Administrator, jalan otomatis, jangan tidur —
/// semuanya diteruskan ke AppHost/Settings (penyimpanan registry
/// yang sama, HKCU\Software\Jembatan).
/// </summary>
public sealed class SettingsPage : UserControl, IRoomPage
{
    private readonly CheckBox _adminBox, _autoBox, _awakeBox;
    private readonly TextBlock _info;
    private bool _loading = true;

    public SettingsPage()
    {
        var root = new StackPanel();

        _adminBox = MakeToggle("Mode Administrator",
            "Aplikasi berjalan dengan hak administrator (tugas terjadwal berhak tertinggi; perubahan bisa memicu buka ulang lewat UAC).");
        _adminBox.Checked += (s, e) => { if (!_loading) App.Host?.ApplyAdminMode(true); };
        _adminBox.Unchecked += (s, e) => { if (!_loading) App.Host?.ApplyAdminMode(false); };
        root.Children.Add(CardOf(_adminBox));

        _autoBox = MakeToggle("Jalan otomatis saat Windows menyala",
            "MuseLink ikut menyala sesudah login Windows (nilai Run atau tugas terjadwal, mengikuti Mode Administrator).");
        _autoBox.Checked += (s, e) => { if (!_loading) App.Host?.ApplyAutoStart(true); };
        _autoBox.Unchecked += (s, e) => { if (!_loading) App.Host?.ApplyAutoStart(false); };
        var autoCard = CardOf(_autoBox);
        autoCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(autoCard);

        _awakeBox = MakeToggle("Jangan tidur",
            "PC dicegah tidur selama MuseLink berjalan, agar bridge selalu bisa dijangkau.");
        _awakeBox.Checked += (s, e) => { if (!_loading) App.Host?.ApplyKeepAwake(true); };
        _awakeBox.Unchecked += (s, e) => { if (!_loading) App.Host?.ApplyKeepAwake(false); };
        var awakeCard = CardOf(_awakeBox);
        awakeCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(awakeCard);

        _info = UiKit.Body("");
        var infoCard = UiKit.Card("INFORMASI", _info);
        infoCard.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(infoCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static CheckBox MakeToggle(string title, string sub)
    {
        var box = new CheckBox
        {
            Foreground = UiKit.Res("CrText"),
            FontSize = 13.5,
            Style = (Style)Application.Current.FindResource("CrCheckBox"),
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        var subText = UiKit.Muted(sub);
        subText.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(subText);
        box.Content = stack;
        return box;
    }

    private static Border CardOf(UIElement content)
    {
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = content,
        };
    }

    public void OnShown() { RefreshState(); }
    public void OnHidden() { }

    private void RefreshState()
    {
        _loading = true;
        try
        {
            _adminBox.IsChecked = Settings.AdminMode;
            _autoBox.IsChecked = App.Host?.AutoStartEnabled ?? false;
            _awakeBox.IsChecked = Settings.KeepAwake;
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            _info.Text = "MuseLink v" + (v != null ? v.ToString(3) : "5.0.0")
                + " • UI WPF (Control Room)\nProses: " + (Settings.IsElevated() ? "Administrator" : "Pengguna biasa")
                + " • Bridge port " + AgentServer.Port
                + "\nPengaturan tersimpan di registry HKCU\\Software\\Jembatan.";
        }
        finally { _loading = false; }
    }
}
