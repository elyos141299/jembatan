using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Jembatan ke pendamping Playwright (v5.14.0): proses Python
/// (venv AI-Tools yang sama dengan sidecar AI) yang mengendarai
/// Chrome TERPASANG lewat Playwright dengan PROFIL OTOMASI
/// SENDIRI (%LOCALAPPDATA%\Jembatan\pw-profile — profil harian
/// user tidak disentuh). HTTP lokal 127.0.0.1:8771. Pola
/// hidupnya meniru AiBridge persis: mulai malas pada panggilan
/// pertama, dipakai ulang selama sehat, dimatikan bersama
/// server hanya bila kamilah yang memulainya, dan semua
/// kegagalan dilaporkan jujur (pemanggil memutuskan cadangan
/// ke /browser CDP dasar).
/// </summary>
public static class PwBridge
{
    private const string Base = "http://127.0.0.1:8771";
    private static readonly HttpClient Http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(120),
    };
    private static readonly object Lock = new object();
    private static Process _proc;
    private static bool _weStarted;
    private static string _lastError = "";

    public static string LastError => _lastError;

    public static string SidecarDir => AiBridge.SidecarDir;
    public static string SidecarScript => Path.Combine(SidecarDir, "pw_sidecar.py");
    public static string PythonExe => AiBridge.PythonExe;

    /// <summary>Terpasang = venv AI-Tools ada DAN skrip pendamping ada.</summary>
    public static bool Installed => File.Exists(PythonExe) && File.Exists(SidecarScript);

    public static string ProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "pw-profile");

    public static bool Healthy()
    {
        try
        {
            using var cts = new CancellationTokenSource(900);
            using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/health");
            using var resp = Http.Send(req, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Pastikan sidecar hidup; false + LastError bila gagal.</summary>
    public static bool EnsureRunning()
    {
        if (Healthy()) return true;
        lock (Lock)
        {
            if (Healthy()) return true;
            if (!File.Exists(PythonExe)) { _lastError = "AI-Tools (Python venv) belum terpasang"; return false; }
            try
            {
                DeployScript();
                if (!File.Exists(SidecarScript)) { _lastError = "pw_sidecar.py tidak ditemukan"; return false; }
                var psi = new ProcessStartInfo
                {
                    FileName = PythonExe,
                    Arguments = "\"" + SidecarScript + "\"",
                    WorkingDirectory = AiBridge.AiToolsDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _proc = new Process { StartInfo = psi };
                string logPath = Path.Combine(SidecarDir, "pw-sidecar.log");
                _proc.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
                _proc.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();
                _weStarted = true;
            }
            catch (Exception ex) { _lastError = ex.Message; return false; }
            for (int i = 0; i < 60; i++)
            {
                if (Healthy()) return true;
                try { if (_proc == null || _proc.HasExited) { _lastError = "sidecar Playwright mati saat mulai"; return false; } }
                catch { _lastError = "sidecar Playwright mati saat mulai"; return false; }
                Thread.Sleep(1000);
            }
            _lastError = "sidecar Playwright timeout saat mulai (>60 dtk)";
            return false;
        }
    }

    private static void AppendLog(string path, string line)
    {
        if (line == null) return;
        try { File.AppendAllText(path, line + "\n"); } catch { /* abaikan */ }
    }

    /// <summary>Salin pw_sidecar.py bawaan aplikasi (di samping
    /// exe) ke AI-Tools\sidecar\ bila belum ada / lebih lama.</summary>
    private static void DeployScript()
    {
        Directory.CreateDirectory(SidecarDir);
        var bundled = Path.Combine(AppContext.BaseDirectory, "pw_sidecar.py");
        if (!File.Exists(bundled)) return;
        if (!File.Exists(SidecarScript) ||
            File.GetLastWriteTimeUtc(bundled) > File.GetLastWriteTimeUtc(SidecarScript))
            File.Copy(bundled, SidecarScript, true);
    }

    /// <summary>Kirim satu perintah JSON ke sidecar; balasan
    /// JSON mentah atau null + error jujur.</summary>
    public static string Call(string path, string jsonBody, out string error)
    {
        error = "";
        if (!EnsureRunning()) { error = _lastError; return null; }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Base + path);
            req.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
            using var resp = Http.Send(req);
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
            {
                error = "sidecar Playwright menjawab " + (int)resp.StatusCode;
                return null;
            }
            return body;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    /// <summary>Matikan sidecar bila kamilah yang memulainya
    /// (dipanggil AgentServer.Stop, pola AiBridge.Shutdown).</summary>
    public static void Shutdown()
    {
        try
        {
            if (_weStarted && _proc != null && !_proc.HasExited)
                _proc.Kill(entireProcessTree: true);
        }
        catch { /* abaikan */ }
        _proc = null;
        _weStarted = false;
    }
}
