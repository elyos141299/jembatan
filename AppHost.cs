using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// AppHost (v5.0.0 — bangun ulang WPF): penampung glue NON-UI yang
/// dulu menempel di MainForm WinForms — siklus hidup AgentServer,
/// buffer cincin log + log berkas, jalan otomatis & tugas watchdog
/// (schtasks), saklar jangan-tidur. UI WPF membaca keadaan dari
/// sini; engine tidak mengenal UI sama sekali.
/// </summary>
public sealed class AppHost
{
    private readonly IUiHost _uiHost;

    public AppHost(IUiHost uiHost)
    {
        _uiHost = uiHost;
        Server = new AgentServer(uiHost, Log, OnActivity);
    }

    /// <summary>Server bridge (protokol tidak berubah dari v4).</summary>
    public AgentServer Server { get; }

    /// <summary>Lokasi exe sendiri (pengganti Application.ExecutablePath WinForms).</summary>
    private static string ExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Jembatan.exe");

    // Keadaan jalan otomatis (dirawat blok schtasks di bawah;
    // dulu field MainForm, halaman Settings membacanya dari sini).
    private bool _autoStartEnabled;

    /// <summary>True bila jalan otomatis saat Windows menyala aktif.</summary>
    public bool AutoStartEnabled => _autoStartEnabled;

    /// <summary>Waktu aktivitas bridge terakhir (dibaca UI).</summary>
    public DateTime LastActivity { get; private set; } = DateTime.MinValue;

    /// <summary>Dinaikkan tiap ada aktivitas bridge.</summary>
    public event Action ActivityNoted;

    /// <summary>Dinaikkan saat aplikasi harus menutup diri
    /// (mis. sesudah relaunch sebagai administrator).</summary>
    public event Action RequestShutdown;

    private void OnActivity()
    {
        LastActivity = DateTime.Now;
        try { ActivityNoted?.Invoke(); } catch { /* UI pelengkap */ }
    }

    /// <summary>Urutan mulai meniru Load MainForm lama: rotasi log,
    /// opsi jalan otomatis, jangan-tidur, lalu listener.</summary>
    public void Start()
    {
        RotateLogFileIfNeeded();
        InitStartOptions();
        if (Settings.KeepAwake) NativeInput.SetKeepAwake(true);
        Server.Start();
    }

    public void Stop() => Server.Stop();

    // ---------- Log (buffer cincin + berkas) ----------

    private readonly List<string> _logRing = new List<string>();

    /// <summary>Salinan baris log terakhir untuk UI,
    /// format "HH:mm:ss\tisi", tertua dulu.</summary>
    public List<string> RecentLogLines(int max)
    {
        lock (_logRing)
        {
            int skip = Math.Max(0, _logRing.Count - max);
            var hasil = new List<string>();
            for (int i = skip; i < _logRing.Count; i++) hasil.Add(_logRing[i]);
            return hasil;
        }
    }

    public void Log(string message)
    {
        if (message == null) message = "";
        string line = DateTime.Now.ToString("HH:mm:ss") + "\t" + message;
        lock (_logRing)
        {
            _logRing.Add(line);
            while (_logRing.Count > 300) _logRing.RemoveAt(0);
        }
        AppendLogToFile(line);
    }

    /// <summary>Saklar jangan-tidur (dulu MainForm.ApplyKeepAwake).</summary>
    public void ApplyKeepAwake(bool on)
    {
        try { Settings.KeepAwake = on; }
        catch (Exception ex)
        {
            Log("GAGAL menyimpan Jangan tidur: " + ex.Message);
            return;
        }
        NativeInput.SetKeepAwake(on);
        Log(on
            ? "Jangan tidur: AKTIF — PC dicegah tidur selama MuseLink jalan"
            : "Jangan tidur: MATI");
    }

    private static readonly object LogFileLock = new object();

