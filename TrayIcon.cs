using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Jembatan;

/// <summary>
/// Ikon tray MuseLink lewat Win32 Shell_NotifyIcon langsung
/// (tanpa WinForms): klik ganda membuka jendela, klik kanan menu
/// asli (Buka / Putuskan-Sambungkan / Emergency Stop / tiga
/// saklar pengaturan bercentang / Keluar). Sejak v5.8.0 saklar
/// pengaturan tinggal di sini — jendela kembali sepolos v1.
/// Pesan tray diterima lewat HwndSource tersembunyi milik utas UI.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;

    private const int WmTrayCallback = 0x8001; // WM_APP + 1
    private const int WmLButtonDblClk = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmReturnCmd = 0x0100;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint MfChecked = 0x0008;
    private const uint MfGrayed = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UID;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint flags, UIntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    private readonly HwndSource _sink;
    private IntPtr _iconHandle;
    private bool _added;

    public event Action OpenRequested;
    public event Action ToggleServerRequested;
    public event Action ToggleStopRequested;
    public event Action ToggleAutoStartRequested;
    public event Action ToggleAdminModeRequested;
    public event Action ToggleKeepAwakeRequested;
    public event Action ExitRequested;

    /// <summary>Keadaan terkini untuk label menu: (bridgeJalan, haltAktif).</summary>
    public Func<(bool running, bool halted)> StateProvider;

    /// <summary>Keadaan tiga saklar pengaturan untuk tanda
    /// centang menu: (jalanOtomatis, modeAdmin, cegahTidur).</summary>
    public Func<(bool autoStart, bool adminMode, bool keepAwake)> SettingsProvider;

    public TrayIcon()
    {
        var par = new HwndSourceParameters("JembatanTraySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _sink = new HwndSource(par);
        _sink.AddHook(WndProc);
        AddIcon();
    }

    private void AddIcon()
    {
        try
        {
            string exe = Environment.ProcessPath;
            if (exe != null)
            {
                using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                {
                    if (ico != null) _iconHandle = CopyIcon(ico.Handle);
                }
            }
        }
        catch { _iconHandle = IntPtr.Zero; }

        var data = new NotifyIconData
        {
            CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            HWnd = _sink.Handle,
            UID = 1,
            UFlags = NifMessage | NifTip | (_iconHandle != IntPtr.Zero ? NifIcon : 0),
            UCallbackMessage = WmTrayCallback,
            HIcon = _iconHandle,
            SzTip = "MuseLink",
        };
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmTrayCallback)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == WmLButtonDblClk)
            {
                OpenRequested?.Invoke();
                handled = true;
            }
            else if (evt == WmRButtonUp)
            {
                ShowMenu();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var (running, halted) = StateProvider?.Invoke() ?? (false, false);
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            // Baris status non-aktif di kepala menu (v5.7.0):
            // versi + keadaan bridge sekilas, tanpa membuka
            // jendela — sesuai watak tray-first.
            var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string statusLine = "MuseLink v" + (ver != null ? ver.ToString(3) : "?")
                + (running ? " — Bridge Online" : " — Bridge Offline")
                + (halted ? " • Stop aktif" : "");
            AppendMenu(menu, MfString | MfGrayed, UIntPtr.Zero, statusLine);
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)1, "Buka MuseLink");
            AppendMenu(menu, MfString, (UIntPtr)2, running ? "Putuskan Akses" : "Sambungkan Akses");
            AppendMenu(menu, (uint)(MfString | (halted ? MfChecked : 0)), (UIntPtr)3,
                halted ? "Lepas Emergency Stop" : "Emergency Stop");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            // Saklar pengaturan (v5.8.0 — pindahan dari halaman
            // Settings yang dipensiunkan bersama shell v5).
            var (autoStart, adminMode, keepAwake) = SettingsProvider?.Invoke() ?? (false, false, false);
            AppendMenu(menu, (uint)(MfString | (autoStart ? MfChecked : 0)), (UIntPtr)5,
                "Jalan otomatis saat Windows menyala");
            AppendMenu(menu, (uint)(MfString | (adminMode ? MfChecked : 0)), (UIntPtr)6,
                "Mode Administrator");
            AppendMenu(menu, (uint)(MfString | (keepAwake ? MfChecked : 0)), (UIntPtr)7,
                "Cegah PC tidur");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)8, "Keluar");

            GetCursorPos(out var pt);
            SetForegroundWindow(_sink.Handle);
            int cmd = TrackPopupMenu(menu, TpmLeftAlign | TpmReturnCmd, pt.X, pt.Y, 0, _sink.Handle, IntPtr.Zero);
            PostMessage(_sink.Handle, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL — perbaikan klasik agar menu menutup benar
            switch (cmd)
            {
                case 1: OpenRequested?.Invoke(); break;
                case 2: ToggleServerRequested?.Invoke(); break;
                case 3: ToggleStopRequested?.Invoke(); break;
                case 5: ToggleAutoStartRequested?.Invoke(); break;
                case 6: ToggleAdminModeRequested?.Invoke(); break;
                case 7: ToggleKeepAwakeRequested?.Invoke(); break;
                case 8: ExitRequested?.Invoke(); break;
            }
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NotifyIconData
            {
                CbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                HWnd = _sink.Handle,
                UID = 1,
            };
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }
        _sink.Dispose();
    }
}
