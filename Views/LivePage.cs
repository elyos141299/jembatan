using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jembatan;

/// <summary>
/// Live Screen v5.3.0 (halaman BARU, persis frame Figma): pemilih
/// display + Refresh + lencana LIVE PREVIEW, area pratinjau besar
/// berisi tangkapan desktop ASLI (diperbarui tiap 2 detik saat
/// halaman terbuka), strip info di kaki pratinjau, 5 chip info,
/// dan kartu Capture Information dengan tombol Ambil Screenshot
/// yang menyimpan PNG ke folder Pictures. Refresh rate dibaca
/// dari DEVMODE Windows (EnumDisplaySettings).
/// </summary>
public sealed class LivePage : UserControl, IRoomPage
{
    private readonly Image _preview;
    private readonly TextBlock _resStrip, _hzStrip, _capInfo;
    private readonly TextBlock[] _chipVals = new TextBlock[5];
    private readonly DispatcherTimer _timer;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;

    public LivePage()
    {
        var root = new StackPanel();

        // ---- Baris atas ----
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var bounds = NativeInput.PrimaryBounds();
        var selector = new Border
        {
            Background = UiKit.Res("CrElevated"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(14, 9, 14, 9),
        };
        var selGrid = new Grid();
        selGrid.Children.Add(new TextBlock
        {
            Text = "Display 1 — " + bounds.Width + "×" + bounds.Height,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
        });
        selGrid.Children.Add(new TextBlock
        {
            Text = "▾",
            FontSize = 12,
            Foreground = UiKit.Res("CrMuted"),
            HorizontalAlignment = HorizontalAlignment.Right,
        });
        selector.Child = selGrid;
        top.Children.Add(selector);
        var refreshBtn = UiKit.Btn("↻ Refresh", (s, e) => CaptureNow());
        refreshBtn.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(refreshBtn, 1);
        top.Children.Add(refreshBtn);
        var liveBadge = UiKit.Badge("LIVE PREVIEW", "CrRed");
        liveBadge.Margin = new Thickness(12, 0, 0, 0);
        liveBadge.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(liveBadge, 2);
        top.Children.Add(liveBadge);
        var capText = new TextBlock
        {
            Text = "GDI Capture • pratinjau per 2 dtk",
            FontSize = 11.5,
            Foreground = UiKit.Res("CrMuted"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(capText, 3);
        top.Children.Add(capText);
        root.Children.Add(top);

        // ---- Area pratinjau ----
        _preview = new Image { Stretch = Stretch.Uniform };
        var previewGrid = new Grid();
        previewGrid.Children.Add(_preview);
        var strip = new Border
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(14, 6, 14, 6),
        };
        strip.Background = new SolidColorBrush(Color.FromArgb(0x99,
            UiKit.ResColor("CrBgColor").R, UiKit.ResColor("CrBgColor").G, UiKit.ResColor("CrBgColor").B));
        var stripGrid = new Grid();
        _resStrip = new TextBlock { Text = bounds.Width + " × " + bounds.Height, FontSize = 10.5, Foreground = UiKit.Res("CrMuted") };
        stripGrid.Children.Add(_resStrip);
        _hzStrip = new TextBlock
        {
            Text = RefreshRateText(),
            FontSize = 10.5,
            Foreground = UiKit.Res("CrMuted"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        stripGrid.Children.Add(_hzStrip);
        strip.Child = stripGrid;
        previewGrid.Children.Add(strip);
        var previewBorder = new Border
        {
            Background = UiKit.Res("CrBg"),
            BorderBrush = UiKit.Res("CrBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = previewGrid,
            Margin = new Thickness(0, 14, 0, 0),
            Height = 430,
        };
        root.Children.Add(previewBorder);

        // ---- Chip info ----
        var chips = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        string[] chipLabels = { "RESOLUTION", "REFRESH RATE", "CAPTURE", "LATENCY", "ACTIVE WINDOW" };
        for (int i = 0; i < 5; i++)
        {
            chips.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var st = new StackPanel();
            st.Children.Add(UiKit.SectionLabel(chipLabels[i]));
            _chipVals[i] = new TextBlock
            {
                Text = "—",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            st.Children.Add(_chipVals[i]);
            var chip = new Border
            {
                Background = UiKit.Res("CrPanel"),
                BorderBrush = UiKit.Res("CrBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Child = st,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, i == 4 ? 0 : 6, 0),
            };
            Grid.SetColumn(chip, i);
            chips.Children.Add(chip);
        }
        _chipVals[0].Text = bounds.Width + " × " + bounds.Height;
        _chipVals[1].Text = RefreshRateText();
        _chipVals[2].Text = "GDI Desktop Capture";
        root.Children.Add(chips);

        // ---- Capture Information ----
        var capStack = new StackPanel();
        _capInfo = UiKit.Body("Pratinjau diperbarui atas pola hemat: tangkapan diambil berkala "
            + "saat halaman ini terbuka, bukan streaming terus-menerus.");
        _capInfo.TextWrapping = TextWrapping.Wrap;
        capStack.Children.Add(_capInfo);
        var shotBtn = UiKit.PrimaryBtn("Ambil Screenshot", (s, e) => SaveScreenshot());
        shotBtn.Margin = new Thickness(0, 12, 0, 0);
        shotBtn.HorizontalAlignment = HorizontalAlignment.Left;
        capStack.Children.Add(shotBtn);
        var capCard = UiKit.Card("CAPTURE INFORMATION", capStack);
        capCard.Margin = new Thickness(0, 16, 0, 0);
        root.Children.Add(capCard);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (s, e) => CaptureNow();
    }

    public void OnShown() { CaptureNow(); _timer.Start(); }
    public void OnHidden() { _timer.Stop(); }

    private static string RefreshRateText()
    {
        try
        {
            var dm = new DevMode { dmSize = (ushort)Marshal.SizeOf(typeof(DevMode)) };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
                return dm.dmDisplayFrequency + " Hz";
        }
        catch { /* abaikan */ }
        return "—";
    }

    private void CaptureNow()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using (var bmp = NativeInput.CaptureScreenScaled(1400))
            {
                if (bmp != null) _preview.Source = UiKit.ToBitmapSource(bmp);
            }
            sw.Stop();
            _chipVals[3].Text = sw.ElapsedMilliseconds + " ms";
            IntPtr fg = NativeInput.ForegroundHandle();
            _chipVals[4].Text = fg != IntPtr.Zero ? NativeInput.TitleOf(fg) : "—";
        }
        catch { /* pratinjau pelengkap */ }
    }

    private void SaveScreenshot()
    {
        try
        {
            using (var bmp = NativeInput.CaptureScreenScaled(1920))
            {
                if (bmp == null) return;
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string file = Path.Combine(dir,
                    "MuseLink-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                long kb = new FileInfo(file).Length / 1024;
                _capInfo.Text = "Screenshot terakhir " + DateTime.Now.ToString("HH:mm:ss")
                    + " • " + kb + " KB • PNG — tersimpan di " + file + ".";
            }
        }
        catch (Exception ex)
        {
            _capInfo.Text = "Screenshot gagal: " + ex.Message;
        }
    }
}
