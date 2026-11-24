using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>
/// Kendali mouse & keyboard lewat user32 (SetCursorPos + mouse_event)
/// dan SendKeys. Semua fungsi di sini HARUS dipanggil dari thread UI
/// (AgentServer mengurus itu) karena SendKeys butuh message loop.
/// </summary>
public static class NativeInput
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;

    private static void ButtonFlags(string button, out uint down, out uint up)
    {
        switch ((button ?? "left").Trim().ToLowerInvariant())
        {
            case "right":
                down = RightDown; up = RightUp; break;
            case "middle":
                down = MiddleDown; up = MiddleUp; break;
            default:
                down = LeftDown; up = LeftUp; break;
        }
    }

    // ---------- Daftar & fokus jendela (v2.1) ----------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SwRestore = 9;

    /// <summary>Satu jendela tingkat atas yang terlihat dan berjudul.</summary>
    public sealed class WindowInfo
    {
        public IntPtr Handle;
        public string Title;
        public string Process;
    }

    /// <summary>Daftar semua jendela tingkat atas yang terlihat & berjudul non-kosong.</summary>
    public static List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            string proc = "";
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                proc = Process.GetProcessById((int)pid).ProcessName;
            }
            catch { /* proses sudah mati dsb. — nama proses dikosongkan */ }
            result.Add(new WindowInfo { Handle = hWnd, Title = title, Process = proc });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>
    /// Bawa jendela ke depan. Jendela yang diminimalkan dipulihkan dulu
    /// (SW_RESTORE) sebelum SetForegroundWindow.
    /// </summary>
    public static bool FocusWindow(IntPtr hWnd)
    {
        if (IsIconic(hWnd)) ShowWindow(hWnd, SwRestore);
        return SetForegroundWindow(hWnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const int SwMinimize = 6;
    private const int SwMaximize = 3;
    private const uint WmClose = 0x0010;

    /// <summary>Handle jendela yang sedang di depan (IntPtr.Zero bila tidak ada).</summary>
    public static IntPtr ForegroundHandle()
    {
        return GetForegroundWindow();
    }

    /// <summary>
    /// Aksi ke jendela tertentu (v2.4.0): "close" (WM_CLOSE, jalan
    /// keluar yang sopan — aplikasi masih bisa bertanya simpan),
    /// "minimize", "maximize", "restore". True bila perintah terkirim.
    /// </summary>
    public static bool WindowAction(IntPtr hWnd, string action)
    {
        switch ((action ?? "").Trim().ToLowerInvariant())
        {
            case "close":
                return PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            case "minimize":
                return ShowWindow(hWnd, SwMinimize);
            case "maximize":
                return ShowWindow(hWnd, SwMaximize);
            case "restore":
                return ShowWindow(hWnd, SwRestore);
            default:
                return false;
        }
    }

    public static void Move(int x, int y)
    {
        SetCursorPos(x, y);
    }

    public static void Click(int x, int y, string button)
    {
        SetCursorPos(x, y);
        ButtonFlags(button, out uint down, out uint up);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(45);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>
    /// Klik berulang di titik yang sama (v2.4.0) — count 2 = klik
    /// ganda sejati dengan jeda 70ms (di dalam ambang klik ganda
    /// Windows), count 3 = klik tiga. Pengirim menjepit count 1..3.
    /// </summary>
    public static void ClickTimes(int x, int y, string button, int count)
    {
        if (count < 1) count = 1;
        for (int i = 0; i < count; i++)
        {
            Click(x, y, button);
            if (i < count - 1) Thread.Sleep(70);
        }
    }

    /// <summary>
    /// Scroll roda mouse di titik (x, y). Delta positif = ke atas,
    /// negatif = ke bawah (kelipatan 120 = satu "klik" roda, standar Windows).
    /// </summary>
    public static void Scroll(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        // dwData untuk wheel bertanda (signed) — kirim sebagai uint.
        mouse_event(Wheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    /// <summary>
    /// Seret (drag): tekan tombol di titik awal, gerakkan bertahap
    /// ke titik akhir, lalu lepas. Gerakan dibuat bertahap agar
    /// aplikasi tujuan menerima event drag dengan wajar.
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, string button)
    {
        ButtonFlags(button, out uint down, out uint up);
        SetCursorPos(x1, y1);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            SetCursorPos(x, y);
            Thread.Sleep(15);
        }
        Thread.Sleep(60);
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Mengetik teks biasa; karakter khusus SendKeys di-escape otomatis.</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SendKeys.SendWait(Escape(text));
    }

    // ---------- Ketik Unicode lewat SendInput (v2.4.0) ----------
    // SendKeys menerjemahkan teks lewat tata letak keyboard aktif —
    // karakter di luar tata letak (simbol, huruf beraksen, emoji)
    // bisa hilang/berubah, dan sebagian aplikasi tidak menerimanya
    // dengan stabil. SendInput dengan KEYEVENTF_UNICODE mengirim
    // kode UTF-16 apa adanya, tidak peduli tata letak.

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public uint Type; // 1 = keyboard
        public InputUnion Data;
    }

    // Union INPUT user32: ukurannya ditentukan anggota terbesar
    // (MOUSEINPUT) — tanpa ini sizeof salah dan SendInput menolak.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Ki;
        [FieldOffset(0)] public MouseInput Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, InputEvent[] pInputs, int cbSize);

    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VkReturn = 0x0D;

    private static InputEvent UnicodeEvent(ushort codeUnit, bool keyUp)
    {
        var ev = new InputEvent { Type = 1 };
        ev.Data.Ki = new KeyboardInput
        {
            Vk = 0,
            Scan = codeUnit,
            Flags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0),
        };
        return ev;
    }

    private static InputEvent VkEvent(ushort vk, bool keyUp)
    {
        var ev = new InputEvent { Type = 1 };
        ev.Data.Ki = new KeyboardInput
        {
            Vk = vk,
            Scan = 0,
            Flags = keyUp ? KeyeventfKeyup : 0,
        };
        return ev;
    }

    // ---------- Tahan tombol & metrik sistem (v2.4.0 gelombang 2) ----------

    /// <summary>
    /// Petakan nama tombol ke kode virtual-key: CTRL/CONTROL, SHIFT,
    /// ALT, WIN, ENTER, ESC, TAB, SPACE, arah, F1–F12, atau satu
    /// karakter huruf/angka. 0 bila nama tidak dikenal.
    /// </summary>
    public static ushort VkFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string k = name.Trim().ToUpperInvariant();
        switch (k)
        {
            case "CTRL": case "CONTROL": return 0x11;
            case "SHIFT": return 0x10;
            case "ALT": return 0x12;
            case "WIN": case "WINDOWS": return 0x5B;
            case "ENTER": case "RETURN": return 0x0D;
            case "ESC": case "ESCAPE": return 0x1B;
            case "TAB": return 0x09;
            case "SPACE": return 0x20;
            case "BACKSPACE": case "BKSP": return 0x08;
            case "DELETE": case "DEL": return 0x2E;
            case "UP": return 0x26;
            case "DOWN": return 0x28;
            case "LEFT": return 0x25;
            case "RIGHT": return 0x27;
            case "HOME": return 0x24;
            case "END": return 0x23;
            case "PGUP": case "PAGEUP": return 0x21;
            case "PGDN": case "PAGEDOWN": return 0x22;
        }
        if (k.Length == 2 && k[0] == 'F' && int.TryParse(k.Substring(1), out int f) && f >= 1 && f <= 12)
            return (ushort)(0x70 + f - 1);
        if (k.Length == 1)
        {
            char c = k[0];
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
        }
        return 0;
    }

    /// <summary>Tahan tombol (key-down saja, tanpa lepas).</summary>
    public static void KeyDown(ushort vk)
    {
        var ev = VkEvent(vk, false);
        SendInput(1, new[] { ev }, Marshal.SizeOf<InputEvent>());
    }

    /// <summary>Lepas tombol yang ditahan.</summary>
    public static void KeyUp(ushort vk)
    {
        var ev = VkEvent(vk, true);
        SendInput(1, new[] { ev }, Marshal.SizeOf<InputEvent>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTimeStruct
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTimeStruct idle, out FileTimeStruct kernel, out FileTimeStruct user);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    private static long FileTimeToLong(FileTimeStruct ft)
    {
        return ((long)ft.High << 32) | ft.Low;
    }

    /// <summary>Angka kesehatan PC untuk /sysinfo (v2.4.0 gelombang 2).</summary>
    public sealed class Metrics
    {
        public int CpuPercent;      // -1 bila gagal diukur
        public int RamPercent;      // persen RAM terpakai
        public long RamTotalMb;
        public long DiskFreeGb;     // drive sistem (C:)
        public long DiskTotalGb;
        public int IdleSec;         // detik sejak input terakhir user
    }

    /// <summary>
    /// Ukur metrik sistem. CPU diukur dari dua cuplikan GetSystemTimes
    /// berjarak ±250ms — fungsi ini TIDUR selama itu; panggil dari
    /// thread non-UI.
    /// </summary>
    public static Metrics SystemMetrics()
    {
        var m = new Metrics { CpuPercent = -1, RamPercent = -1, IdleSec = -1 };
        try
        {
            if (GetSystemTimes(out var idle1, out var kern1, out var user1))
            {
                long i1 = FileTimeToLong(idle1);
                long t1 = FileTimeToLong(kern1) + FileTimeToLong(user1);
                Thread.Sleep(250);
                if (GetSystemTimes(out var idle2, out var kern2, out var user2))
                {
                    long idleDelta = FileTimeToLong(idle2) - i1;
                    long totalDelta = (FileTimeToLong(kern2) + FileTimeToLong(user2)) - t1;
                    if (totalDelta > 0)
                        m.CpuPercent = (int)(100 - idleDelta * 100 / totalDelta);
                }
            }
        }
        catch { /* CPU tetap -1 */ }
        try
        {
            var ms = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(ms) && ms.TotalPhys > 0)
            {
                m.RamPercent = (int)ms.MemoryLoad;
                m.RamTotalMb = (long)(ms.TotalPhys / (1024 * 1024));
            }
        }
        catch { /* RAM tetap -1 */ }
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            m.DiskFreeGb = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
            m.DiskTotalGb = drive.TotalSize / (1024L * 1024 * 1024);
        }
        catch { /* disk tetap 0 */ }
        try
        {
            var lii = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref lii))
                m.IdleSec = (int)((Environment.TickCount64 - (long)lii.Time) / 1000);
        }
        catch { /* idle tetap -1 */ }
        return m;
    }

    /// <summary>
    /// Mengetik teks apa adanya lewat SendInput Unicode (v2.4.0).
    /// Baris baru dikirim sebagai tombol Enter sungguhan. Teks
    /// dikirim per potongan ±200 kejadian agar SendInput tidak
    /// menolak rangkaian yang terlalu panjang.
    /// </summary>
    public static void TypeUnicode(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var batch = new List<InputEvent>(256);
        void Flush()
        {
            if (batch.Count == 0) return;
            SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<InputEvent>());
            batch.Clear();
        }
        foreach (char c in text)
        {
            if (c == '\r') continue; // pasangan \r\n cukup satu Enter dari \n
            if (c == '\n')
            {
                batch.Add(VkEvent(VkReturn, false));
                batch.Add(VkEvent(VkReturn, true));
            }
            else
            {
                batch.Add(UnicodeEvent(c, false));
                batch.Add(UnicodeEvent(c, true));
            }
            if (batch.Count >= 200) Flush();
        }
        Flush();
    }

    /// <summary>
    /// Menekan satu tombol khusus ("ENTER", "TAB", "ESC", "F5", ...)
    /// atau kombinasi dalam sintaks SendKeys ("^c" = Ctrl+C, "%{F4}" = Alt+F4).
    /// </summary>
    public static void PressKey(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return;
        string k = keys.Trim();
        if (SpecialKeys.TryGetValue(k, out string token))
            SendKeys.SendWait(token);
        else
            SendKeys.SendWait(k);
    }

    // Nama tombol -> token SendKeys yang harus dikirim.
    private static readonly Dictionary<string, string> SpecialKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENTER", "{ENTER}" },
            { "TAB", "{TAB}" },
            { "ESC", "{ESC}" },
            { "ESCAPE", "{ESC}" },
            { "BACKSPACE", "{BACKSPACE}" },
            { "BKSP", "{BACKSPACE}" },
            { "DELETE", "{DELETE}" },
            { "DEL", "{DELETE}" },
            { "INSERT", "{INSERT}" },
            { "INS", "{INSERT}" },
            { "UP", "{UP}" },
            { "DOWN", "{DOWN}" },
            { "LEFT", "{LEFT}" },
            { "RIGHT", "{RIGHT}" },
            { "HOME", "{HOME}" },
            { "END", "{END}" },
            { "PGUP", "{PGUP}" },
            { "PAGEUP", "{PGUP}" },
            { "PGDN", "{PGDN}" },
            { "PAGEDOWN", "{PGDN}" },
            { "SPACE", " " },
            { "CAPSLOCK", "{CAPSLOCK}" },
            { "NUMLOCK", "{NUMLOCK}" },
            { "SCROLLLOCK", "{SCROLLLOCK}" },
            { "PRTSC", "{PRTSC}" },
            { "F1", "{F1}" }, { "F2", "{F2}" }, { "F3", "{F3}" }, { "F4", "{F4}" },
            { "F5", "{F5}" }, { "F6", "{F6}" }, { "F7", "{F7}" }, { "F8", "{F8}" },
            { "F9", "{F9}" }, { "F10", "{F10}" }, { "F11", "{F11}" }, { "F12", "{F12}" },
        };

    // Karakter yang punya arti khusus di SendKeys dibungkus kurung kurawal,
    // mis. "+" menjadi "{+}". Enter/tab menjadi tombol sungguhan.
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r':
                    break; // pasangan \r\n cukup satu {ENTER} dari \n
                case '\n':
                    sb.Append("{ENTER}");
                    break;
                case '\t':
                    sb.Append("{TAB}");
                    break;
                case '+':
                case '^':
                case '%':
                case '~':
                case '(':
                case ')':
                case '{':
                case '}':
                case '[':
                case ']':
                    sb.Append('{').Append(c).Append('}');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
