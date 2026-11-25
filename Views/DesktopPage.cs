using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Desktop Control: daftar jendela live dari engine (NativeInput.
/// ListWindows), Fokus/Tutup lewat WindowAction, dan pratinjau
/// layar berkala (CaptureScreenScaled). Tata letak TableLayout
/// murni — pelajaran dari cacat v4.1.0 WinForms.
/// </summary>
public sealed class DesktopPage : UserControl, IRoomPage
{
    private readonly ListBox _list;
    private readonly Image _preview;
    private readonly TextBlock _note;
    private List<NativeInput.WindowInfo> _windows = new List<NativeInput.WindowInfo>();
    private readonly DispatcherTimer _timer;

    public DesktopPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(UiKit.Btn("Segarkan", (s, e) => RefreshAll()));
        var focusBtn = UiKit.Btn("Fokus", Focus_Click);
        focusBtn.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(focusBtn);
        var closeBtn = UiKit.Btn("Tutup", Close_Click);
        closeBtn.Margin = new Thickness(8, 0, 0, 0);
        bar.Children.Add(closeBtn);
        _note = UiKit.Muted("");
        _note.Margin = new Thickness(12, 6, 0, 0);
        bar.Children.Add(_note);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(470) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _list = UiKit.List();
        _list.FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
        Grid.SetColumn(_list, 0);
        body.Children.Add(_list);

        _preview = new Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
        };
        // Pratinjau dibingkai kartu (v5.1).
        var previewFrame = new Border
        {
            Background = UiKit.Res("CrCard"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Margin = new Thickness(14, 0, 0, 0),
            Child = _preview,
        };
        Grid.SetColumn(previewFrame, 1);
        body.Children.Add(previewFrame);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (s, e) => RefreshPreview();
    }

    public void OnShown() { RefreshAll(); _timer.Start(); }
    public void OnHidden() { _timer.Stop(); }

    private void RefreshAll()
    {
        try
        {
            _windows = NativeInput.ListWindows();
            _list.Items.Clear();
            foreach (var w in _windows)
            {
                string line = w.Title + "  —  " + w.Process + " (" + w.Pid + ")  •  " + w.State;
                _list.Items.Add(new ListBoxItem { Content = line, Tag = w });
            }
            _note.Text = _windows.Count + " jendela • pratinjau diperbarui otomatis saat halaman terbuka.";
        }
        catch (Exception ex)
        {
            _note.Text = "Gagal membaca jendela: " + ex.Message;
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            using (var bmp = NativeInput.CaptureScreenScaled(880))
            {
                if (bmp != null) _preview.Source = UiKit.ToBitmapSource(bmp);
            }
        }
        catch { /* pratinjau pelengkap */ }
    }

    private NativeInput.WindowInfo Selected()
    {
        return (_list.SelectedItem as ListBoxItem)?.Tag as NativeInput.WindowInfo;
    }

    private void Focus_Click(object sender, RoutedEventArgs e)
    {
        var w = Selected();
        if (w != null) NativeInput.FocusWindow(w.Handle);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        var w = Selected();
        if (w != null)
        {
            NativeInput.WindowAction(w.Handle, "close");
            Dispatcher.BeginInvoke(new Action(RefreshAll), DispatcherPriority.Background);
        }
    }
}
