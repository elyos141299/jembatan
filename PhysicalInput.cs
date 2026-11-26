using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jembatan;

/// <summary>
/// Sensor sentuhan FISIK user (v5.13.0 — Arbitrase): hook
/// low-level mouse &amp; keyboard yang membedakan kejadian
/// FISIK (tangan user di perangkat beneran) dari kejadian
/// INJECTED (suntikan SendInput — MuseLink sendiri, AHK,
/// SendKeys, dsb. — semuanya membawa flag INJECTED).
///
/// Fondasi dua fitur: (1) YIELD — operasi input panjang
/// berhenti seketika saat user mengambil alih; (2) penjaga
/// idle rutinitas — "user aktif" diukur dari sentuhan fisik,
/// bukan dari aktivitas suntikan agent sendiri.
///
/// Callback dibuat sangat ramping (catat cap waktu, teruskan
/// rantai) karena hook low-level yang lambat akan diputus
/// Windows. Hook dipasang di thread UI (ber-message-loop)
/// lewat IUiHost, pola yang sama dengan EventLog.StartHooks.
/// </summary>
internal static class PhysicalInput
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const uint LlmhfInjected = 0x00000001;
    private const uint LlmhfLowerIlInjected = 0x00000002;
    private const uint LlkhfInjected = 0x00000010;
    private const uint LlkhfLowerIlInjected = 0x00000002;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHook
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbLlHook
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private static HookProc _mouseProc; // disimpan agar tidak di-GC
    private static HookProc _kbProc;
    private static IntPtr _mouseHook;
    private static IntPtr _kbHook;
    private static volatile bool _started;
    private static long _lastTick;
    private static long _count;
    private static string _lastKind = "";

    /// <summary>Cap TickCount64 sentuhan fisik terakhir (0 = belum pernah).</summary>
    public static long LastTick => Interlocked.Read(ref _lastTick);

    /// <summary>Jumlah kejadian fisik sejak aplikasi mulai.</summary>
    public static long Count => Interlocked.Read(ref _count);

    /// <summary>"mouse" | "keyboard" | "" — jenis sentuhan fisik terakhir.</summary>
    public static string LastKind => _lastKind;

    /// <summary>Detik sejak sentuhan fisik terakhir; -1 bila belum pernah.</summary>
    public static long AgoSec
    {
        get
        {
            long t = LastTick;
            return t == 0 ? -1 : (Environment.TickCount64 - t) / 1000;
        }
    }

    /// <summary>True bila user menyentuh perangkat fisik sesudah cap ini.</summary>
    public static bool TouchedSince(long tick) => LastTick > tick;

    /// <summary>Pasang kedua hook di thread UI (idempoten).</summary>
    public static void StartOnUi(IUiHost ui)
    {
        if (ui == null || _started) return;
        _started = true;
        try
        {
            ui.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (_mouseProc == null) _mouseProc = MouseCallback;
                    if (_kbProc == null) _kbProc = KbCallback;
                    IntPtr mod = GetModuleHandle(null);
                    if (_mouseHook == IntPtr.Zero)
                        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, mod, 0);
                    if (_kbHook == IntPtr.Zero)
                        _kbHook = SetWindowsHookEx(WhKeyboardLl, _kbProc, mod, 0);
                }
                catch { /* sensor gagal pasang tidak menjatuhkan aplikasi */ }
            }));
        }
        catch { /* abaikan */ }
    }

    private static void Mark(string kind)
    {
        Interlocked.Exchange(ref _lastTick, Environment.TickCount64);
        Interlocked.Increment(ref _count);
        _lastKind = kind;
    }

    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var s = Marshal.PtrToStructure<MsLlHook>(lParam);
                if ((s.Flags & (LlmhfInjected | LlmhfLowerIlInjected)) == 0)
                    Mark("mouse");
            }
            catch { /* jangan pernah melempar dari callback hook */ }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var s = Marshal.PtrToStructure<KbLlHook>(lParam);
                if ((s.Flags & (LlkhfInjected | LlkhfLowerIlInjected)) == 0)
                    Mark("keyboard");
            }
            catch { /* jangan pernah melempar dari callback hook */ }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }
}
