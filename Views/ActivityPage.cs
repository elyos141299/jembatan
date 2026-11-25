using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Activity Monitor (brief: event streaming, timeline, filtering,
/// searching, detail, korelasi, export). Sumber: EventLog engine
/// (kejadian proses/jendela/fokus) — disegarkan sebagai aliran,
/// bukan potret sekali.
/// </summary>
public sealed class ActivityPage : UserControl, IRoomPage
{
    private readonly ListBox _list;
    private readonly TextBox _filter;
    private readonly CheckBox _follow;
    private readonly TextBlock _detail;
    private readonly List<EventLog.Entry> _entries = new List<EventLog.Entry>();
    private readonly DispatcherTimer _timer;
    private long _lastSeq;

    public ActivityPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(92) });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        _filter = UiKit.Input();
        _filter.Width = 300;
        _filter.TextChanged += (s, e) => Rebuild();
        bar.Children.Add(_filter);
        var hint = UiKit.Muted("saring teks / jenis kejadian");
        hint.Margin = new Thickness(8, 6, 0, 0);
        bar.Children.Add(hint);
        _follow = new CheckBox
        {
            Content = "Ikuti otomatis",
            IsChecked = true,
            Foreground = UiKit.Res("CrText"),
            Margin = new Thickness(18, 4, 0, 0),
        };
        bar.Children.Add(_follow);
        var exportBtn = UiKit.Btn("Ekspor", Export_Click);
        exportBtn.Margin = new Thickness(18, 0, 0, 0);
        bar.Children.Add(exportBtn);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);

        _list = UiKit.List();
        _list.SelectionChanged += (s, e) => ShowDetail();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        _detail = UiKit.Body("Pilih kejadian untuk melihat rincian (seq, waktu, jenis, hwnd, pid).");
        var detailCard = UiKit.Card("RINCIAN KEJADIAN", _detail);
        detailCard.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(detailCard, 2);
        root.Children.Add(detailCard);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (s, e) => Pull();
    }

    public void OnShown()
    {
        _entries.Clear();
        _lastSeq = 0;
        Pull();
        _timer.Start();
    }

    public void OnHidden() { _timer.Stop(); }

    private void Pull()
    {
        var fresh = EventLog.Since(_lastSeq, 200);
        if (fresh.Count == 0) return;
        foreach (var en in fresh)
        {
            _entries.Add(en);
            if (en.Seq > _lastSeq) _lastSeq = en.Seq;
        }
        while (_entries.Count > 400) _entries.RemoveAt(0);
        Rebuild();
        if (_follow.IsChecked == true && _list.Items.Count > 0)
            _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
    }

    private void Rebuild()
    {
        string f = _filter.Text.Trim();
        _list.Items.Clear();
        foreach (var en in _entries)
        {
            string line = en.Time + "  [" + en.Kind + "]  " + en.Detail;
            if (f.Length > 0 && line.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _list.Items.Add(new ListBoxItem { Content = line, Tag = en });
        }
    }

    private void ShowDetail()
    {
        if (_list.SelectedItem is ListBoxItem item && item.Tag is EventLog.Entry en)
        {
            _detail.Text = "seq " + en.Seq + " • " + en.Time + " • jenis " + en.Kind
                + " • hwnd " + en.Hwnd + " • pid " + en.Pid + "\n" + en.Detail;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "muselink-activity-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".txt",
            Filter = "Teks|*.txt|JSON|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                sb.AppendLine("[");
                for (int i = 0; i < _entries.Count; i++)
                {
                    var en = _entries[i];
                    sb.Append("  {\"seq\":").Append(en.Seq)
                      .Append(",\"time\":\"").Append(en.Time)
                      .Append("\",\"kind\":\"").Append(en.Kind.Replace("\"", "'"))
                      .Append("\",\"detail\":\"").Append(en.Detail.Replace("\\", "\\\\").Replace("\"", "'"))
                      .Append("\",\"hwnd\":").Append(en.Hwnd)
                      .Append(",\"pid\":").Append(en.Pid).Append("}");
                    if (i < _entries.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                sb.AppendLine("]");
                File.WriteAllText(dlg.FileName, sb.ToString());
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var en in _entries)
                    sb.AppendLine(en.Time + "\t[" + en.Kind + "]\t" + en.Detail);
                File.WriteAllText(dlg.FileName, sb.ToString());
            }
            App.Host?.Log("Activity Monitor diekspor: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            _detail.Text = "Ekspor gagal: " + ex.Message;
        }
    }
}
