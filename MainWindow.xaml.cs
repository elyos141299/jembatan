using System.Reflection;
using System.Windows;

namespace Jembatan;

/// <summary>
/// Jendela utama Control Room WPF (v5.0.0). Tahap fondasi: kerangka
/// kosong bertema gelap; sidebar + 9 halaman datang di tahap
/// kerangka berikutnya.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = "Control Room • v" + (v != null ? v.ToString(3) : "5.0.0");
    }
}
