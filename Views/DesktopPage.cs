using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Desktop Control v5.3.0 — komposisi persis frame Figma: kartu
/// Mouse Control + Application Control (kiri), Keyboard Control +
/// Desktop Information (kanan), Active Window membentang di
/// bawah. Data asli dari NativeInput (posisi kursor, daftar
/// jendela, jendela depan); tombol Fokus/Minimize/Tutup pada
/// Active Window dan Fokus/Tutup per aplikasi SUNGGUH bekerja.
/// Tiga tombol klik mouse masih contoh desain (klik pada posisi
/// kursor sendiri tidak bermakna dari dalam aplikasi — fitur
/// menyusul), kecepatan mouse dibaca dari Windows.
/// </summary>
public sealed class DesktopPage : UserControl, IRoomPage
{
    private readonly TextBlock _cursorVal, _speedPct, _lastInput;
    private readonly Grid _speedBar;
    private readonly StackPanel _appRows, _infoRows;
    private readonly TextBlock _awTitle, _awSub;
    private IntPtr _awHandle = IntPtr.Zero;
    private readonly DispatcherTimer _timer;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);
    private const uint SPI_GETMOUSESPEED = 0x0070;

    public DesktopPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(cols);

        // ---- Kolom kiri ----
        var left = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        Grid.SetColumn(left, 0);
        cols.Children.Add(left);

        var mouseStack = new StackPanel();
        mouseStack.Children.Add(UiKit.Muted("Posisi kursor"));
        _cursorVal = new TextBlock
        {
            Text = "X — • Y —",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 6, 0, 0),
        };
        mouseStack.Children.Add(_cursorVal);
        mouseStack.Children.Add(new TextBlock
        {
            Text = "Kecepatan gerak",
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
            Margin = new Thickness(0, 16, 0, 0),
        });
        var speedRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _speedBar = UiKit.Bar("CrAccent", 6);
        speedRow.Children.Add(_speedBar);
        _speedPct = new TextBlock
        {
            Text = "—",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Res("CrAccent"),
            Margin = new Thickness(10, -3, 0, 0),
        };
        Grid.SetColumn(_speedPct, 1);
        speedRow.Children.Add(_speedPct);
        mouseStack.Children.Add(speedRow);
        var clickRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < 3; i++)
            clickRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        string[] clickLabels = { "Klik Kiri", "Klik Kanan", "Klik Ganda" };
        for (int i = 0; i < 3; i++)
        {
            var b = UiKit.Btn(clickLabels[i], null);
            b.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            Grid.SetColumn(b, i);
            clickRow.Children.Add(b);
        }
        mouseStack.Children.Add(clickRow);
        var mouseNote = UiKit.Muted("Presisi: target dicari via UIA / OCR sebelum klik.");
        mouseNote.FontSize = 11;
        mouseNote.Foreground = UiKit.Res("CrFaint");
        mouseNote.Margin = new Thickness(0, 14, 0, 0);
        mouseStack.Children.Add(mouseNote);
        left.Children.Add(UiKit.Card("MOUSE CONTROL", mouseStack));

        _appRows = new StackPanel();
        var appCard = UiKit.Card("APPLICATION CONTROL", _appRows);
        appCard.Margin = new Thickness(0, 16, 0, 0);
        left.Children.Add(appCard);

        // ---- Kolom kanan ----
        var right = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(right, 1);
        cols.Children.Add(right);

        var kbStack = new StackPanel();
        kbStack.Children.Add(UiKit.Muted("Input terakhir"));
        _lastInput = new TextBlock
        {
            Text = "—",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        kbStack.Children.Add(_lastInput);
        kbStack.Children.Add(new TextBlock
        {
            Text = "Pintasan cepat",
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
            Margin = new Thickness(0, 16, 0, 0),
        });
        foreach (var s in new[] { "Ctrl + V — Tempel", "Ctrl + A — Pilih Semua", "Enter — Konfirmasi", "Esc — Batal" })
        {
            kbStack.Children.Add(new TextBlock
            {
                Text = "▸ " + s,
                FontSize = 12,
                Foreground = UiKit.Res("CrMuted"),
                Margin = new Thickness(0, 5, 0, 0),
            });
        }
        right.Children.Add(UiKit.Card("KEYBOARD CONTROL", kbStack));

        _infoRows = new StackPanel();
        var infoCard = UiKit.Card("DESKTOP INFORMATION", _infoRows);
        infoCard.Margin = new Thickness(0, 16, 0, 0);
        right.Children.Add(infoCard);

        // ---- Active Window (bawah, membentang) ----
        var awStack = new StackPanel();
        _awTitle = new TextBlock
        {
            Text = "—",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        awStack.Children.Add(_awTitle);
        _awSub = UiKit.Muted("");
        _awSub.Margin = new Thickness(0, 5, 0, 0);
        awStack.Children.Add(_awSub);
        var awBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var focusBtn = UiKit.PrimaryBtn("Fokus", (s, e) =>
        {
            if (_awHandle != IntPtr.Zero) NativeInput.FocusWindow(_awHandle);
        });
        awBtns.Children.Add(focusBtn);
        var minBtn = UiKit.Btn("Minimize", (s, e) =>
        {
            if (_awHandle != IntPtr.Zero) NativeInput.WindowAction(_awHandle, "minimize");
        });
        minBtn.Margin = new Thickness(10, 0, 0, 0);
        awBtns.Children.Add(minBtn);
        var closeBtn = UiKit.DangerBtn("Tutup", (s, e) =>
        {
            if (_awHandle != IntPtr.Zero) NativeInput.WindowAction(_awHandle, "close");
        });
        closeBtn.Margin = new Thickness(10, 0, 0, 0);
        awBtns.Children.Add(closeBtn);
        awStack.Children.Add(awBtns);
        var awCard = UiKit.Card("ACTIVE WINDOW", awStack);
        awCard.Margin = new Thickness(0, 16, 0, 0);
        Grid.SetRow(awCard, 1);
        root.Children.Add(awCard);

        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += (s, e) => RefreshAll();
    }

    public void OnShown() { RefreshAll(); _timer.Start(); }
    public void OnHidden() { _timer.Stop(); }

    private void RefreshAll()
    {
        try
        {
            var pos = NativeInput.CursorPosition();
            _cursorVal.Text = "X " + pos.X + " • Y " + pos.Y;

            int speed = 0;
            try
            {
                if (SystemParametersInfo(SPI_GETMOUSESPEED, 0, ref speed, 0) && speed >= 1 && speed <= 20)
                {
                    UiKit.SetBar(_speedBar, speed * 5);
                    _speedPct.Text = speed * 5 + "%";
                }
            }
            catch { /* biarkan terakhir */ }

            var events = EventLog.Since(Math.Max(0, EventLog.LastSeq - 1), 5);
            if (events.Count > 0) _lastInput.Text = "“" + events[events.Count - 1].Detail + "”";

            var windows = NativeInput.ListWindows();
            IntPtr fg = NativeInput.ForegroundHandle();

            // Baris aplikasi (maks 5) + OBS bila tidak berjalan
            _appRows.Children.Clear();
            int shown = 0;
            foreach (var w in windows)
            {
                if (shown >= 5) break;
                _appRows.Children.Add(AppRow(w, w.Handle == fg));
                shown++;
            }
            bool obsRunning = false;
            foreach (var w in windows)
                if (w.Process.IndexOf("obs", StringComparison.OrdinalIgnoreCase) >= 0) obsRunning = true;
            if (!obsRunning)
                _appRows.Children.Add(ObsOffRow());

            // Informasi desktop
            _infoRows.Children.Clear();
            var bounds = NativeInput.PrimaryBounds();
            string monitor = bounds.Width + "×" + bounds.Height;
            try
            {
                foreach (var mon in NativeInput.AllScreens())
                    if (mon.Primary) monitor = mon.DeviceName + " — " + mon.Bounds.Width + "×" + mon.Bounds.Height;
            }
            catch { }
            double dpi = 100;
            try { dpi = Math.Round(System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX * 100); } catch { }
            string fgTitle = fg != IntPtr.Zero ? NativeInput.TitleOf(fg) : "—";
            AddInfo("Monitor", monitor);
            AddInfo("Sesi", "Desktop-1 • DPI " + dpi + "%");
            AddInfo("Jendela aktif", fgTitle);
            AddInfo("Sistem", "Windows • " + (Settings.IsElevated() ? "Administrator" : "Pengguna biasa"));

            // Active Window
            _awHandle = fg;
            _awTitle.Text = fgTitle.Length > 0 ? fgTitle : "—";
            if (fg != IntPtr.Zero)
            {
                int pid = NativeInput.PidOf(fg);
                bool hung = false;
                try { hung = NativeInput.IsHung(fg); } catch { }
                string state = NativeInput.IsWindowMinimized(fg) ? "minimized" : "normal";
                _awSub.Text = "PID " + pid + " • hwnd " + fg.ToInt64() + " • " + state
                    + " • " + (hung ? "TIDAK MERESPONS" : "merespons normal");
            }
            else _awSub.Text = "";
        }
        catch { /* halaman pelengkap — gagal diam */ }
    }

    private Border AppRow(NativeInput.WindowInfo w, bool isForeground)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UiKit.Dot(isForeground ? "CrGreen" : "CrAccent", 8));
        var name = new TextBlock
        {
            Text = FriendlyAppName(w),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var state = new TextBlock
        {
            Text = isForeground ? "Aktif di depan" : w.State,
            FontSize = 11,
            Foreground = UiKit.Res("CrMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);
        var focus = UiKit.Btn("Fokus", (s, e) => NativeInput.FocusWindow(w.Handle));
        focus.Padding = new Thickness(12, 4, 12, 4);
        Grid.SetColumn(focus, 3);
        grid.Children.Add(focus);
        var close = UiKit.DangerBtn("Tutup", (s, e) =>
        {
            NativeInput.WindowAction(w.Handle, "close");
            Dispatcher.BeginInvoke(new Action(RefreshAll),
                System.Windows.Threading.DispatcherPriority.Background);
        });
        close.Padding = new Thickness(12, 4, 12, 4);
        close.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(close, 4);
        grid.Children.Add(close);
        return new Border { Child = grid, Padding = new Thickness(0, 6, 0, 6) };
    }

    private Border ObsOffRow()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(UiKit.Dot("CrFaint", 8));
        var name = new TextBlock
        {
            Text = "OBS Studio",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(10, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var state = new TextBlock
        {
            Text = "Tidak berjalan",
            FontSize = 11,
            Foreground = UiKit.Res("CrFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 2);
        grid.Children.Add(state);
        return new Border { Child = grid, Padding = new Thickness(0, 6, 0, 6) };
    }

    private static string FriendlyAppName(NativeInput.WindowInfo w)
    {
        string t = w.Title ?? "";
        int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && t.Length - dash < 30) return t.Substring(dash + 3);
        if (!string.IsNullOrEmpty(w.Process)) return w.Process;
        return t;
    }

    private void AddInfo(string label, string value)
    {
        var stack = new StackPanel { Margin = new Thickness(0, _infoRows.Children.Count == 0 ? 0 : 12, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiKit.Res("CrFaint"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        _infoRows.Children.Add(stack);
    }
}