    private static string LogFilePath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jembatan", "jembatan.log");
        }
    }

    private static void AppendLogToFile(string line)
    {
        try
        {
            lock (LogFileLock)
            {
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch { /* abaikan */ }
    }

    // Rotasi sederhana saat aplikasi dimulai: berkas log yang sudah
    // melewati 2 MB diganti nama jadi jembatan.log.old (meniban yang
    // lama) dan berkas baru dimulai dari kosong.
    private static void RotateLogFileIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                string oldPath = LogFilePath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
                File.Move(LogFilePath, oldPath);
            }
        }
        catch { /* abaikan */ }
    }
    internal void ToggleServer()
    {
        if (Server.IsRunning)
        {
            Server.Stop();
            Log("Listener DIMATIKAN oleh kamu — akses Muse berhenti total.");
        }
        else
        {
            try
            {
                Server.Start();
                Log("Listener dinyalakan lagi — Muse bisa terhubung.");
            }
            catch (Exception ex)
            {
                Log("GAGAL memulai layanan: " + ex.Message);
            }
        }
    }

    // ---------- Jalan otomatis & Mode Administrator (v2.1 + v2.3) ----------
    // Dua mekanisme jalan otomatis, dipilih oleh SyncAutoStart():
    //  - Nilai "MuseLink" di HKCU\...\CurrentVersion\Run — jalan
    //    sebagai pengguna biasa (perilaku v2.1, tanpa admin).
    //  - Tugas Terjadwal "MuseLink" berhak tertinggi (schtasks) —
    //    jalan sebagai ADMINISTRATOR saat login, tanpa prompt UAC.
    //    Hanya bisa dibuat/dihapus oleh proses yang sedang admin.
    // Mode Administrator sendiri disimpan di HKCU\Software\Jembatan
    // (kelas Settings); kenaikan hak prosesnya diatur di Program.cs.
    //
    // Nama tampil aplikasi berganti dua kali: "Jembatan" (≤ v2.2) →
    // "Muse Windows" (v2.3.2) → "MuseLink" (sejak v2.3.3). Nama
    // internal tetap "Jembatan": berkas exe, kunci registry, mutex.
    // Tugas & nilai Run dari SEMUA generasi lama dibersihkan oleh
    // SyncAutoStart()/SetAutoStart() sebagai migrasi nama. Di mode
    // terjadwal juga ada tugas penjaga "MuseLink Watchdog"
    // (EnsureWatchdogTask); pendahulunya "Muse Windows Watchdog"
    // ikut dibersihkan.

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MuseLink";
    private static readonly string[] LegacyRunValueNames = { "Jembatan", "Muse Windows" };
    private const string TaskName = "MuseLink";
    private const string WatchdogTaskName = "MuseLink Watchdog";
    private static readonly string[] LegacyTaskNames = { "Jembatan", "Muse Windows", "Muse Windows Watchdog" };

    private static string ExpectedRunCommand()
    {
        // Path exe dikutip agar aman dari spasi di nama folder.
        return "\"" + ExePath + "\" --auto";
    }

    private static bool HasAutoStartValue()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null) return false;
                // Nilai generasi lama juga dihitung ada (migrasi nama).
                if (key.GetValue(RunValueName) != null) return true;
                foreach (var legacy in LegacyRunValueNames)
                    if (key.GetValue(legacy) != null) return true;
                return false;
            }
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enabled)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (key == null) return;
            if (enabled) key.SetValue(RunValueName, ExpectedRunCommand());
            else key.DeleteValue(RunValueName, false);
            // Nilai generasi lama selalu dibersihkan.
            foreach (var legacy in LegacyRunValueNames)
                key.DeleteValue(legacy, false);
        }
    }

    // ---------- Tugas Terjadwal (schtasks) ----------

    private static int RunSchtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using (var p = Process.Start(psi))
        {
            if (p == null) { output = ""; return -1; }
            // stderr dibaca async agar kedua pipa tidak saling memblokir.
            var errTask = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch { /* abaikan */ }
                output = stdout;
                return -2;
            }
            string stderr = "";
            try { stderr = errTask.Result; } catch { /* abaikan */ }
            output = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return p.ExitCode;
        }
    }

    private static bool ScheduledTaskExists(string name)
    {
        try
        {
            return RunSchtasks("/Query /TN \"" + name + "\"", out _) == 0;
        }
        catch { return false; }
    }

    private void EnsureScheduledTask()
    {
        // Path exe di dalam nilai /TR harus terbungkus tanda kutip —
        // pola baku schtasks: /TR "\"C:\...\Jembatan.exe\"".
        string args = "/Create /TN \"" + TaskName + "\" /TR \"\\\"" +
                      ExePath + "\\\" --auto\" /SC ONLOGON /RL HIGHEST /F";
        int code = RunSchtasks(args, out string output);
        if (code != 0)
            Log("GAGAL membuat tugas terjadwal admin (kode " + code + "): " + output.Trim());
    }

    // Penjaga anti-crash (v2.3.2): tugas "MuseLink Watchdog"
    // berjalan tiap menit menjalankan VBScript TERSEMBUNYI yang
    // menyalakan ulang Jembatan bila prosesnya tidak ada — KECUALI
    // bendera UserStopped terpasang (user berhenti sengaja lewat menu
    // tray → Keluar). Penutupan oleh Windows shutdown / Task Manager
    // tidak memasang bendera itu, jadi penjaga tetap menyalakan
    // ulang. Didaftarkan/dihapus bersama tugas login di SyncAutoStart,
    // dengan syarat yang sama (jalan otomatis + admin + proses admin).
    // v3.10.0: penjaga juga memprobe /ping — proses yang ada tapi
    // tidak menjawab 2× berurutan dibunuh & dinyalakan ulang.
    private void EnsureWatchdogTask()
    {
        // v2.5.2: penjaga kini VBScript lewat wscript.exe — TANPA
        // konsol sama sekali. Cara lama (powershell -File, sejak
        // v2.3.4) walau memakai -WindowStyle Hidden tetap sempat
        // menampilkan jendela konsol sepersekian detik di sebagian
        // mesin (user melaporkannya sebagai jendela yang nongol
        // beberapa milidetik, makin sering sejak interval penjaga
        // dipendekkan ke 1 menit di v2.4.5). wscript tidak punya
        // jendela konsol, jadi kedip itu hilang secara fisik.
        string scriptPath;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jembatan");
            Directory.CreateDirectory(dir);
            scriptPath = Path.Combine(dir, "watchdog.vbs");
            // v3.10.0 (DSA-13): penjaga kini juga SADAR
            // RESPONSIVITAS — proses yang ada tapi tidak menjawab
            // /ping dua kali berurutan dibunuh & dinyalakan ulang
            // (dulu: proses menggantung lolos selamanya karena
            // yang dicek hanya keberadaan proses). Penghitung
            // gagal disimpan di watchdog-fails.txt, reset saat
            // ping sehat atau proses dinyalakan dari nol.
            string script =
                "' Penjaga MuseLink (v3.10.0): VBScript - tanpa konsol sama sekali.\r\n" +
                "On Error Resume Next\r\n" +
                "Dim shell, fso, stopped, procs, failFile, fails, http, okPing\r\n" +
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                "Set fso = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
                "failFile = shell.ExpandEnvironmentStrings(\"%LOCALAPPDATA%\") & \"\\Jembatan\\watchdog-fails.txt\"\r\n" +
                "stopped = 0\r\n" +
                "stopped = shell.RegRead(\"HKCU\\Software\\Jembatan\\UserStopped\")\r\n" +
                "If stopped <> 1 Then\r\n" +
                "  Set procs = GetObject(\"winmgmts:\").ExecQuery(\"SELECT Name FROM Win32_Process WHERE Name = 'Jembatan.exe'\")\r\n" +
                "  If procs.Count = 0 Then\r\n" +
                "    If fso.FileExists(failFile) Then fso.DeleteFile failFile\r\n" +
                "    shell.Run \"\"\"" + ExePath + "\"\" --auto\", 0, False\r\n" +
                "  Else\r\n" +
                "    okPing = False\r\n" +
                "    Set http = CreateObject(\"WinHttp.WinHttpRequest.5.1\")\r\n" +
                "    http.SetTimeouts 1000, 1000, 1000, 2000\r\n" +
                "    http.Open \"GET\", \"http://127.0.0.1:8765/ping\", False\r\n" +
                "    http.Send\r\n" +
                "    If Err.Number = 0 Then If http.Status = 200 Then okPing = True\r\n" +
                "    Err.Clear\r\n" +
                "    If okPing Then\r\n" +
                "      If fso.FileExists(failFile) Then fso.DeleteFile failFile\r\n" +
                "    Else\r\n" +
                "      fails = 0\r\n" +
                "      If fso.FileExists(failFile) Then fails = CInt(Trim(fso.OpenTextFile(failFile, 1).ReadAll))\r\n" +
                "      fails = fails + 1\r\n" +
                "      Dim tf\r\n" +
                "      Set tf = fso.CreateTextFile(failFile, True)\r\n" +
                "      tf.Write fails\r\n" +
                "      tf.Close\r\n" +
                "      If fails >= 2 Then\r\n" +
                "        shell.Run \"taskkill /F /IM Jembatan.exe\", 0, True\r\n" +
                "        WScript.Sleep 2000\r\n" +
                "        fso.DeleteFile failFile\r\n" +
                "        shell.Run \"\"\"" + ExePath + "\"\" --auto\", 0, False\r\n" +
                "      End If\r\n" +
                "    End If\r\n" +
                "  End If\r\n" +
                "End If\r\n";
            File.WriteAllText(scriptPath, script);
            // Berkas penjaga lama (PowerShell) dibersihkan.
            try { File.Delete(Path.Combine(dir, "watchdog.ps1")); } catch { /* abaikan */ }
        }
        catch (Exception ex)
        {
            Log("GAGAL menulis naskah penjaga: " + ex.Message);
            return;
        }
        string tr = "wscript.exe \\\"" + scriptPath + "\\\"";
        string args = "/Create /TN \"" + WatchdogTaskName + "\" /TR \"" + tr +
                      "\" /SC MINUTE /MO 1 /RL HIGHEST /F";
        int code = RunSchtasks(args, out string output);
        if (code != 0)
            Log("GAGAL membuat tugas penjaga (kode " + code + "): " + output.Trim());
    }

    private void DeleteScheduledTask(string name)
    {
        int code = RunSchtasks("/Delete /TN \"" + name + "\" /F", out string output);
        if (code != 0)
            Log("GAGAL menghapus tugas terjadwal (kode " + code + "): " + output.Trim());
        else
            Log("Tugas terjadwal '" + name + "' dihapus.");
    }

    /// <summary>
    /// Selaraskan mekanisme jalan otomatis dengan keadaan yang diminta:
    /// saklar jalan otomatis + pengaturan Mode Administrator + hak
    /// proses ini. Dipanggil sekali saat jendela dimuat dan tiap kali
    /// SALAH SATU saklar berubah. Semua kegagalan hanya dicatat di
    /// log — tidak pernah menjatuhkan jendela.
    /// </summary>
    private void SyncAutoStart()
    {
        bool autoStart = _autoStartEnabled;
        bool adminWanted = Settings.AdminMode;
        bool elevated = Settings.IsElevated();
        try
        {
            if (autoStart && adminWanted && elevated)
            {
                // Jalan otomatis sebagai admin: tugas terjadwal yang
                // memegang peran (login + penjaga anti-crash); nilai
                // Run dicabut agar tidak dobel. Tugas generasi lama
                // ("Jembatan", "Muse Windows", "Muse Windows
                // Watchdog") ikut dibersihkan (migrasi nama).
                EnsureScheduledTask();
                EnsureWatchdogTask();
                foreach (var legacy in LegacyTaskNames)
                    if (ScheduledTaskExists(legacy))
                        DeleteScheduledTask(legacy);
                SetAutoStart(false);
            }
            else if (autoStart)
            {
                // Jalan otomatis sebagai pengguna biasa: nilai Run.
                SetAutoStart(true);
                DeleteAdminTasks(elevated);
            }
            else
            {
                SetAutoStart(false);
                DeleteAdminTasks(elevated);
            }
        }
        catch (Exception ex)
        {
            Log("GAGAL menyelaraskan jalan otomatis: " + ex.Message);
        }
    }

    // Hapus tugas-tugas mode admin (login "MuseLink", penjaganya, dan
    // semua nama generasi lama) bila masih terdaftar. Menghapus tugas
    // butuh proses admin; bila proses ini belum admin, cukup catat di
    // log — perilaku yang sama seperti v2.3 untuk tugas login.
    private void DeleteAdminTasks(bool elevated)
    {
        var names = new System.Collections.Generic.List<string> { TaskName, WatchdogTaskName };
        names.AddRange(LegacyTaskNames);
        foreach (var name in names)
        {
            if (!ScheduledTaskExists(name)) continue;
            if (elevated) DeleteScheduledTask(name);
            else Log("Tugas terjadwal '" + name + "' masih terdaftar — penghapusannya butuh MuseLink berjalan sebagai administrator.");
        }
    }

    private void InitStartOptions()
    {
        bool taskExists = ScheduledTaskExists(TaskName)
                          || ScheduledTaskExists("Jembatan")
                          || ScheduledTaskExists("Muse Windows");
        // "Jalan pertama" = belum ada jejak apa pun: nilai Run tidak
        // ada, tugas tidak ada, dan Mode Administrator belum pernah
        // ditulis. Di jalan pertama jalan otomatis AKTIF secara bawaan
        // (perilaku v2.1); sesudahnya keadaan registry yang berkuasa,
        // supaya pilihan MATI dari user tidak dibatalkan diam-diam.
        bool firstRun = !Settings.HasAdminModeValue && !HasAutoStartValue() && !taskExists;
        bool autoStart = HasAutoStartValue() || taskExists || firstRun;
        if (firstRun)
            Log("Jalan otomatis saat Windows menyala: AKTIF (diaktifkan di jalan pertama).");

        // Sejak v2.5.0 saklarnya tinggal di jendela Pengaturan —
        // di sini cukup keadaan awal yang disimpan.
        _autoStartEnabled = autoStart;

        if (Program.ElevationDeclined)
            Log("Mode Administrator ditolak — jalan sebagai pengguna biasa");

        SyncAutoStart();

        if (firstRun)
        {
            // Tulis nilai Mode Administrator apa adanya supaya jalan
            // berikutnya tidak lagi dianggap "jalan pertama".
            try { Settings.AdminMode = Settings.AdminMode; }
            catch { /* abaikan — bukan hal kritis */ }
        }
    }

    /// <summary>Dipanggil jendela Pengaturan saat saklar jalan otomatis berubah.</summary>
    internal void ApplyAutoStart(bool on)
    {
        _autoStartEnabled = on;
        Log(on
            ? "Jalan otomatis saat Windows menyala: AKTIF"
            : "Jalan otomatis saat Windows menyala: MATI");
        SyncAutoStart();
    }

    /// <summary>Dipanggil jendela Pengaturan saat saklar Mode Administrator berubah.</summary>
    internal void ApplyAdminMode(bool on)
    {
        try
        {
            Settings.AdminMode = on;
        }
        catch (Exception ex)
        {
            Log("GAGAL menyimpan Mode Administrator: " + ex.Message);
            return;
        }

        if (on)
        {
            Log("Mode Administrator: AKTIF");
            if (!Settings.IsElevated())
            {
                // Naikkan hak sekarang juga: mulai ulang sebagai admin
                // lalu tutup instans ini (instans admin yang meneruskan).
                if (Settings.TryRelaunchElevated(Array.Empty<string>()))
                {
                    RequestShutdown?.Invoke();
                    return;
                }
                Log("Mode Administrator ditolak — jalan sebagai pengguna biasa");
            }
            SyncAutoStart();
        }
        else
        {
            SyncAutoStart();
            // Proses ini tetap admin sampai ditutup — yang berubah
            // adalah jalan berikutnya.
            Log("Mode Administrator dimatikan — berlaku penuh setelah MuseLink dibuka ulang");
        }
    }
}
