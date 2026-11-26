using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jembatan;

internal static class Program
{
    private static Mutex _instanceMutex;

    /// <summary>
    /// True bila Mode Administrator aktif tapi user menolak prompt UAC
    /// saat aplikasi dibuka — proses ini lanjut sebagai pengguna biasa
    /// dan MainForm mencatatnya sekali di log.
    /// </summary>
    internal static bool ElevationDeclined { get; private set; }

    /// <summary>
    /// True bila proses ini dimulai OTOMATIS (tugas login / nilai
    /// Run / penjaga — semuanya menambahkan argumen "--auto").
    /// MainForm memakainya untuk menepi sendiri ke system tray
    /// sesudah ±6 detik (v2.7.0, permintaan user). Dibuka manual
    /// oleh user → jendela tetap tampil seperti biasa.
    /// </summary>
    internal static bool StartedAuto { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        StartedAuto = args != null && Array.IndexOf(args, "--auto") >= 0;

        // Pencatat crash (v5.12.0): kematian aplikasi harus
        // meninggalkan jejak tertulis. Penyelidikan restart
        // misterius 26 Nov 2026 membuktikan TIDAK ada crash
        // (listener dimatikan dari UI — tercatat di log utama),
        // tapi pengecualian tak tertangani di masa depan kini
        // tertulis di crash.log, bukan menguap tanpa bukti.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        // Penjaga satu instans: jalan otomatis (Tugas Terjadwal / Run)
        // dan pembukaan manual bisa berbenturan — instans kedua keluar
        // diam-diam. Pengecekan dicoba berulang sebentar karena saat
        // serah-terima naik hak (relaunch admin) instans lama butuh
        // waktu singkat untuk mati dan melepas mutex.
        if (!AcquireSingleInstance())
            return;

        // Mode Administrator (v2.3): pengaturan AKTIF tapi proses ini
        // belum admin → coba naikkan hak SEKALI lewat UAC, lalu keluar
        // (instans admin yang melanjutkan). Manifest tetap asInvoker —
        // kenaikan hak bersifat kondisional saat runtime, bukan wajib.
        // Bila user menolak UAC, JANGAN mengulang: lanjut jalan sebagai
        // pengguna biasa dan catat lewat ElevationDeclined.
        if (Settings.AdminMode && !Settings.IsElevated())
        {
            if (Settings.TryRelaunchElevated(args))
            {
                ReleaseSingleInstance();
                return;
            }
            ElevationDeclined = true;
        }

        // Selaras dengan app.manifest (dpiAwareness PerMonitorV2):
        // koordinat layar selalu piksel fisik — WPF membaca
        // manifest yang sama. UI WPF dimulai dari sini (v5.0.0).
        var app = new App();
        app.InitializeComponent();
        app.Run();

        ReleaseSingleInstance();
    }

    private static bool AcquireSingleInstance()
    {
        for (int i = 0; i < 50; i++)
        {
            var m = new Mutex(false, "JembatanSingleInstance", out bool createdNew);
            if (createdNew)
            {
                _instanceMutex = m;
                return true;
            }
            // Mutex sudah ada: ambil kepemilikan hanya bila pemilik
            // lama sudah melepas — atau MENINGGALKANNYA mati tanpa
            // melepas (AbandonedMutexException, BUG-04 audit: dulu
            // lolos tak tertangkap dari konstruktor initiallyOwned
            // dan aplikasi gagal mulai sesudah crash/kill paksa).
            bool owned;
            try { owned = m.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (owned)
            {
                _instanceMutex = m;
                return true;
            }
            m.Dispose();
            Thread.Sleep(100);
        }
        return false;
    }

    private static void WriteCrashLog(string kind, Exception ex)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jembatan");
            Directory.CreateDirectory(dir);
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + kind + "\t" +
                (ex == null ? "(tanpa objek pengecualian)" : ex.ToString().Replace("\r", " ").Replace("\n", " | "));
            File.AppendAllText(Path.Combine(dir, "crash.log"), line + Environment.NewLine);
        }
        catch { /* pencatat tidak boleh ikut menjatuhkan aplikasi */ }
    }

    private static void ReleaseSingleInstance()
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* abaikan */ }
        try { _instanceMutex?.Dispose(); } catch { /* abaikan */ }
        _instanceMutex = null;
    }
}
