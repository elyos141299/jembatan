using System;
using System.Threading;
using System.Windows.Forms;

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
        // koordinat layar selalu piksel fisik.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());

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

    private static void ReleaseSingleInstance()
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* abaikan */ }
        try { _instanceMutex?.Dispose(); } catch { /* abaikan */ }
        _instanceMutex = null;
    }
}
