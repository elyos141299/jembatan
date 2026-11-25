using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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
        public int Pid;
        // Kotak batas jendela (v2.4.4) — Muse bisa mengarahkan
        // klik tanpa screenshot dulu.
        public Rectangle Rect;
        // Keadaan jendela (v3.8.0 — DSA-04): "normal" | "minimized"
        // | "maximized".
        public string State = "normal";
        // True bila jendela dialog (v3.8.0 — DSA-03): kelas #32770
        // atau gaya WS_EX_DLGMODALFRAME.
        public bool IsDialog;
        // Jendela pemilik (GW_OWNER) — dialog menunjuk induknya
        // (v3.8.0 — DSA-03). IntPtr.Zero bila tidak ada.
        public IntPtr OwnerHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // v3.8.0 — keadaan & identitas jendela: maximized, nama kelas,
    // jendela pemilik, gaya extended (deteksi dialog).
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const int WsExDlgModalFrame = 0x00000001;

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
            // Deteksi dialog (v3.8.0 — DSA-03): kelas #32770 atau
            // gaya modal. Dialog TANPA judul tetap dicatat (titik
            // buta paling berbahaya); jendela biasa tanpa judul
            // tetap dibuang seperti sebelumnya.
            string cls = "";
            try
            {
                var cb = new StringBuilder(256);
                if (GetClassName(hWnd, cb, cb.Capacity) > 0) cls = cb.ToString();
            }
            catch { /* nama kelas hanya keterangan */ }
            int exStyle = 0;
            try { exStyle = GetWindowLong(hWnd, GwlExStyle); } catch { }
            bool isDialog = cls == "#32770" || (exStyle & WsExDlgModalFrame) != 0;
            if (string.IsNullOrWhiteSpace(title) && !isDialog) return true;
            string proc = "";
            uint pidSafe = 0;
            try
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                pidSafe = pid;
                proc = Process.GetProcessById((int)pid).ProcessName;
            }
            catch { /* proses sudah mati dsb. — nama proses dikosongkan */ }
            var rect = Rectangle.Empty;
            try
            {
                if (GetWindowRect(hWnd, out RECT rr) && rr.Right > rr.Left && rr.Bottom > rr.Top)
                    rect = new Rectangle(rr.Left, rr.Top, rr.Right - rr.Left, rr.Bottom - rr.Top);
            }
            catch { /* kotak batas hanya keterangan tambahan */ }
            string wstate = "normal";
            IntPtr owner = IntPtr.Zero;
            try
            {
                if (IsIconic(hWnd)) wstate = "minimized";
                else if (IsZoomed(hWnd)) wstate = "maximized";
                owner = GetWindow(hWnd, GwOwner);
            }
            catch { /* keadaan hanya keterangan tambahan */ }
            result.Add(new WindowInfo
            {
                Handle = hWnd, Title = title, Process = proc, Pid = (int)pidSafe, Rect = rect,
                State = wstate, IsDialog = isDialog, OwnerHandle = owner,
            });
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

    /// <summary>True bila jendela sedang terminimize (v2.7.3).</summary>
    public static bool IsWindowMinimized(IntPtr hWnd)
    {
        return hWnd != IntPtr.Zero && IsIconic(hWnd);
    }

    /// <summary>Handle jendela yang sedang di depan (IntPtr.Zero bila tidak ada).</summary>
    public static IntPtr ForegroundHandle()
    {
        return GetForegroundWindow();
    }

    /// <summary>Judul jendela dari handle (v3.9.0 — untuk callback
    /// WinEventHook di EventLog; ringan, tanpa enumerasi).</summary>
    public static string TitleOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>PID pemilik jendela dari handle (v3.9.0).</summary>
    public static int PidOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return 0;
        try
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return (int)pid;
        }
        catch { return 0; }
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    /// <summary>Atur posisi & ukuran jendela persis angka (v3.5.0 —
    /// Window Manager). Tanpa mengaktifkan jendelanya.</summary>
    public static bool MoveWindowRect(IntPtr hWnd, int x, int y, int w, int h)
    {
        const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
        return SetWindowPos(hWnd, IntPtr.Zero, x, y, w, h, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>True bila Windows menilai jendela tidak merespons
    /// (hang) (v3.5.0 — Desktop State Awareness).</summary>
    public static bool IsHung(IntPtr hWnd)
    {
        try { return IsHungAppWindow(hWnd); } catch { return false; }
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

    /// <summary>Mengetik teks biasa (v5.0.0: SendInput murni,
    /// dulu SendKeys WinForms). Enter/tab menjadi tombol sungguhan,
    /// karakter lain lewat jalur Unicode per karakter.</summary>
    public static void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { TapKey(VkReturn); continue; }
            if (c == '\t') { TapKey(VkTab); continue; }
            TypeUnicode(c.ToString());
        }
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

    /// <summary>Detik sejak masukan terakhir user (untuk pemantau kejadian).</summary>
    public static int IdleSeconds()
    {
        var lii = new LastInputInfo();
        lii.Size = (uint)Marshal.SizeOf(typeof(LastInputInfo));
        if (GetLastInputInfo(ref lii))
            return (int)((Environment.TickCount64 - (long)lii.Time) / 1000);
        return 0;
    }

    // ---------- Jangan tidur (v2.5.0) ----------
    // SetThreadExecutionState dengan ES_CONTINUOUS berlaku dari
    // thread yang memanggilnya sampai diubah lagi; makanya dipakai
    // satu thread khusus berumur panjang yang memasang bendera
    // lalu tidur menunggu sinyal berhenti.

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private static readonly object _awakeLock = new object();
    private static ManualResetEventSlim _awakeStop;

    /// <summary>Keadaan cegah-tidur saat ini.</summary>
    public static bool KeepAwakeOn { get; private set; }

    /// <summary>Pasang / lepas cegah-tidur (sistem + layar).</summary>
    public static void SetKeepAwake(bool on)
    {
        lock (_awakeLock)
        {
            if (on == KeepAwakeOn) return;
            if (on)
            {
                var stop = new ManualResetEventSlim(false);
                _awakeStop = stop;
                var t = new Thread(() =>
                {
                    SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired);
                    stop.Wait();
                    SetThreadExecutionState(EsContinuous);
                })
                { IsBackground = true, Name = "KeepAwake" };
                t.Start();
                KeepAwakeOn = true;
            }
            else
            {
                _awakeStop?.Set();
                _awakeStop = null;
                KeepAwakeOn = false;
            }
        }
    }

    // ---------- Tangkap layar cepat (v2.5.0) ----------
    // StretchBlt langsung dari DC layar ke bitmap tujuan yang
    // sudah berukuran kecil — pengemudi grafis yang mengerjakan
    // pengecilannya, bukan GDI+ piksel per piksel seperti
    // CaptureScaledBitmap lama. Dipakai /stream & /frame.

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const uint Srccopy = 0x00CC0020;

    /// <summary>
    /// Tangkap layar utama langsung pada lebar tujuan (pengecilan
    /// oleh pengemudi grafis). Pemanggil membuang bitmap-nya.
    /// </summary>
    public static Bitmap CaptureScreenScaled(int maxWidth)
    {
        // v2.7.0: jalur DXGI lebih dulu — bingkai langsung dari
        // pipa GPU (permukaan D3D seperti pratinjau OBS & game
        // tertangkap benar, tidak putih/hitam seperti GDI).
        // Tidak tersedia/gagal → jalur StretchBlt lama di bawah.
        try
        {
            // v2.7.3: skala terintegrasi — bitmap langsung terbit
            // pada ukuran tujuan dari tekstur staging, melewati
            // bitmap resolusi penuh + gambar-ulang GDI+ di bawah.
            var langsung = DxgiCapture.TryCaptureScaled(maxWidth);
            if (langsung != null) return langsung;
            var full = DxgiCapture.TryCapture();
            if (full != null)
            {
                var bnd0 = PrimaryBounds();
                int tw0 = Math.Min(maxWidth, bnd0.Width);
                if (tw0 <= 0) tw0 = bnd0.Width;
                int th0 = Math.Max(1, (int)(bnd0.Height * (tw0 / (double)bnd0.Width)));
                if (full.Width == tw0 && full.Height == th0) return full;
                var kecil = new Bitmap(tw0, th0);
                using (var g = Graphics.FromImage(kecil))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.DrawImage(full, 0, 0, tw0, th0);
                }
                full.Dispose();
                return kecil;
            }
        }
        catch { /* jatuh ke GDI */ }
        var bounds = PrimaryBounds();
        int tw = Math.Min(maxWidth, bounds.Width);
        if (tw <= 0) tw = bounds.Width;
        int th = Math.Max(1, (int)(bounds.Height * (tw / (double)bounds.Width)));
        var bmp = new Bitmap(tw, th);
        IntPtr hdcSrc = GetDC(IntPtr.Zero);
        try
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdcDst = g.GetHdc();
                try
                {
                    SetStretchBltMode(hdcDst, 3); // COLORONCOLOR — cepat
                    StretchBlt(hdcDst, 0, 0, tw, th, hdcSrc,
                        bounds.X, bounds.Y, bounds.Width, bounds.Height, Srccopy);
                }
                finally { g.ReleaseHdc(hdcDst); }
            }
        }
        finally { if (hdcSrc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdcSrc); }
        return bmp;
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
        // v2.4.1: SATU karakter per SendInput + jeda 15ms. Semburan
        // ratusan event sekaligus dirusak mesin teks TSF aplikasi
        // modern (terbukti di Notepad Windows 11: sesudah beberapa
        // karakter, sisanya berubah jadi huruf 'i' beruntun — OS
        // menerima semua event, aplikasinya yang kewalahan).
        // Ritme per karakter membuat teks tersulit pun masuk persis
        // (teruji di tempat: —, é, ✓, %, kutip tunggal+ganda).
        foreach (char c in text)
        {
            if (c == '\r') continue; // pasangan \r\n cukup satu Enter dari \n
            InputEvent[] pair = c == '\n'
                ? new[] { VkEvent(VkReturn, false), VkEvent(VkReturn, true) }
                : new[] { UnicodeEvent(c, false), UnicodeEvent(c, true) };
            SendInput(2, pair, Marshal.SizeOf<InputEvent>());
            Thread.Sleep(15);
        }
    }

    /// <summary>
    /// Menekan satu tombol khusus ("ENTER", "TAB", "ESC", "F5", ...)
    /// atau kombinasi dalam sintaks SendKeys ("^c" = Ctrl+C, "%{F4}" = Alt+F4).
    /// v5.0.0: diuraikan sendiri ke SendInput — SendKeys WinForms hilang.
    /// </summary>
    public static void PressKey(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return;
        string k = keys.Trim();
        if (SpecialKeys.TryGetValue(k, out ushort vkOnly))
        {
            TapKey(vkOnly);
            return;
        }
        // v3.6.0 (BUG-05 audit): nama tak dikenal TIDAK lagi diketik
        // literal huruf demi huruf ("ctrl+z" dulu terkirim sebagai
        // teks c-t-r-l...). Yang diterima hanya sintaks SendKeys
        // sungguhan: diawali ^ % ~ + { ( atau tepat satu karakter.
        char c0 = k[0];
        if (k.Length == 1 || c0 == '^' || c0 == '%' || c0 == '~' ||
            c0 == '+' || c0 == '{' || c0 == '(')
        {
            SendKeysSyntax(k);
            return;
        }
        throw new ArgumentException("nama tombol tidak dikenal: " + keys);
    }

    // Pengurai sintaks SendKeys -> SendInput (v5.0.0). Modifier
    // (^ Ctrl, % Alt, + Shift) menempel ke tombol berikutnya, ~
    // berarti Enter, {NAMA} tombol bernama / karakter literal,
    // (grup) menerapkan modifier ke semua karakter di dalamnya.
    private static void SendKeysSyntax(string s)
    {
        var mods = new List<ushort>();
        int i = 0;
        try
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '^') { KeyDown(VkControl); mods.Add(VkControl); i++; continue; }
                if (c == '%') { KeyDown(VkMenu); mods.Add(VkMenu); i++; continue; }
                if (c == '+') { KeyDown(VkShift); mods.Add(VkShift); i++; continue; }
                if (c == '~') { TapKey(VkReturn); i++; continue; }
                if (c == '(')
                {
                    int close = s.IndexOf(')', i);
                    if (close < 0) close = s.Length;
                    for (int j = i + 1; j < close; j++) TapChar(s[j]);
                    i = close + 1;
                    ReleaseMods(mods);
                    continue;
                }
                if (c == '{')
                {
                    int close = s.IndexOf('}', i);
                    if (close < 0) throw new ArgumentException("kurung kurawal tak tertutup: " + s);
                    string name = s.Substring(i + 1, close - i - 1);
                    if (name.Length == 1) TypeUnicode(name);
                    else if (SpecialKeys.TryGetValue(name, out ushort vk)) TapKey(vk);
                    else throw new ArgumentException("nama tombol tidak dikenal: " + name);
                    i = close + 1;
                    ReleaseMods(mods);
                    continue;
                }
                TapChar(c);
                ReleaseMods(mods);
                i++;
            }
        }
        finally { ReleaseMods(mods); }
    }

    private static void ReleaseMods(List<ushort> mods)
    {
        for (int i = mods.Count - 1; i >= 0; i--) KeyUp(mods[i]);
        mods.Clear();
    }

    private static void TapChar(char c)
    {
        short r = VkKeyScanW(c);
        if (r == -1) { TypeUnicode(c.ToString()); return; }
        ushort vk = (ushort)(r & 0xFF);
        int state = (r >> 8) & 0xFF;
        bool shift = (state & 1) != 0, ctrl = (state & 2) != 0, alt = (state & 4) != 0;
        if (shift) KeyDown(VkShift);
        if (ctrl) KeyDown(VkControl);
        if (alt) KeyDown(VkMenu);
        TapKey(vk);
        if (alt) KeyUp(VkMenu);
        if (ctrl) KeyUp(VkControl);
        if (shift) KeyUp(VkShift);
    }

    internal static void TapKey(ushort vk)
    {
        KeyDown(vk);
        KeyUp(vk);
    }

    // Kode tombol virtual untuk pengurai & pengetik.
    private const ushort VkTab = 0x09;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanW(char ch);

    /// <summary>Batas monitor utama dalam piksel fisik (pengganti
    /// Screen.PrimaryScreen.Bounds milik WinForms).</summary>
    public static Rectangle PrimaryBounds()
    {
        int w = GetSystemMetrics(0); // SM_CXSCREEN
        int h = GetSystemMetrics(1); // SM_CYSCREEN
        if (w <= 0 || h <= 0) return new Rectangle(0, 0, 1280, 720);
        return new Rectangle(0, 0, w, h);
    }

    /// <summary>Info satu monitor (pengganti Screen WinForms).</summary>
    public sealed class MonitorInfo
    {
        public string DeviceName = "";
        public Rectangle Bounds;
        public bool Primary;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public RectNative Monitor;
        public RectNative WorkArea;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RectNative rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx info);

    /// <summary>Semua monitor (pengganti Screen.AllScreens).</summary>
    public static List<MonitorInfo> AllScreens()
    {
        var list = new List<MonitorInfo>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, hdc, ref RectNative rect, IntPtr data) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (GetMonitorInfo(hMon, ref info))
                {
                    list.Add(new MonitorInfo
                    {
                        DeviceName = info.Device ?? "",
                        Bounds = new Rectangle(info.Monitor.Left, info.Monitor.Top,
                            info.Monitor.Right - info.Monitor.Left,
                            info.Monitor.Bottom - info.Monitor.Top),
                        Primary = (info.Flags & 1) != 0,
                    });
                }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* jatuh ke bawaan di bawah */ }
        if (list.Count == 0)
            list.Add(new MonitorInfo { DeviceName = "DISPLAY1", Bounds = PrimaryBounds(), Primary = true });
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative pt);

    /// <summary>Posisi kursor (pengganti Cursor.Position WinForms).</summary>
    public static Point CursorPosition()
    {
        return GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : new Point(0, 0);
    }

    // Nama tombol -> kode virtual (v5.0.0; dulu token SendKeys).
    private static readonly Dictionary<string, ushort> SpecialKeys =
        new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "ENTER", 0x0D },
            { "TAB", 0x09 },
            { "ESC", 0x1B },
            { "ESCAPE", 0x1B },
            { "BACKSPACE", 0x08 },
            { "BKSP", 0x08 },
            { "DELETE", 0x2E },
            { "DEL", 0x2E },
            { "INSERT", 0x2D },
            { "INS", 0x2D },
            { "UP", 0x26 },
            { "DOWN", 0x28 },
            { "LEFT", 0x25 },
            { "RIGHT", 0x27 },
            { "HOME", 0x24 },
            { "END", 0x23 },
            { "PGUP", 0x21 },
            { "PAGEUP", 0x21 },
            { "PGDN", 0x22 },
            { "PAGEDOWN", 0x22 },
            { "SPACE", 0x20 },
            { "CAPSLOCK", 0x14 },
            { "NUMLOCK", 0x90 },
            { "SCROLLLOCK", 0x91 },
            { "PRTSC", 0x2C },
            { "F1", 0x70 }, { "F2", 0x71 }, { "F3", 0x72 }, { "F4", 0x73 },
            { "F5", 0x74 }, { "F6", 0x75 }, { "F7", 0x76 }, { "F8", 0x77 },
            { "F9", 0x78 }, { "F10", 0x79 }, { "F11", 0x7A }, { "F12", 0x7B },
        };
}
