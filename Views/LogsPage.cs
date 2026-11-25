using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// System Logs: tiga sumber asli — Event (EventLog engine),
/// Operasi (RecentOpsSnapshot server), Log aplikasi (buffer
/// cincin AppHost) — dengan ikuti-otomatis dan ekspor teks.
/// </summary>
public sealed class LogsPage : UserControl, IRoomPage
{
    private readonly ComboBox _source;
    private readonly ListBox _list;
    private readonly CheckBox _follow;
    private readonly DispatcherTimer _timer;

    public LogsPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _source = new ComboBox { Width = 170, FontSize = 13 };
        _source.Items.Add("Event");
        _source.Items.Add("Operasi");
        _source.Items.Add("Log aplikasi");
        _source.SelectedIndex = 0;
        _source.SelectionChanged += (s, e) => RefreshList();
        bar.Children.Add(_source);
        bar.Children.Add(UiKit.Btn("Segarkan", (s, e) => RefreshList()));
        _follow = new CheckBox
        {
            Content = "Ikuti otomatis",
            IsChecked = true,
            Foreground = UiKit.Res("CrText"),
            Margin = new Thickness(16, 4, 0, 0),
        };
        bar.Children.Add(_follow);
        var exportBtn = UiKit.Btn("Ekspor", Export_Click);
        exportBtn.Margin = new Thickness(16, 0, 0, 0);
        bar.Children.Add(exportBtn);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        _list = UiKit.List();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (s, e) => { if (_follow.IsChecked == true) RefreshList(); };
    }

    public void OnShown() { RefreshList(); _timer.Start(); }
    public void OnHidden() { _timer.Stop(); }

    private string CurrentSource => _source.SelectedItem as string ?? "Event";

    private void RefreshList()
    {
        try
        {
            _list.Items.Clear();
            switch (CurrentSource)
            {
                case "Operasi":
                    var ops = App.Host?.Server.RecentOpsSnapshot(120);
                    if (ops != null)
                        foreach (var row in ops) _list.Items.Add(string.Join("  ", row));
                    break;
                case "Log aplikasi":
                    var lines = App.Host?.RecentLogLines(200);
                    if (lines != null)
                        foreach (var line in lines) _list.Items.Add(line.Replace("\t", "  "));
                    break;
                default:
                    foreach (var en in EventLog.Since(0, 200))
                        _list.Items.Add(en.Time + "  [" + en.Kind + "]  " + en.Detail);
                    break;
            }
            if (_list.Items.Count == 0) _list.Items.Add("(kosong)");
            if (_follow.IsChecked == true && _list.Items.Count > 0)
                _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
        }
        catch { }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "muselink-log-" + CurrentSource.Replace(" ", "-").ToLowerInvariant()
                + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt",
            Filter = "Teks|*.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            foreach (var item in _list.Items) sb.AppendLine(item?.ToString());
            File.WriteAllText(dlg.FileName, sb.ToString());
            App.Host?.Log("System Logs diekspor: " + dlg.FileName);
        }
        catch { }
    }
}
