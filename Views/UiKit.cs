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
            FontFamily = new FontFamily("Consolas"),
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
            return Imaging.CreateBitmapSourceFromHBitmap(
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
}
