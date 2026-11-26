using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Rekam &amp; putar ulang gerakan mouse/keyboard (v5.10.0 —
/// Upgrade Tangan butir 8). Rekaman memakai hook tingkat
/// rendah (WH_MOUSE_LL / WH_KEYBOARD_LL) di utas khusus
/// berpump pesan; kejadian disimpan bertanda waktu relatif
/// dan ditulis sebagai JSON lokal di
/// %LOCALAPPDATA%\Jembatan\macros\&lt;nama&gt;.json.
///
/// Batas keras: rekam maks 10 menit / 20.000 kejadian
/// (sesudahnya kejadian baru tidak ditambah dan bendera
/// truncated menyala — hook TIDAK pernah bocor: selalu
/// dilepas saat Stop/Shutdown). Gerakan mouse disampel
/// hemat (maks tiap 30 ms atau geser &gt;=4px). Kejadian
/// suntikan (dari Muse sendiri / skrip) ikut terekam dan
/// DITANDAI Inj=true — makro adalah catatan apa yang
/// terjadi di layar, dan putar ulang memainkannya apa
/// adanya.
///
/// Putar ulang berjalan di utas sendiri (respons HTTP tidak
/// menggantung selama makro berjalan), berhenti bila
/// dihentikan eksplisit, Emergency Stop aktif, atau server
/// berhenti; tombol yang sedang tertahan di tengah putar
/// ulang selalu dilepas di akhir.
/// </summary>
public static class MacroRecorder
{
    // ---------- Model ----------

    public sealed class MacroEvent
    {
        public long T { get; set; }
        public string Kind { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public int Delta { get; set; }
        public int Vk { get; set; }
        public bool Inj { get; set; }
    }

    public sealed class MacroFile
    {
        public string Name { get; set; } = "";
        public string Created { get; set; } = "";
        public long DurationMs { get; set; }
        public bool Truncated { get; set; }
        public List<MacroEvent> Events { get; set; } = new List<MacroEvent>();
    }

    // ---------- Konstanta & keadaan rekam ----------

    private const int MaxEvents = 20000;
    private const long MaxDurationMs = 600000; // 10 menit

    private static readonly object _lock = new object();
    private static Thread _hookThread;
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static IntPtr _keyHook = IntPtr.Zero;
    private static LowLevelProc _mouseProc; // disimpan: mencegah GC delegate hook
    private static LowLevelProc _keyProc;
    private static uint _hookThreadId;
    private static Stopwatch _clock;
    private static List<MacroEvent> _events;
    private static string _recName = "";
    private static bool _truncated;
    private static long _lastMoveT;
    private static int _lastMoveX, _lastMoveY;
    private static ManualResetEventSlim _hookReady;
    private static bool _hookOk;
    // Pemungut posisi berkala (v5.11.0 — Bagian A.1b): hook
    // tingkat rendah TIDAK menerima gerakan SetCursorPos
    // (teleport senyap), jadi selama merekam posisi kursor
    // ikut disampel dari utas terpisah sebagai jaring kedua.
    private static Thread _pollThread;
    private static volatile bool _pollStop;

    public static bool IsRecording { get { lock (_lock) { return _hookThread != null; } } }
    public static string RecordingName { get { lock (_lock) { return _recName; } } }
    public static int RecordedEvents { get { lock (_lock) { return _events?.Count ?? 0; } } }
    public static long RecordElapsedMs { get { lock (_lock) { return _clock?.ElapsedMilliseconds ?? 0; } } }

    public static string MacrosDir()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jembatan", "macros");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "makro";
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c.ToString(), "");
        name = name.Trim();
        if (name.Length == 0) name = "makro";
        if (name.Length > 60) name = name.Substring(0, 60);
        return name;
    }

    // ---------- Rekam: mulai / berhenti ----------

    /// <summary>Mulai merekam. False bila sudah merekam /
    /// sedang memutar / hook gagal terpasang.</summary>
    public static bool StartRecording(string name)
    {
        lock (_lock)
        {
            if (_hookThread != null || _playThread != null) return false;
            _recName = SanitizeName(name);
            _events = new List<MacroEvent>();
            _truncated = false;
            _lastMoveT = -1000; _lastMoveX = int.MinValue; _lastMoveY = int.MinValue;
            _hookReady = new ManualResetEventSlim(false);
            _hookOk = false;
            _hookThread = new Thread(HookThreadMain)
            { IsBackground = true, Name = "MacroHook" };
            _hookThread.Start();
        }
        _hookReady.Wait(2000);
        bool ok;
        lock (_lock) { ok = _hookOk && _hookThread != null; }
        if (!ok) Shutdown(); // hook gagal terpasang — jangan tinggalkan utas menggantung
        if (ok)
        {
            _pollStop = false;
            _pollThread = new Thread(PollThreadMain)
            { IsBackground = true, Name = "MacroPoll" };
            _pollThread.Start();
        }
        return ok;
    }

    // Sampel posisi kursor tiap 50 ms selama merekam.
    // Kejadian move hanya ditambah bila kursor berpindah
    // ≥4px dari titik tercatat terakhir DAN hook belum
    // mencatat gerakan dalam 80 ms terakhir — jadi jalur
    // ini khusus menangkap gerakan yang hook lewatkan
    // (teleport), bukan menduplikasi gerakan fisik.
    private static void PollThreadMain()
    {
        while (!_pollStop)
        {
            try
            {
                var pos = NativeInput.CursorPosition();
                lock (_lock)
                {
                    if (_events != null && _clock != null)
                    {
                        long t = _clock.ElapsedMilliseconds;
                        bool jauh = Math.Abs(pos.X - _lastMoveX) >= 4 || Math.Abs(pos.Y - _lastMoveY) >= 4;
                        if (jauh && t - _lastMoveT >= 80)
                        {
                            _lastMoveT = t; _lastMoveX = pos.X; _lastMoveY = pos.Y;
                            AddEventLocked(new MacroEvent { Kind = "move", X = pos.X, Y = pos.Y, Inj = true }, t);
                        }
                    }
                }
            }
            catch { /* pemungut tidak boleh menjatuhkan perekam */ }
            Thread.Sleep(50);
        }
    }

    private static void StopPolling()
    {
        _pollStop = true;
        var pt = _pollThread;
        _pollThread = null;
        if (pt != null && pt.IsAlive) pt.Join(600);
    }

    private static void HookThreadMain()
    {
        _mouseProc = HookCallback;
        _keyProc = HookCallback;
        IntPtr hMod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, hMod, 0);
        _keyHook = SetWindowsHookEx(WhKeyboardLl, _keyProc, hMod, 0);
        _hookThreadId = GetCurrentThreadId();
        lock (_lock)
        {
            _hookOk = _mouseHook != IntPtr.Zero && _keyHook != IntPtr.Zero;
            _clock = Stopwatch.StartNew();
        }
        _hookReady?.Set();
        // Pump pesan: hook tingkat rendah hanya dipanggil
        // selama utas memasang & mengolah pesan.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }

    /// <summary>Berhenti merekam &amp; simpan ke berkas.
    /// Mengembalikan makro yang tersimpan (null bila tidak
    /// sedang merekam).</summary>
    public static MacroFile StopRecording()
    {
        Thread t;
        lock (_lock)
        {
            t = _hookThread;
            if (t == null) return null;
        }
        PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        t.Join(2500);
        StopPolling();
        MacroFile file;
        lock (_lock)
        {
            _hookThread = null;
            file = new MacroFile
            {
                Name = _recName,
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                DurationMs = _clock?.ElapsedMilliseconds ?? 0,
                Truncated = _truncated,
                Events = _events ?? new List<MacroEvent>(),
            };
            _events = null;
            _clock = null;
            _recName = "";
        }
        try
        {
            string path = Path.Combine(MacrosDir(), file.Name + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(file));
        }
        catch { /* makro tetap dikembalikan walau simpan gagal */ }
        return file;
    }

    // ---------- Callback hook (HARUS ramping) ----------

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Callback yang sama melayani mouse & keyboard:
        // bedakan dari rentang pesan.
        if (nCode >= 0)
        {
            try
            {
                int msg = wParam.ToInt32();
                if (msg >= 0x0100 && msg <= 0x0105)
                {
                    var kb = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    bool down = msg == WmKeyDown || msg == WmSysKeyDown;
                    bool up = msg == WmKeyUp || msg == WmSysKeyUp;
                    if (down || up)
                        AddEvent(new MacroEvent
                        {
                            Kind = down ? "keydown" : "keyup",
                            Vk = (int)kb.VkCode,
                            Inj = (kb.Flags & LlkhfInjected) != 0,
                        });
                }
                else
                {
                    var ms = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                    bool inj = (ms.Flags & LlmhfInjected) != 0;
                    switch (msg)
                    {
                        case WmMouseMove:
                            AddMove(ms.Pt.X, ms.Pt.Y, inj);
                            break;
                        case WmLButtonDown: AddEvent(new MacroEvent { Kind = "ldown", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmLButtonUp: AddEvent(new MacroEvent { Kind = "lup", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmRButtonDown: AddEvent(new MacroEvent { Kind = "rdown", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmRButtonUp: AddEvent(new MacroEvent { Kind = "rup", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmMButtonDown: AddEvent(new MacroEvent { Kind = "mdown", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmMButtonUp: AddEvent(new MacroEvent { Kind = "mup", X = ms.Pt.X, Y = ms.Pt.Y, Inj = inj }); break;
                        case WmMouseWheel:
                            AddEvent(new MacroEvent { Kind = "wheel", X = ms.Pt.X, Y = ms.Pt.Y, Delta = (short)(ms.MouseData >> 16), Inj = inj });
                            break;
                        case WmMouseHWheel:
                            AddEvent(new MacroEvent { Kind = "hwheel", X = ms.Pt.X, Y = ms.Pt.Y, Delta = (short)(ms.MouseData >> 16), Inj = inj });
                            break;
                    }
                }
            }
            catch { /* callback hook tidak boleh melempar */ }
        }
        // Teruskan ke rantai hook yang benar: pesan keyboard
        // lewat hook keyboard, sisanya hook mouse.
        int m2 = wParam.ToInt32();
        IntPtr hook = (m2 >= 0x0100 && m2 <= 0x0105) ? _keyHook : _mouseHook;
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static void AddMove(int x, int y, bool inj)
    {
        lock (_lock)
        {
            if (_events == null || _clock == null) return;
            long t = _clock.ElapsedMilliseconds;
            bool jauh = Math.Abs(x - _lastMoveX) >= 4 || Math.Abs(y - _lastMoveY) >= 4;
            if (t - _lastMoveT < 30 && !jauh) return;
            _lastMoveT = t; _lastMoveX = x; _lastMoveY = y;
            AddEventLocked(new MacroEvent { Kind = "move", X = x, Y = y, Inj = inj }, t);
        }
    }

    private static void AddEvent(MacroEvent ev)
    {
        lock (_lock)
        {
            if (_events == null || _clock == null) return;
            AddEventLocked(ev, _clock.ElapsedMilliseconds);
        }
    }

    private static void AddEventLocked(MacroEvent ev, long t)
    {
        if (t > MaxDurationMs || _events.Count >= MaxEvents)
        {
            _truncated = true;
            return;
        }
        ev.T = t;
        _events.Add(ev);
    }

    // ---------- Daftar & muat ----------

    public static List<Dictionary<string, object>> ListMacros()
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            foreach (string f in Directory.GetFiles(MacrosDir(), "*.json"))
            {
                var fi = new FileInfo(f);
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = Path.GetFileNameWithoutExtension(f),
                    ["bytes"] = fi.Length,
                    ["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                });
            }
        }
        catch { /* folder belum ada dsb. */ }
        list.Sort((a, b) => string.Compare((string)a["name"], (string)b["name"], StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static MacroFile LoadMacro(string name)
    {
        string path = Path.Combine(MacrosDir(), SanitizeName(name) + ".json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MacroFile>(File.ReadAllText(path)); }
        catch { return null; }
    }

    // ---------- Putar ulang ----------

    private static Thread _playThread;
    private static volatile bool _playStop;
    private static string _playName = "";

    public static bool IsPlaying { get { lock (_lock) { return _playThread != null; } } }
    public static string PlayingName { get { lock (_lock) { return _playName; } } }

    /// <summary>Mulai putar ulang di utas sendiri. False bila
    /// sedang merekam/memutar. halted dipanggil berkala —
    /// true = batalkan (Emergency Stop).</summary>
    public static bool Play(MacroFile macro, double speed, Func<bool> halted)
    {
        if (macro == null || macro.Events == null || macro.Events.Count == 0) return false;
        if (speed < 0.25) speed = 0.25;
        if (speed > 4) speed = 4;
        lock (_lock)
        {
            if (_playThread != null || _hookThread != null) return false;
            _playStop = false;
            _playName = macro.Name;
            _playThread = new Thread(() => PlayMain(macro, speed, halted))
            { IsBackground = true, Name = "MacroPlay" };
            _playThread.Start();
        }
        return true;
    }

    private static void PlayMain(MacroFile macro, double speed, Func<bool> halted)
    {
        var heldButtons = new HashSet<string>();
        var heldKeys = new HashSet<ushort>();
        try
        {
            var sw = Stopwatch.StartNew();
            foreach (var ev in macro.Events)
            {
                if (_playStop) break;
                if (halted != null && halted()) break;
                long target = (long)(ev.T / speed);
                // Tidur bertahap agar berhenti terasa segera.
                while (sw.ElapsedMilliseconds < target)
                {
                    if (_playStop) break;
                    if (halted != null && halted()) break;
                    long sisa = target - sw.ElapsedMilliseconds;
                    Thread.Sleep((int)Math.Min(50, Math.Max(1, sisa)));
                }
                if (_playStop) break;
                if (halted != null && halted()) break;
                ExecuteEvent(ev, heldButtons, heldKeys);
            }
        }
        catch { /* putar ulang tidak boleh menjatuhkan aplikasi */ }
        finally
        {
            // Jaring pengaman: tidak ada tombol boleh tertahan.
            foreach (var b in heldButtons) { try { NativeInput.ButtonUp(b); } catch { } }
            foreach (var k in heldKeys) { try { NativeInput.KeyUp(k); } catch { } }
            lock (_lock) { _playThread = null; _playName = ""; }
        }
    }

    private static void ExecuteEvent(MacroEvent ev, HashSet<string> heldButtons, HashSet<ushort> heldKeys)
    {
        switch (ev.Kind)
        {
            case "move": NativeInput.Move(ev.X, ev.Y); break;
            case "ldown": NativeInput.Move(ev.X, ev.Y); NativeInput.ButtonDown("left"); heldButtons.Add("left"); break;
            case "lup": NativeInput.ButtonUp("left"); heldButtons.Remove("left"); break;
            case "rdown": NativeInput.Move(ev.X, ev.Y); NativeInput.ButtonDown("right"); heldButtons.Add("right"); break;
            case "rup": NativeInput.ButtonUp("right"); heldButtons.Remove("right"); break;
            case "mdown": NativeInput.Move(ev.X, ev.Y); NativeInput.ButtonDown("middle"); heldButtons.Add("middle"); break;
            case "mup": NativeInput.ButtonUp("middle"); heldButtons.Remove("middle"); break;
            case "wheel": NativeInput.Scroll(ev.X, ev.Y, ev.Delta); break;
            case "hwheel": NativeInput.ScrollH(ev.X, ev.Y, ev.Delta); break;
            case "keydown": NativeInput.KeyDown((ushort)ev.Vk); heldKeys.Add((ushort)ev.Vk); break;
            case "keyup": NativeInput.KeyUp((ushort)ev.Vk); heldKeys.Remove((ushort)ev.Vk); break;
        }
    }

    /// <summary>Minta putar ulang berhenti (berlaku di
    /// batas tidur berikutnya, maks ±50 ms).</summary>
    public static void StopPlaying()
    {
        _playStop = true;
    }

    /// <summary>Lepas semua: berhenti memutar &amp; berhenti
    /// merekam TANPA menyimpan (dipakai Stop server).</summary>
    public static void Shutdown()
    {
        _playStop = true;
        StopPolling();
        Thread t;
        lock (_lock) { t = _hookThread; }
        if (t != null)
        {
            PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            t.Join(1500);
            lock (_lock)
            {
                if (_hookThread == t) _hookThread = null;
                _events = null; _clock = null; _recName = "";
            }
        }
    }

    // ---------- P/Invoke ----------

    private const int WhMouseLl = 14;
    private const int WhKeyboardLl = 13;
    private const uint WmQuit = 0x0012;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020B;
    private const uint LlmhfInjected = 0x00000001;
    private const uint LlkhfInjected = 0x00000010;

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public PointStruct Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsgStruct
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public PointStruct Pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MsgStruct lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MsgStruct lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MsgStruct lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
