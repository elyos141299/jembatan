using System;
using System.Windows.Forms;

namespace Jembatan;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Selaras dengan app.manifest (dpiAwareness PerMonitorV2):
        // koordinat layar selalu piksel fisik.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
