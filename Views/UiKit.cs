using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Jembatan;

/// <summary>Halaman Control Room yang sadar tampil/sembunyi —
/// timer &amp; polling hanya jalan saat halamannya terlihat, agar
/// navigasi tidak mengganggu tugas yang sedang berjalan.</summary>
public interface IRoomPage
{
    void OnShown();
    void OnHidden();
}

/// <summary>Pabrik elemen UI bersama halaman-halaman Control Room
/// WPF (padanan RoomUi di WinForms lama).</summary>
public static class UiKit
{
    public static System.Windows.Media.Brush Res(string key) => (System.Windows.Media.Brush)Application.Current.FindResource(key);

    public static TextBlock CardTitle(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("CrCardTitle"),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    public static TextBlock Value(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("CrBigValue"),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    public static TextBlock Muted(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("CrMutedText"),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    public static TextBlock Body(string text)
    {
        return new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("CrBody"),
            TextWrapping = TextWrapping.Wrap,
        };
    }

    /// <summary>Kartu: judul kecil di atas isi.</summary>
    public static Border Card(string title, UIElement content)
    {
        var panel = new StackPanel();
        panel.Children.Add(CardTitle(title));
        panel.Children.Add(content);
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = panel,
        };
    }

    /// <summary>Kartu dengan titik aksen berwarna di samping judul
    /// (v5.1 — aksen Pasangan 4 per kartu Overview).</summary>
    public static Border Card(string title, UIElement content, string accentKey)
    {
        var titleBlock = CardTitle(title);
        titleBlock.Margin = new Thickness(0);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };
        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Res(accentKey),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        row.Children.Add(titleBlock);
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(content);
        return new Border
        {
            Style = (Style)Application.Current.FindResource("CrCardStyle"),
            Child = panel,
        };
    }

