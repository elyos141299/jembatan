using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Jembatan;

/// <summary>
/// Penanda halaman selama tahap kerangka v5.0.0 — digantikan
/// halaman sungguhan satu per satu di tahap halaman. Ditulis
/// dalam kode agar berkasnya tunggal.
/// </summary>
public sealed class PagePlaceholder : UserControl
{
    public PagePlaceholder(string title)
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("CrText"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Halaman ini diisi data asli pada tahap halaman v5.0.0.",
            FontSize = 12.5,
            Foreground = (Brush)FindResource("CrMuted"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });
        Content = panel;
    }
}
