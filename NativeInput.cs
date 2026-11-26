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

    // ---------- Gerakan SendInput absolut (v5.11.0 — Bagian A.1) ----------
    // SetCursorPos adalah "warga kelas dua": mesin tinta
    // Paint/Krita tidak menggambar setia darinya, perekam
    // makro buta terhadapnya, dan sebagian aplikasi tidak
    // mengakuinya sebagai gerakan (bukti audit 26 Nov +
    // latihan gambar Krita). Semua gerakan kini lewat
    // SendInput absolut (MOUSEEVENTF_ABSOLUTE|VIRTUALDESK) —
    // gerakan pointer ASLI yang diakui semua jalur itu.
    // SetCursorPos hanya tinggal sebagai jalan kembali bila
    // SendInput gagal total.

    private const uint MouseEventfMove = 0x0001;
    private const uint MouseEventfAbsolute = 0x8000;
    private const uint MouseEventfVirtualDesk = 0x4000;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Pindahkan kursor ke (x, y) sebagai gerakan
    /// pointer asli lewat SendInput absolut di seluruh
    /// desktop virtual (koordinat ternormalisasi 0..65535).</summary>
    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
        int vy = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
        int vw = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
        int vh = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
        if (vw <= 1 || vh <= 1) { SetCursorPos(x, y); return; }
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        var ev = new InputEvent { Type = 0 };
        ev.Data.Mi = new MouseInput
        {
            Dx = nx,
            Dy = ny,
            MouseData = 0,
            Flags = MouseEventfMove | MouseEventfAbsolute | MouseEventfVirtualDesk,
            Time = 0,
            ExtraInfo = UIntPtr.Zero,
        };
        if (SendInput(1, new[] { ev }, Marshal.SizeOf<InputEvent>()) == 0)
            SetCursorPos(x, y);
    }

    public static void Move(int x, int y) => MoveTo(x, y);

    /// <summary>
    /// Lepas tombol mouse DENGAN verifikasi (v5.11.0): sesudah
    /// event lepas dikirim, keadaan tombol diperiksa lewat
    /// GetAsyncKeyState; bila masih terbaca tertahan, event
    /// lepas dikirim ulang (maks 3×). Obat buat artefak
    /// "pena belum terangkat" (garis liar antar goresan di
    /// Krita, 26 Nov) — perpindahan kursor sesudah ini aman.
    /// </summary>
    public static void ReleaseButton(string button)
    {
        ButtonFlags(button, out uint _, out uint up);
        int vk = ((button ?? "left").Trim().ToLowerInvariant()) switch
        {
            "right" => 0x02,   // VK_RBUTTON
            "middle" => 0x04,  // VK_MBUTTON
            _ => 0x01,         // VK_LBUTTON
        };
        mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        for (int i = 0; i < 3; i++)
        {
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) return;
            Thread.Sleep(10);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
        }
    }

    // ---------- Pena sintetis Windows Ink (v5.11.0 — Bagian B.9, PROTOTIPE) ----------
    // Tekanan kuas tidak bisa dikirim SendInput/mouse_event.
    // Jalur resminya: perangkat pena sintetis
    // (CreateSyntheticPointerDevice + InjectSyntheticPointerInput,
    // Windows 10 1809+) dengan POINTER_PEN_INFO bertekanan
    // 0..1024. Aplikasi tujuan harus memakai API tablet
    // "Windows 8+ Pointer Input (Windows Ink)" — di Krita:
    // Settings > Configure Krita > Tablet Settings.
    // Status: prototipe bergerbang tes goresan meruncing.

    private const uint PtPen = 3; // PT_PEN
    private const uint PointerFeedbackDefault = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerInfo
    {
        public uint PointerType;
        public uint PointerId;
        public uint FrameId;
        public uint PointerFlags;
        public IntPtr SourceDevice;
        public IntPtr HwndTarget;
        public PointNative PtPixelLocation;
        public PointNative PtHimetricLocation;
        public PointNative PtPixelLocationRaw;
        public PointNative PtHimetricLocationRaw;
        public uint DwTime;
        public uint HistoryCount;
        public int InputData;
        public uint DwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointerPenInfo
    {
        public PointerInfo PointerInfo;
        public uint PenFlags;
        public uint PenMask;
        public uint Pressure;
        public uint Rotation;
        public int TiltX;
        public int TiltY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint activeMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, PointerPenInfo[] pointerInfos, uint count);

    private static IntPtr _penDevice = IntPtr.Zero;

    private const uint PenFlagNone = 0;
    private const uint PenMaskPressure = 0x00000001;
    private const uint PfNew = 0x00000001;
    private const uint PfInRange = 0x00000002;
    private const uint PfInContact = 0x00000004;
    private const uint PfFirstButton = 0x00000010;
    private const uint PfPrimary = 0x00002000;
    private const uint PfDown = 0x00010000;
    private const uint PfUpdate = 0x00020000;
    private const uint PfUp = 0x00040000;

    /// <summary>
    /// Satu goresan pena bertekanan menyusuri titik-titik
    /// (x, y, pressure 0..1024). stepMs = jeda antar titik.
    /// False + error bila perangkat sintetis gagal dibuat
    /// atau suntikan ditolak sistem.
    /// </summary>
    private static uint _penFrameId;

    private static bool InjectPenFrame(uint flags, int x, int y, uint pressure)
    {
        var info = new PointerPenInfo
        {
            PointerInfo = new PointerInfo
            {
                PointerType = PtPen,
                PointerId = 1,
                FrameId = ++_penFrameId,
                PointerFlags = flags,
                PtPixelLocation = new PointNative { X = x, Y = y },
                PtPixelLocationRaw = new PointNative { X = x, Y = y },
                DwTime = (uint)(Environment.TickCount64 & 0xFFFFFFFF),
                HistoryCount = 1,
            },
            PenFlags = PenFlagNone,
            PenMask = PenMaskPressure,
            Pressure = pressure,
            Rotation = 0,
            TiltX = 0,
            TiltY = 0,
        };
        return InjectSyntheticPointerInput(_penDevice, new[] { info }, 1);
    }

    public static bool PenStroke(IReadOnlyList<(int X, int Y, int Pressure)> pts, int stepMs, out string error)
    {
        error = "";
        if (pts == null || pts.Count < 2) { error = "goresan pena butuh minimal 2 titik"; return false; }
        if (stepMs < 0) stepMs = 0;
        if (stepMs > 100) stepMs = 100;
        if (_penDevice == IntPtr.Zero)
        {
            _penDevice = CreateSyntheticPointerDevice(PtPen, 1, PointerFeedbackDefault);
            if (_penDevice == IntPtr.Zero)
            {
                error = "CreateSyntheticPointerDevice gagal (err " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
        }
        // v5.11.1: bingkai HOVER pembuka di titik pertama
        // (pena "mendekat" dulu sebelum menyentuh) + cap
        // waktu & nomor bingkai yang hidup di semua
        // bingkai — di v5.11.0 keduanya nol sehingga
        // Krita menggabung seluruh goresan jadi satu
        // kejadian (palet pop-up + satu titik liar).
        var first = pts[0];
        for (int h = 0; h < 2; h++)
        {
            if (!InjectPenFrame(PfInRange | PfPrimary | PfUpdate, first.X, first.Y, 0))
            {
                error = "InjectSyntheticPointerInput gagal di hover pembuka (err " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            if (stepMs > 0) Thread.Sleep(stepMs);
        }
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            int pressure = p.Pressure < 0 ? 0 : p.Pressure > 1024 ? 1024 : p.Pressure;
            uint flags;
            if (i == 0) flags = PfNew | PfInRange | PfInContact | PfFirstButton | PfPrimary | PfDown;
            else if (i == pts.Count - 1) flags = PfInRange | PfPrimary | PfUp;
            else flags = PfInRange | PfInContact | PfFirstButton | PfPrimary | PfUpdate;
            uint pr = i == pts.Count - 1 ? 0u : (uint)pressure;
            if (!InjectPenFrame(flags, p.X, p.Y, pr))
            {
                error = "InjectSyntheticPointerInput gagal di titik " + i + " (err " + Marshal.GetLastWin32Error() + ")";
                return false;
            }
            if (stepMs > 0 && i < pts.Count - 1) Thread.Sleep(stepMs);
        }
        // Bingkai hover penutup: pena terangkat & menjauh.
        var lastPt = pts[pts.Count - 1];
        InjectPenFrame(PfInRange | PfPrimary | PfUpdate, lastPt.X, lastPt.Y, 0);
        return true;
    }

    /// <summary>
    /// Gerak meluncur (v5.10.0): kursor menyusuri garis lurus
    /// dari posisi sekarang ke (x, y) dalam glideMs dengan
    /// perlambatan halus di ujung (ease-in-out) — untuk
    /// aplikasi/game yang tidak mengakui teleport SetCursorPos
    /// sebagai gerakan. glideMs &lt;= 0 = teleport biasa.
    /// </summary>
    public static void GlideTo(int x, int y, int glideMs)
    {
        if (glideMs <= 0) { MoveTo(x, y); return; }
        if (glideMs > 10000) glideMs = 10000;
        var from = CursorPosition();
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double t = Math.Min(1.0, (double)sw.ElapsedMilliseconds / glideMs);
            double e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            int cx = from.X + (int)Math.Round((x - from.X) * e);
            int cy = from.Y + (int)Math.Round((y - from.Y) * e);
            MoveTo(cx, cy);
            if (t >= 1.0) break;
            Thread.Sleep(12);
        }
    }

    public static void Click(int x, int y, string button)
    {
        MoveTo(x, y);
        ButtonFlags(button, out uint down, out uint _);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(45);
        ReleaseButton(button);
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
        MoveTo(x, y);
        // dwData untuk wheel bertanda (signed) — kirim sebagai uint.
        mouse_event(Wheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    private const uint HWheel = 0x01000;

    /// <summary>Scroll MENYAMPING di titik (x, y) (v5.10.0).
    /// Delta positif = ke kanan, negatif = ke kiri.</summary>
    public static void ScrollH(int x, int y, int delta)
    {
        MoveTo(x, y);
        mouse_event(HWheel, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }

    /// <summary>
    /// Scroll halus (v5.10.0): delta total dipecah menjadi
    /// langkah ±40 per 15 ms — permukaan yang mendukung scroll
    /// piksel (kanvas, peramban) bergerak mulus alih-alih
    /// meloncat per klik roda. horizontal memilih sumbu.
    /// </summary>
    public static void ScrollSmooth(int x, int y, int delta, bool horizontal)
    {
        MoveTo(x, y);
        uint flag = horizontal ? HWheel : Wheel;
        int sisa = delta;
        while (sisa != 0)
        {
            int langkah = Math.Max(-40, Math.Min(40, sisa));
            mouse_event(flag, 0, 0, unchecked((uint)langkah), UIntPtr.Zero);
            sisa -= langkah;
            if (sisa != 0) Thread.Sleep(15);
        }
    }

    /// <summary>Tekan tombol mouse di posisi kursor saat ini
    /// TANPA melepasnya (v5.9.0 — pasangan ButtonUp; untuk
    /// tekan-tahan &amp; seret manual dua tahap).</summary>
    public static void ButtonDown(string button)
    {
        ButtonFlags(button, out uint down, out uint _);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>Lepas tombol mouse yang sedang tertahan (v5.9.0;
    /// v5.11.0: kini lewat ReleaseButton terverifikasi).</summary>
    public static void ButtonUp(string button)
    {
        ReleaseButton(button);
    }

    /// <summary>Klik tekan-tahan (v5.9.0): tekan di titik,
    /// tahan holdMs, lalu lepas — long-press dalam SATU aksi
    /// lokal sehingga timing-nya tidak dirusak latensi relay.</summary>
    public static void ClickHold(int x, int y, string button, int holdMs)
    {
        if (holdMs < 0) holdMs = 0;
        if (holdMs > 10000) holdMs = 10000;
        MoveTo(x, y);
        ButtonDown(button);
        if (holdMs > 0) Thread.Sleep(holdMs);
        ButtonUp(button);
    }

    /// <summary>
    /// Seret (drag): tekan tombol di titik awal, gerakkan bertahap
    /// ke titik akhir, lalu lepas. Gerakan dibuat bertahap agar
    /// aplikasi tujuan menerima event drag dengan wajar.
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, string button)
        => Drag(x1, y1, x2, y2, button, 60, 16, 15, 60);

    /// <summary>
    /// Seret dengan timing yang bisa diatur (v5.9.0): grabMs =
    /// tahan di titik awal sebelum bergerak (target sensitif
    /// seperti node Figma butuh genggaman mantap), steps/stepMs
    /// = kehalusan lintasan, settleMs = tahan di tujuan sebelum
    /// lepas. Bawaan memanggil versi lama persis (60/16/15/60).
    /// </summary>
    public static void Drag(int x1, int y1, int x2, int y2, string button,
        int grabMs, int steps, int stepMs, int settleMs)
    {
        if (grabMs < 0) grabMs = 0;
        if (grabMs > 2000) grabMs = 2000;
        if (steps < 1) steps = 1;
        if (steps > 120) steps = 120;
        if (stepMs < 0) stepMs = 0;
        if (stepMs > 100) stepMs = 100;
        if (settleMs < 0) settleMs = 0;
        if (settleMs > 2000) settleMs = 2000;
        ButtonFlags(button, out uint down, out uint _);
        MoveTo(x1, y1);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        if (grabMs > 0) Thread.Sleep(grabMs);
        for (int i = 1; i <= steps; i++)
        {
            int x = x1 + (x2 - x1) * i / steps;
            int y = y1 + (y2 - y1) * i / steps;
            MoveTo(x, y);
            if (stepMs > 0) Thread.Sleep(stepMs);
        }
        if (settleMs > 0) Thread.Sleep(settleMs);
        ReleaseButton(button);
    }

    /// <summary>
    /// Seret menyusuri JALUR banyak titik (v5.10.0 — waypoints):
    /// tekan di titik pertama, berhenti sejenak (DwellMs) di
    /// tiap titik singgah, lepas di titik terakhir sesudah
    /// settleMs. Gerakan antar titik dipecah langkah ±12px
    /// berjeda stepMs. Untuk lintasan melengkung, slider yang
    /// butuh singgah, dan pola seret yang tidak lurus.
    /// </summary>
    public static void DragPath(IReadOnlyList<(int X, int Y, int DwellMs)> path,
        string button, int grabMs, int stepMs, int settleMs)
    {
        if (path == null || path.Count < 2)
            throw new ArgumentException("jalur seret butuh minimal 2 titik");
        if (grabMs < 0) grabMs = 0;
        if (grabMs > 2000) grabMs = 2000;
        if (stepMs < 0) stepMs = 0;
        if (stepMs > 100) stepMs = 100;
        if (settleMs < 0) settleMs = 0;
        if (settleMs > 2000) settleMs = 2000;
        ButtonFlags(button, out uint down, out uint _);
        var start = path[0];
        MoveTo(start.X, start.Y);
        mouse_event(down, 0, 0, 0, UIntPtr.Zero);
        if (grabMs > 0) Thread.Sleep(grabMs);
        int cx = start.X, cy = start.Y;
        for (int p = 1; p < path.Count; p++)
        {
            var target = path[p];
            int dx = target.X - cx, dy = target.Y - cy;
            int dist = (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy));
            // Kecepatan adaptif (v5.11.0 — Bagian B.11): ukuran
            // langkah mengikuti ketajaman belokan DI titik
            // tujuan segmen ini. Lurus = langkah 16px (lebih
            // sedikit jeda, lebih cepat); belokan tajam =
            // langkah 6px (rapat & pelan, tinta setia). Sudut
            // dihitung dari arah masuk vs arah keluar titik.
            double sudut = 0;
            if (p + 1 < path.Count)
            {
                var next = path[p + 1];
                double ax = target.X - cx, ay = target.Y - cy;
                double bx = next.X - target.X, by = next.Y - target.Y;
                double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
                if (la > 0 && lb > 0)
                {
                    double cos = (ax * bx + ay * by) / (la * lb);
                    if (cos < -1) cos = -1; if (cos > 1) cos = 1;
                    sudut = Math.Acos(cos) * 180.0 / Math.PI;
                }
            }
            int stepSize = sudut >= 60 ? 6 : sudut >= 25 ? 9 : 16;
            int steps = Math.Max(2, (int)Math.Ceiling((double)dist / stepSize));
            for (int i = 1; i <= steps; i++)
            {
                MoveTo(cx + dx * i / steps, cy + dy * i / steps);
                if (stepMs > 0) Thread.Sleep(stepMs);
            }
            cx = target.X; cy = target.Y;
            int dwell = target.DwellMs;
            if (dwell < 0) dwell = 0;
            if (dwell > 5000) dwell = 5000;
            if (dwell > 0) Thread.Sleep(dwell);
        }
        if (settleMs > 0) Thread.Sleep(settleMs);
        ReleaseButton(button);
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
            // v5.11.0 (Bagian A.6): nama tombol dilengkapi —
            // sisip, kunci-kunci khusus, numpad, media & volume.
            case "INSERT": case "INS": return 0x2D;
            case "PRINTSCREEN": case "PRTSC": case "PRTSCR": return 0x2C;
            case "SCROLLLOCK": case "SCROLL": return 0x91;
            case "PAUSE": case "BREAK": return 0x13;
            case "CAPSLOCK": case "CAPS": return 0x14;
            case "NUMLOCK": return 0x90;
            case "MENU": case "APPS": return 0x5D;
            case "MULTIPLY": case "NUMPAD*": return 0x6A;
            case "ADD": case "NUMPAD+": return 0x6B;
            case "SUBTRACT": case "NUMPAD-": return 0x6D;
            case "DECIMAL": case "NUMPAD.": return 0x6E;
            case "DIVIDE": case "NUMPAD/": return 0x6F;
            case "MEDIA_NEXT": case "MEDIANEXT": case "NEXT_TRACK": case "NEXTTRACK": return 0xB0;
            case "MEDIA_PREV": case "MEDIAPREV": case "PREV_TRACK": case "PREVTRACK": return 0xB1;
            case "MEDIA_STOP": case "MEDIASTOP": return 0xB2;
            case "MEDIA_PLAYPAUSE": case "PLAYPAUSE": case "PLAY_PAUSE": return 0xB3;
            case "VOL_MUTE": case "VOLMUTE": case "VOLUME_MUTE": case "MUTE": return 0xAD;
            case "VOL_DOWN": case "VOLDOWN": case "VOLUME_DOWN": return 0xAE;
            case "VOL_UP": case "VOLUP": case "VOLUME_UP": return 0xAF;
        }
        if (k.StartsWith("NUMPAD") && k.Length == 7 && k[6] >= '0' && k[6] <= '9')
            return (ushort)(0x60 + (k[6] - '0'));
        if (k.Length >= 2 && k.Length <= 3 && k[0] == 'F' && int.TryParse(k.Substring(1), out int f) && f >= 1 && f <= 24)
            return (ushort)(f <= 12 ? 0x70 + f - 1 : 0x7C + f - 13);
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

    // ---------- Mode game: kode pindai / scan code (v5.10.0) ----------
    // Kejadian VK murni (Scan=0) diterjemahkan Windows, tetapi
    // game yang membaca DirectInput/Raw Input melihat kode
    // pindai fisiknya — VK suntikan tanpa scan code bisa cuek
    // saja bagi mereka. Mode ini mengirim KEYEVENTF_SCANCODE
    // dengan kode dari MapVirtualKey(VK_TO_VSC_EX), persis
    // seperti keyboard fisik, termasuk bendera extended untuk
    // tombol arah/Home/dsb.

    private const uint KeyeventfExtendedKey = 0x0001;
    private const uint KeyeventfScancode = 0x0008;
    private const uint MapVkToVscEx = 4;

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    private static InputEvent ScanEvent(ushort vk, bool keyUp)
    {
        uint mapped = MapVirtualKeyW(vk, MapVkToVscEx);
        ushort scan = (ushort)(mapped & 0xFF);
        uint flags = KeyeventfScancode | (keyUp ? KeyeventfKeyup : 0);
        if (((mapped >> 8) & 0xFF) != 0) flags |= KeyeventfExtendedKey;
        var ev = new InputEvent { Type = 1 };
        ev.Data.Ki = new KeyboardInput { Vk = 0, Scan = scan, Flags = flags };
        return ev;
    }

    /// <summary>Tahan tombol dalam mode scan code (v5.10.0).</summary>
    public static void KeyDownScan(ushort vk)
    {
        var ev = ScanEvent(vk, false);
        SendInput(1, new[] { ev }, Marshal.SizeOf<InputEvent>());
    }

    /// <summary>Lepas tombol mode scan code (v5.10.0).</summary>
    public static void KeyUpScan(ushort vk)
    {
        var ev = ScanEvent(vk, true);
        SendInput(1, new[] { ev }, Marshal.SizeOf<InputEvent>());
    }

    /// <summary>Tekan-lepas satu tombol mode scan code.
    /// v5.11.0 (Bagian A.5): jeda tahan dari _tapHoldMs —
    /// ketukan tanpa jeda bisa hilang di game polling.</summary>
    public static void TapKeyScan(ushort vk)
    {
        KeyDownScan(vk);
        if (_tapHoldMs > 0) Thread.Sleep(_tapHoldMs);
        KeyUpScan(vk);
    }

    // Jeda tahan ketukan (v5.11.0 — Bagian A.5): dipasang
    // PressKey/PressKeyScan dari bidang holdMs selama satu
    // panggilan. Semua jalur ini berjalan serial di utas UI,
    // jadi satu bidang bersama aman. 0 = perilaku lama persis.
    private static int _tapHoldMs;

    private static int ClampHold(int holdMs)
    {
        if (holdMs < 0) return 0;
        if (holdMs > 5000) return 5000;
        return holdMs;
    }

    /// <summary>
    /// Tekan tombol/kombinasi mode scan code (v5.10.0). Sintaks
    /// SENGAJA berbeda dari PressKey: nama-nama tombol digabung
    /// "+" (mis. "ctrl+shift+esc", "alt+f4", "w") — semua
    /// kecuali terakhir ditahan, terakhir diketuk, lalu semua
    /// dilepas. Nama dari VkFromName / daftar tombol khusus.
    /// </summary>
    public static void PressKeyScan(string keys) => PressKeyScan(keys, 0);

    /// <summary>PressKeyScan dengan jeda tahan (v5.11.0):
    /// holdMs menahan tombol terakhir sebelum dilepas.</summary>
    public static void PressKeyScan(string keys, int holdMs)
    {
        if (string.IsNullOrWhiteSpace(keys)) return;
        var vks = new List<ushort>();
        foreach (string part in keys.Split('+'))
        {
            string p = part.Trim();
            if (p.Length == 0) continue;
            ushort vk = VkFromName(p);
            if (vk == 0 && SpecialKeys.TryGetValue(p, out ushort sp)) vk = sp;
            if (vk == 0) throw new ArgumentException("nama tombol tidak dikenal: " + p);
            vks.Add(vk);
        }
        if (vks.Count == 0) return;
        int prevHold = _tapHoldMs;
        _tapHoldMs = ClampHold(holdMs);
        try
        {
            for (int i = 0; i < vks.Count - 1; i++) KeyDownScan(vks[i]);
            TapKeyScan(vks[vks.Count - 1]);
        }
        finally
        {
            for (int i = vks.Count - 2; i >= 0; i--) KeyUpScan(vks[i]);
            _tapHoldMs = prevHold;
        }
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

    // Selisih idle yang AMAN dari luapan tick 32-bit
    // (v5.11.0 — Bagian A.8): LastInputInfo.Time adalah
    // DWORD (membungkus tiap 49,7 hari) sedangkan
    // TickCount64 terus naik — pengurangan langsung
    // menghasilkan angka raksasa sesudah luapan. Selisih
    // dihitung di ruang uint 32-bit (ikut membungkus),
    // benar untuk idle < 49,7 hari. -1 = gagal membaca.
    private static long IdleDiffMs()
    {
        var lii = new LastInputInfo();
        lii.Size = (uint)Marshal.SizeOf(typeof(LastInputInfo));
        if (!GetLastInputInfo(ref lii)) return -1;
        uint now = unchecked((uint)Environment.TickCount64);
        return unchecked(now - lii.Time);
    }

    /// <summary>Detik sejak masukan terakhir user (untuk pemantau kejadian).</summary>
    public static int IdleSeconds()
    {
        long d = IdleDiffMs();
        return d < 0 ? 0 : (int)(d / 1000);
    }

    /// <summary>Milidetik sejak masukan terakhir user (v5.9.0 —
    /// resolusi halus untuk gerbang arbitrase manusia di /batch).
    /// Gagal membaca = dianggap sangat lama (tidak menghalangi).</summary>
    public static long IdleMilliseconds()
    {
        long d = IdleDiffMs();
        return d < 0 ? long.MaxValue : d;
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
    public static void TypeUnicode(string text) => TypeUnicode(text, 15);

    /// <summary>
    /// Sama seperti TypeUnicode klasik, dengan jeda per
    /// karakter yang bisa diatur (v5.10.0): rateMs 0 = secepat
    /// SendInput sanggup (berisiko di aplikasi TSF rewel —
    /// pilihan pemanggil), 15 = ritme aman bawaan.
    /// </summary>
    public static void TypeUnicode(string text, int rateMs)
    {
        if (rateMs < 0) rateMs = 0;
        if (rateMs > 50) rateMs = 50;
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
            if (rateMs > 0) Thread.Sleep(rateMs);
        }
    }

    /// <summary>
    /// Menekan satu tombol khusus ("ENTER", "TAB", "ESC", "F5", ...)
    /// atau kombinasi dalam sintaks SendKeys ("^c" = Ctrl+C, "%{F4}" = Alt+F4).
    /// v5.0.0: diuraikan sendiri ke SendInput — SendKeys WinForms hilang.
    /// </summary>
    public static void PressKey(string keys) => PressKey(keys, 0);

    /// <summary>PressKey dengan jeda tahan (v5.11.0 — Bagian
    /// A.5): holdMs menahan tiap ketukan sebelum dilepas,
    /// untuk aplikasi/game polling yang kehilangan ketukan
    /// sekejap. 0 = perilaku lama persis.</summary>
    public static void PressKey(string keys, int holdMs)
    {
        if (string.IsNullOrWhiteSpace(keys)) return;
        int prevHold = _tapHoldMs;
        _tapHoldMs = ClampHold(holdMs);
        try { PressKeyInti(keys); }
        finally { _tapHoldMs = prevHold; }
    }

    private static void PressKeyInti(string keys)
    {
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
        if (_tapHoldMs > 0) Thread.Sleep(_tapHoldMs);
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
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RectNative rect, IntPtr data) =>
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