    public static Button Btn(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("CrButton"),
        };
        b.Click += onClick;
        return b;
    }

    /// <summary>Tombol aksi utama (isian cyan) untuk tindakan
    /// paling penting di halamannya (v5.2).</summary>
    public static Button PrimaryBtn(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("CrPrimaryButton"),
        };
        b.Click += onClick;
        return b;
    }

    public static TextBox Input(string text = "")
    {
        return new TextBox
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("CrTextBox"),
        };
    }

    public static ListBox List()
    {
        return new ListBox
        {
            Style = (Style)Application.Current.FindResource("CrListBox"),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        };
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    /// <summary>Bitmap GDI -> BitmapSource WPF (pegangan HBitmap
    /// selalu dilepas).</summary>
    public static BitmapSource ToBitmapSource(Bitmap bmp)
    {
        IntPtr h = bmp.GetHbitmap();
        try
        {
            return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally { DeleteObject(h); }
    }

    /// <summary>Ambil nilai kamus BuildHealth sebagai teks.</summary>
    public static string GetStr(System.Collections.Generic.Dictionary<string, object> d, string key)
    {
        if (d == null) return "";
        if (!d.TryGetValue(key, out var v) || v == null) return "";
        return v.ToString();
    }

    public static bool GetBool(System.Collections.Generic.Dictionary<string, object> d, string key)
    {
        if (d == null) return false;
        return d.TryGetValue(key, out var v) && v is bool b && b;
    }

    public static long GetLong(System.Collections.Generic.Dictionary<string, object> d, string key)
    {
        if (d == null) return 0;
        if (!d.TryGetValue(key, out var v) || v == null) return 0;
        try { return Convert.ToInt64(v); } catch { return 0; }
    }

    // ---------- Komponen gaya Figma (v5.3.0) ----------

    /// <summary>Warna mentah dari sumber daya "CrXColor".</summary>
    public static System.Windows.Media.Color ResColor(string key)
        => (System.Windows.Media.Color)Application.Current.FindResource(key);

    /// <summary>Label seksi kecil ala Figma (huruf kapital redup).</summary>
    public static TextBlock SectionLabel(string text)
        => new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("CrFaint"),
        };

    /// <summary>Titik status bundar.</summary>
    public static System.Windows.Shapes.Ellipse Dot(string colorKey, double size = 8)
        => new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Fill = Res(colorKey),
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>Lencana pil bertinta warna (latar transparan warna
    /// aksen + teks berwarna) ala badge Figma. Teksnya bisa diubah
    /// lewat SetBadgeText().</summary>
    public static Border Badge(string text, string colorKey)
    {
        string colorRes = colorKey == "CrSage" ? "CrSageColor"
            : colorKey == "CrGreen" ? "CrGreenColor"
            : colorKey == "CrGold" ? "CrGoldColor"
            : colorKey == "CrRed" ? "CrRedColor"
            : colorKey == "CrMuted" ? "CrMutedColor"
            : colorKey == "CrFaint" ? "CrFaintColor" : "CrAccentColor";
        var c = ResColor(colorRes);
        var bg = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, c.R, c.G, c.B));
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(c),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3, 9, 3),
            Child = tb,
            Tag = tb,
        };
    }

    /// <summary>Ubah teks lencana yang dibuat Badge().</summary>
    public static void SetBadgeText(Border badge, string text)
    {
        if (badge.Tag is TextBlock tb) tb.Text = text;
    }

    /// <summary>Bilah kemajuan: rel elevated + isian berwarna.
    /// Atur persennya lewat SetBar().</summary>
    public static Grid Bar(string colorKey, double height)
    {
        var track = new Grid { Height = height };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
        var bg = new Border
        {
            Background = Res("CrElevated"),
            CornerRadius = new CornerRadius(height / 2),
        };
        Grid.SetColumnSpan(bg, 2);
        track.Children.Add(bg);
        var fill = new Border
        {
            Background = Res(colorKey),
            CornerRadius = new CornerRadius(height / 2),
        };
        track.Children.Add(fill);
        track.Tag = fill;
        return track;
    }

    /// <summary>Atur persen bilah yang dibuat Bar() (0-100).</summary>
    public static void SetBar(Grid track, double pct)
    {
        if (pct < 0) pct = 0;
        if (pct > 100) pct = 100;
        track.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        track.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
        if (track.Tag is Border fill) fill.Visibility = pct <= 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Tombol garis bahaya (Hentikan/Stop/Tutup).</summary>
    public static Button DangerBtn(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource("CrDangerOutline"),
            Padding = new Thickness(16, 7, 16, 7),
        };
        if (onClick != null) b.Click += onClick;
        return b;
    }

    /// <summary>Saklar gaya Figma (CrToggle).</summary>
    public static CheckBox Toggle(bool on)
        => new CheckBox
        {
            Style = (Style)Application.Current.FindResource("CrToggle"),
            IsChecked = on,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>Nama ramah operasi bridge untuk linimasa.</summary>
    public static string FriendlyOp(string method, string path)
    {
        switch (path)
        {
            case "/exec": return "Perintah dijalankan";
            case "/screenshot": case "/frame": return "Screenshot diambil";
            case "/click": return "Klik dieksekusi";
            case "/type": return "Teks diketik";
            case "/key": return "Tombol ditekan";
            case "/move": return "Mouse digerakkan";
            case "/open": return "Aplikasi dibuka";
            case "/health": return "Pemeriksaan kesehatan";
            case "/ping": return "Ping bridge";
            default:
                if (path != null && path.StartsWith("/routines")) return "Workflow dijalankan";
                if (path != null && path.StartsWith("/window")) return "Jendela diatur";
                if (path != null && path.StartsWith("/uia")) return "Elemen UI dioperasikan";
                return (method ?? "") + " " + (path ?? "");
        }
    }

    /// <summary>Kunci warna titik dari kode status HTTP.</summary>
    public static string StatusColor(string status)
    {
        if (int.TryParse(status, out int s))
        {
            if (s >= 200 && s < 300) return "CrGreen";
            if (s >= 400 && s < 500) return "CrGold";
            if (s >= 500) return "CrRed";
        }
        return "CrAccent";
    }
}
