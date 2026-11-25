using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Jembatan;

/// <summary>
/// Command Center: runner PowerShell internal aplikasi (padanan
/// /exec lewat UI) — keluaran ditangkap penuh, batas 60 detik,
/// riwayat sesi, dan gerbang izin: nonaktif saat Emergency Stop
/// aktif atau bridge berhenti.
/// </summary>
public sealed class CommandPage : UserControl, IRoomPage
{
    private readonly TextBox _input;
    private readonly TextBox _output;
    private readonly ListBox _history;
    private readonly Button _runBtn;
    private CancellationTokenSource _cts;

    public CommandPage()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _input = UiKit.Input();
        _input.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _input.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) _ = RunAsync();
        };
        Grid.SetColumn(_input, 0);
        inputRow.Children.Add(_input);
        _runBtn = UiKit.Btn("Jalankan", (s, e) => _ = RunAsync());
        _runBtn.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(_runBtn, 1);
        inputRow.Children.Add(_runBtn);
        Grid.SetRow(inputRow, 0);
        left.Children.Add(inputRow);

        _output = new TextBox
        {
            Style = (Style)Application.Current.FindResource("CrTextBox"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12.5,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(_output, 1);
        left.Children.Add(_output);
        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        _history = UiKit.List();
        _history.SelectionChanged += (s, e) =>
        {
            if (_history.SelectedItem is string cmd) _input.Text = cmd;
        };
        var histCard = UiKit.Card("RIWAYAT SESI", _history);
        histCard.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetColumn(histCard, 1);
        root.Children.Add(histCard);

        Content = root;
    }

    public void OnShown() { RefreshGate(); }
    public void OnHidden() { }

    private void RefreshGate()
    {
        var server = App.Host?.Server;
        bool blocked = server == null || !server.IsRunning || server.InputHalted;
        _runBtn.IsEnabled = !blocked;
        _input.IsEnabled = !blocked;
        if (server != null && server.InputHalted)
            _output.Text = "Emergency Stop aktif — perintah ditahan sampai stop dilepas.";
    }

    private async Task RunAsync()
    {
        string cmd = _input.Text.Trim();
        if (cmd.Length == 0) return;
        RefreshGate();
        if (!_runBtn.IsEnabled) return;

        _history.Items.Insert(0, cmd);
        _runBtn.IsEnabled = false;
        _output.Text = "> " + cmd + "\n";
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var sb = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + cmd.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var proc = new Process { StartInfo = psi })
            {
                proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await Task.Run(() => proc.WaitForExit(60000), _cts.Token);
                if (!proc.HasExited)
                {
                    try { proc.Kill(); } catch { }
                    sb.AppendLine("[dihentikan — melewati batas 60 detik]");
                }
                else
                {
                    sb.AppendLine("[selesai — kode keluar " + proc.ExitCode + "]");
                }
            }
            _output.Text = "> " + cmd + "\n" + sb;
        }
        catch (Exception ex)
        {
            _output.Text = "> " + cmd + "\n[gagal: " + ex.Message + "]";
        }
        finally
        {
            RefreshGate();
        }
    }
}
