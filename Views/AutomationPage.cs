using System;
using System.Windows;
using System.Windows.Controls;

namespace Jembatan;

/// <summary>
/// Automation: rutinitas tersimpan di PC (dibuat &amp; dijalankan
/// agen lewat bridge /routines). Halaman ini mengelolanya:
/// daftar, lihat isi aksi (JSON), hapus.
/// </summary>
public sealed class AutomationPage : UserControl, IRoomPage
{
    private readonly ListBox _list;
    private readonly TextBox _json;
    private readonly TextBlock _note;

    public AutomationPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = 380 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list = UiKit.List();
        _list.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        _list.SelectionChanged += (s, e) => ShowJson();
        Grid.SetColumn(_list, 0);
        body.Children.Add(_list);

        _json = new TextBox
        {
            Style = (Style)Application.Current.FindResource("CrTextBox"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(14, 0, 0, 0),
        };
        Grid.SetColumn(_json, 1);
        body.Children.Add(_json);
        Grid.SetRow(body, 0);
        root.Children.Add(body);

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(UiKit.Btn("Segarkan", (s, e) => RefreshList()));
        var delBtn = UiKit.Btn("Hapus Rutinitas", Delete_Click);
        delBtn.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(delBtn);
        _note = UiKit.Muted("Rutinitas dibuat & dijalankan oleh agen lewat bridge; halaman ini untuk memeriksa dan menghapusnya.");
        _note.Margin = new Thickness(12, 6, 0, 0);
        bar.Children.Add(_note);
        Grid.SetRow(bar, 1);
        root.Children.Add(bar);

        Content = root;
    }

    public void OnShown() { RefreshList(); }
    public void OnHidden() { }

    private void RefreshList()
    {
        _list.Items.Clear();
        try
        {
            foreach (var kv in Routines.List())
                _list.Items.Add(new ListBoxItem { Content = kv.Key + "  —  " + kv.Value + " langkah", Tag = kv.Key });
            if (_list.Items.Count == 0)
                _note.Text = "Belum ada rutinitas tersimpan di PC ini.";
        }
        catch (Exception ex)
        {
            _note.Text = "Gagal membaca rutinitas: " + ex.Message;
        }
    }

    private void ShowJson()
    {
        if (_list.SelectedItem is ListBoxItem item && item.Tag is string name
            && Routines.TryGet(name, out string json))
        {
            _json.Text = json;
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_list.SelectedItem is ListBoxItem item && item.Tag is string name)
        {
            if (Routines.Delete(name))
            {
                App.Host?.Log("Rutinitas dihapus dari halaman Automation: " + name);
                _json.Text = "";
                RefreshList();
            }
        }
    }
}
