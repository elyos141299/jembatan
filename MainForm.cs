using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Jembatan;

/// <summary>
/// Jendela utama MuseLink (nama internal tetap "Jembatan").
/// Selama jendela ini terbuka dan status
/// tersambung, layanan agen (AgentServer) mendengarkan di port 8765
/// dan Muse bisa bekerja di PC ini lewat jaringan Tailscale user.
/// Menekan PUTUSKAN menghentikan akses seketika. Tombol X hanya
/// menyembunyikan jendela ke system tray (layanan tetap berjalan);
/// keluar total lewat menu tray → Keluar.
/// Semua aktivitas Muse dicatat di log jendela ini, real-time.
/// Tampilan sejak v2.3.3: tema TERANG gaya iCloud — kartu putih
/// bersudut bundar di atas latar #F5F5F7, saklar ala iOS pengganti
/// checkbox, tombol pil (kontrolnya di UiControls.cs). Murni
/// tampilan; perilaku sama persis dengan v2.3.2.
/// </summary>
public sealed class MainForm : Form
{
    // Palet dari AppTheme (v2.5.0): Terang / Gelap / Ikuti perangkat.
    // Nama-nama lama dipertahankan sebagai properti agar seluruh
    // pemakaian di bawah tidak berubah; nilainya mengikuti tema
    // efektif dan ApplyTheme() mengecat ulang saat tema berganti.
    private static Color Bg => AppTheme.Bg;
    private static Color Card => AppTheme.Card;
    private static Color Ink => AppTheme.Ink;
    private static Color InkDim => AppTheme.InkDim;
    private static Color InkFaint => AppTheme.InkFaint;
    private static Color Panel => AppTheme.Panel;
    private static Color LogBg => AppTheme.LogBg;
    private static Color InkCard => AppTheme.InkCard;
    private static Color Hairline => AppTheme.Hairline;
    private static Color Accent => AppTheme.Accent;
    private static Color AccentStrong => AppTheme.AccentStrong;
    private static Color AccentAlt => AppTheme.AccentAlt;
    private static Color Green => AppTheme.Green;
    // v3.3.0: hijau titik status yang lebih hidup/jenuh.
    private static Color LiveGreen => Color.FromArgb(0x5B, 0xBB, 0x63);
    // v3.4.1: pil status relay di kartu kepala (gaya mockup
    // Figma) — hijau muda + teks hijau tua saat relay terhubung.
    private static Color RelayOkBg => Color.FromArgb(0xE9, 0xF3, 0xE8);
    private static Color RelayOkInk => Color.FromArgb(0x35, 0x71, 0x3C);
    private static Color Red => AppTheme.Red;
    private static Color RedOnDark => AppTheme.RedOnDark;
    private static Color LightFill => AppTheme.LightFill;
    private static Color DotWaiting => AppTheme.DotWaiting;
    private static Color DotStopped => AppTheme.DotStopped;
    private static Color ErrorBg => AppTheme.ErrorBg;
    private static Color ErrorInk => AppTheme.ErrorInk;
    private static Color Rail => AppTheme.Rail;
    private static Color DividerInk => AppTheme.DividerInk;
    private static Color TitleDotBg => AppTheme.TitleDotBg;
    private static Color RailHover => AppTheme.RailHover;
    private static Color InkOnDark => AppTheme.InkOnDark;
    private static Color Sage => AppTheme.Sage;
    private static Color Gold => AppTheme.Gold;

    private readonly AgentServer _server;
    private RoundDot _statusDot;
    private Label _statusLabel;
    private RichTextBox _logBox;
    private RichTextBox _panelLog;
    private RoundedPanel _headerCard;
    private Panel _logCard;
    private Label _titleLabel;
    // v3.2.0 (koreksi #9): info kepala panel jadi baris terstruktur.
    private Label _portValue;
    private Label _netValue;
    private Label _modeValue;
    private RoundedPanel _statusPill;
    // v3.4.1: pil relay di kartu kepala + kartu AKSES di bawah
    // kolom kiri panel (mockup Figma; sensor tetap dipertahankan).
    private RoundedPanel _relayPill;
    private RoundDot _relayDot;
    private Label _relayLabel;
    private string _relayPillState = "";
    private RoundedPanel _aksesCard;
    private Label _aksesServerValue;
    private Label _aksesWatchdogValue;
    private volatile string _watchdogText = "…";
    private long _watchdogPollTick;
    private GradientBar _gradBar;
    private string _statusTextFull = "Menunggu koneksi";
    private PictureBox _logoBox;
    private RoundedPanel _tilesCard;
    private Label _tileRelayValue;
    private Label _tileUptimeValue;
    private Label _tileCaptureValue;
    private Label _tileVersionValue;

    /// <summary>Versi tampil dari FileVersion rakitan (v3.7.0):
    /// satu sumber versi untuk seluruh UI — tidak ada lagi string
    /// versi tulis-tangan yang bisa tertinggal saat rilis.</summary>
    internal static string DisplayVersion
    {
        get
        {
            try
            {
                // v3.8.0: ProcessPath, bukan Assembly.Location
                // (kosong pada single-file) — lihat AgentServer.
                string exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    exePath = typeof(MainForm).Assembly.Location;
                string fv = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath).FileVersion;
                if (!string.IsNullOrEmpty(fv))
                {
                    var p = fv.Split('.');
                    if (p.Length >= 3) return p[0] + "." + p[1] + "." + p[2];
                    return fv;
                }
            }
            catch { /* abaikan — pakai bawaan */ }
            return "3.11.0";
        }
    }
    private Label _tileRamValue;
    private Label _tileCpuValue;
    private Label _tileGpuValue;
    private readonly System.Collections.Generic.List<Label> _tileCaptions = new System.Collections.Generic.List<Label>();
    private readonly System.Collections.Generic.List<Panel> _tileDividers = new System.Collections.Generic.List<Panel>();
    private System.Windows.Forms.Timer _uiTimer;
    private string _statusState = "waiting";
    private Panel _leftRoot;
    private Panel _chatRoot;
    private SettingsForm _settingsForm;
    // v2.8.0 — permukaan rombak total: bar judul, strip status,
    // panel log bawah, tombol akses & pengaturan di panel kiri.
    // v3.0.1 — log melayang (LogFlyoutForm) menggantikan dock
    // bawah; sensor sistem untuk baris RAM/CPU/GPU; animasi
    // lipat panel & penunda lipat-otomatis.
    private LogFlyoutForm _logFlyout;
    private ChatFlyoutForm _chatFlyout;
    private long _lastLogToggleTick;
    private long _lastChatToggleTick;
    private volatile string _cpuText = "…";
    private volatile string _gpuText = "…";
    private CpuTempSensor _cpuSensor;
    private volatile bool _sensorPollBusy;
    private bool _captureSettled;
    private Panel _titleBar;
    private Label _barTitle;
    private PictureBox _titleLogo;
    private RoundDot _titleDot;
    private CaptionButton _capMin;
    private CaptionButton _capMax;
    private CaptionButton _capClose;
    private Panel _logHeader;
    private Panel _logHair;
    private RoundedPanel _errBadge;
    private Label _errBadgeLabel;
    private RoundedButton _settingsBtn;
    private Label _logTitleLabel;
    // v3.0.0 — rel ikon 56px + strip kepala kartu chat (blueprint final).
    private Panel _rail;
    private PictureBox _railLogo;
    private RoundDot _railStatusDot;
    private RailGlyphButton _railLogBtn;
    private Panel _railLogBar;
    private RoundDot _railLogDot;
    private RailGlyphButton _railChatBtn;
    private Panel _railChatBar;
    private Panel _railSettingsBtn;
    private Label _railSettingsGlyph;
    private RoundedPanel _chatStrip;
    private Label _chatStripStatus;
    // v3.2.0 (koreksi #3): wadah sisi kanan strip chat — status +
    // zoom dikurung di pita tampak 36px, tidak menjorok keluar.
    private Panel _chatStripRight;
    // v3.2.0 (koreksi #6): denyut titik status Terhubung.
    private System.Windows.Forms.Timer _pulseTimer;
    private double _pulsePhase;
    // v3.1.0 — wadah & perilaku WebView2: kontrol zoom di strip
    // chat dan pengamat "balasan selesai" (jajak pendapat ringan;
    // menonaktifkan diri bila halaman berubah bentuk).
    private Panel _chatZoomHost;
    private Label _chatZoomLabel;
    private System.Windows.Forms.Timer _chatWatch;
    private bool _wasGenerating;
    private int _watchFailures;
    private bool _autoStartEnabled;

    /// <summary>Keadaan jalan otomatis saat ini (untuk jendela Pengaturan).</summary>
    internal bool AutoStartEnabled => _autoStartEnabled;

    /// <summary>True bila listener sedang berjalan (untuk jendela Pengaturan).</summary>
    internal bool ServerRunning => _server.IsRunning;

    // v3.4.0 (blueprint user): tata letak DIBALIK — panel kendali
    // kini penghuni utama jendela (lebar, mengisi seluruh sisa
    // jendela), dan chat WebView2 jadi jendela MELAYANG dari rel
    // (ChatFlyoutForm, mekanisme persis Log Aktivitas). Pemisah
    // SplitContainer pensiun.
    private RoundedPanel _chatBody;
    private Label _chatErrorLabel;
    private Label _chatLoadingLabel;
    private WebView2 _webView;

    // System tray: tombol X hanya menyembunyikan jendela ke tray —
    // layanan tetap berjalan. Keluar sungguhan hanya lewat menu tray.
    private NotifyIcon _trayIcon;
    private bool _exitRequested;
    private bool _trayTipShown;

    public MainForm()
    {
        Text = "MuseLink";
        // v2.4.1: jendela SELALU lebar — panel kendali + chat
        // menyatu dalam satu jendela sejak mulai.
        // v2.4.2: ukuran & posisi terakhir DIPULIHKAN dari registry
        // (keputusan user "gas semua") — jendela dibuka lagi persis
        // seperti ditinggalkan. Batas yang tersimpan hanya dipakai
        // bila masih cukup terlihat di salah satu layar; selain itu
        // jatuh ke bawaan 1280x800 di tengah layar.
        Width = 1280;
        Height = 800;
        // v3.4.1: tinggi minimum naik — kolom kiri panel kini
        // memuat kartu kepala + kartu STATUS + kartu AKSES.
        MinimumSize = new Size(980, 780);
        StartPosition = FormStartPosition.CenterScreen;
        var savedBounds = Settings.WindowBounds;
        if (savedBounds.HasValue && IsMostlyOnScreen(savedBounds.Value))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = savedBounds.Value;
            if (Settings.WindowMaximized)
                WindowState = FormWindowState.Maximized;
        }
        // v2.8.0: jendela tanpa bingkai Windows (bar judul gambar
        // sendiri di BuildLayout); batas maksimal = area kerja
        // layar agar tidak menutupi taskbar saat termaksimalkan.
        try { MaximizedBounds = Screen.PrimaryScreen.WorkingArea; } catch { /* abaikan */ }
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);

        BuildLayout();
        SizeChanged += (s, e) =>
        {
            if (_capMax != null)
            {
                _capMax.Maximized = WindowState == FormWindowState.Maximized;
                _capMax.Invalidate();
            }
            // v3.4.0: lipat panel pensiun (panel = penghuni utama
            // jendela, tidak bisa dilipat). Jendela terminimalkan
            // -> semua flyout rel ikut sembunyi seketika.
            if (WindowState == FormWindowState.Minimized)
            {
                _logFlyout?.HideImmediate();
                _chatFlyout?.HideImmediate();
            }
        };
        InitTrayIcon();
        FormClosing += OnMainFormClosing;
        FormClosed += (s, e) =>
        {
            try { _logFlyout?.Dispose(); } catch { /* abaikan */ }
            try { _chatFlyout?.Dispose(); } catch { /* abaikan */ }
            try { _cpuSensor?.Dispose(); } catch { /* abaikan */ }
        };

        _server = new AgentServer(this, Log, OnActivity);

        // Tema "Ikuti perangkat": cat ulang langsung saat tema
        // Windows berubah; kunci/buka layar masuk catatan kejadian.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if (AppTheme.Current == AppTheme.Choice.Device) ApplyTheme();
        };
        Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
        {
            bool kunci = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock;
            EventLog.Add(kunci ? "lock" : "unlock", kunci ? "layar dikunci" : "layar dibuka lagi");
        };

        Load += (s, e) =>
        {
            RotateLogFileIfNeeded();
            Log("=== MuseLink v" + DisplayVersion + " dimulai ===");
            InitStartOptions();
            if (Settings.KeepAwake)
            {
                NativeInput.SetKeepAwake(true);
                Log("Jangan tidur: AKTIF (dari pengaturan tersimpan).");
            }
            EventLog.Add("app-start", "MuseLink v" + DisplayVersion + " dimulai");
            // v2.8.0: roda gigi melayang di bar judul PENSIUN —
            // Pengaturan kini tombol tetap di bawah panel kiri.
            try { MaximizedBounds = Screen.FromHandle(Handle).WorkingArea; } catch { /* abaikan */ }
            ApplyTheme();
            RefreshTiles();
            _uiTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _uiTimer.Tick += (s2, e2) => RefreshTiles();
            _uiTimer.Start();
            // v3.2.0 (koreksi #6): denyut lembut titik status saat
            // Terhubung — indikator "live", bukan gambar tempel.
            _pulseTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _pulseTimer.Tick += PulseTick;
            _pulseTimer.Start();
            // v3.4.0: panel kendali penghuni utama jendela;
            // WebView2 chat dibuat sejak awal di dalam flyout
            // chat (tersembunyi) & dibuka ke muse.ai — sesi chat
            // sudah hangat saat user pertama membuka flyout-nya.
            // InitChatAsync menelan semua galatnya sendiri
            // (flyout menampilkan pesan galat; jembatan utama
            // tidak terganggu).
            RefreshRail();
            // v3.0.1: panaskan mesin tangkap DXGI sejak awal (di
            // utas latar) agar baris CAPTURE langsung membaca mode
            // aslinya, bukan "gdi" bawaan sebelum bingkai pertama.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { using (var b = DxgiCapture.TryCapture()) { } }
                catch { /* abaikan */ }
            });
            StartSensorPoller();
            _ = InitChatAsync();
            try
            {
                _server.Start();
                SetWaiting();
                // Jalan sehat (bukan berhenti-sengaja): bersihkan
                // bendera UserStopped agar penjaga (watchdog) kembali
                // melindungi proses ini.
                try { Settings.UserStopped = false; } catch { /* abaikan */ }
                Log("MuseLink berjalan di port " + AgentServer.Port + ".");
                Log("Menunggu Muse terhubung lewat Tailscale…");
            }
            catch (Exception ex)
            {
                SetStopped("Gagal memulai: " + ex.Message);
                Log("GAGAL memulai layanan: " + ex.Message);
            }
            // v2.7.0 (permintaan user): mulai otomatis (login
            // Windows / penjaga) → jendela menepi sendiri ke tray
            // sesudah ±6 detik, supaya tidak menghalangi layar
            // tiap PC menyala. Dibuka manual → blok ini tidak
            // jalan dan jendela tetap tampil.
            if (Program.StartedAuto)
            {
                var tmrAutoTray = new System.Windows.Forms.Timer { Interval = 6000 };
                tmrAutoTray.Tick += (s2, e2) =>
                {
                    tmrAutoTray.Stop();
                    tmrAutoTray.Dispose();
                    if (!_exitRequested && Visible)
                    {
                        Hide();
                        Log("Jendela otomatis disembunyikan ke system tray (mulai otomatis).");
                    }
                };
                tmrAutoTray.Start();
            }
        };
        FormClosed += (s, e) =>
        {
            _server.Stop();
            // Buang ikon tray agar tidak meninggalkan ikon hantu.
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        };
    }

    // ---------- System tray ----------

    // Ikon tray selalu tampil selama aplikasi berjalan. Klik ganda
    // membuka jendela; klik kanan memberi menu Buka / Keluar.
    private void InitTrayIcon()
    {
        Icon icon = null;
        try { icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { /* jatuh ke ikon bawaan di bawah */ }
        if (icon == null) icon = SystemIcons.Application;
        Icon = icon;

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Buka MuseLink");
        openItem.Click += (s, e) => ShowFromTray();
        var settingsItem = new ToolStripMenuItem("Pengaturan");
        settingsItem.Click += (s, e) => { ShowFromTray(); OpenSettings(); };
        var exitItem = new ToolStripMenuItem("Keluar");
        exitItem.Click += (s, e) => ExitFromTray();
        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "MuseLink",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.DoubleClick += (s, e) => ShowFromTray();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // Satu-satunya jalan keluar sungguhan dari dalam aplikasi:
    // tandai dulu, buang ikon tray, baru tutup — OnMainFormClosing
    // melihat tanda ini dan membiarkan jendela benar-benar tertutup.
    // Ini juga SATU-SATUNYA jalan yang memasang bendera UserStopped:
    // berhenti di sini memang sengaja, jadi penjaga (watchdog) tidak
    // boleh menyalakan ulang aplikasi.
    private void ExitFromTray()
    {
        try { Settings.UserStopped = true; } catch { /* abaikan */ }
        _exitRequested = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
    }

    // Tombol X (dan penutupan oleh user lainnya) TIDAK mematikan
    // MuseLink: jendela disembunyikan ke tray dan layanan agen terus
    // berjalan. Penutupan bukan dari user (Windows shutdown, Task
    // Manager) serta jalan Keluar dari tray dibiarkan lewat.
    private void OnMainFormClosing(object sender, FormClosingEventArgs e)
    {
        SaveWindowBounds();
        if (_exitRequested) return;
        // v2.4.3: tutup jendela SELALU berarti sembunyi ke tray, apa pun
        // sumber perintah tutupnya. Dulu penutupan yang datang bukan dari
        // klik user (mis. WM_CLOSE terprogram dari perintah /window)
        // lolos dari pemeriksaan CloseReason dan aplikasi KELUAR betulan.
        // Satu-satunya jalan keluar tetap menu tray "Keluar"
        // (_exitRequested) — pengecualian: Windows sendiri yang mati.
        if (e.CloseReason == CloseReason.WindowsShutDown) return;

        e.Cancel = true;
        Hide();
        Log("Jendela disembunyikan ke system tray — MuseLink tetap berjalan.");
        if (!_trayTipShown)
        {
            _trayTipShown = true;
            _trayIcon?.ShowBalloonTip(5000,
                "MuseLink tetap berjalan",
                "MuseLink tetap aktif di system tray. Klik ganda ikon tray untuk membuka lagi, atau klik kanan lalu pilih Keluar untuk berhenti total.",
                ToolTipIcon.Info);
        }
    }

    // v2.4.2: simpan ukuran & posisi jendela agar jalan berikutnya
    // membukanya persis seperti ditinggalkan. Keadaan dimaksimalkan
    // disimpan sebagai bendera; batas yang disimpan selalu batas
    // NORMAL (RestoreBounds) supaya membuka-ulang tidak aneh.
    private void SaveWindowBounds()
    {
        try
        {
            if (WindowState == FormWindowState.Maximized)
            {
                Settings.WindowBounds = RestoreBounds;
                Settings.WindowMaximized = true;
            }
            else if (WindowState == FormWindowState.Normal)
            {
                Settings.WindowBounds = Bounds;
                Settings.WindowMaximized = false;
            }
        }
        catch { /* abaikan — gagal menyimpan tidak boleh mengganggu penutupan */ }
    }

    private static bool IsMostlyOnScreen(Rectangle r)
    {
        foreach (var screen in Screen.AllScreens)
        {
            var wa = screen.WorkingArea;
            int ix = Math.Max(0, Math.Min(r.Right, wa.Right) - Math.Max(r.Left, wa.Left));
            int iy = Math.Max(0, Math.Min(r.Bottom, wa.Bottom) - Math.Max(r.Top, wa.Top));
            if (ix >= 120 && iy >= 80) return true;
        }
        return false;
    }

    private void BuildLayout()
    {
        // v2.8.0 — ROMBAK TOTAL tampilan (cetak biru riset gelap
        // modern; aksen indigo Linear pilihan user): bar judul
        // terpadu gambar-sendiri 36px di atas, strip status 24px di
        // bawah, dan di tengah pemisah horizontal — atas berisi
        // panel kendali (kiri 332px) + chat, bawah berisi panel log
        // selebar jendela yang bisa dilipat & digeser tingginya
        // (pola VS Code/OBS). Panel kiri: kartu kepala, tile 2x2,
        // grup AKSES (tombol Putuskan/Sambungkan), tombol
        // Pengaturan menempel di bawah.
        FormBorderStyle = FormBorderStyle.None;

        // ---------- Bar judul terpadu ----------
        var titleBar = _titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Bg,
        };
        // v3.2.0 (koreksi #4): bar atas bergradasi lavender halus —
        // tidak lagi polos satu warna yang terkesan kosong.
        titleBar.Paint += TitleBarPaint;
        _titleLogo = new PictureBox
        {
            Size = new Size(24, 24),
            Location = new Point(10, 8),
            BackColor = Color.Transparent,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try
        {
            using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null) _titleLogo.Image = ic.ToBitmap();
        }
        catch { /* tanpa logo pun rapi */ }
        titleBar.Controls.Add(_titleLogo);
        var barTitle = _barTitle = new Label
        {
            Text = "MuseLink",
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = Ink,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(42, 12),
        };
        titleBar.Controls.Add(barTitle);
        _titleDot = new RoundDot
        {
            Size = new Size(18, 18),
            Location = new Point(115, 11),
            GlowPulse = true,
            // v3.3.2: BUKAN Transparan — di atas gradasi bar judul
            // transparan tampil sebagai KOTAK gelap (temuan full
            // test). Pakai sampel warna gradasi di posisi titik.
            BackColor = TitleDotBg,
            DotColor = DotWaiting,
        };
        titleBar.Controls.Add(_titleDot);
        _capMin = new CaptionButton { Kind = CaptionButton.GlyphKind.Minimize, Dock = DockStyle.Right };
        _capMax = new CaptionButton { Kind = CaptionButton.GlyphKind.Maximize, Dock = DockStyle.Right };
        _capClose = new CaptionButton { Kind = CaptionButton.GlyphKind.Close, Dock = DockStyle.Right };
        titleBar.Controls.Add(_capMin);
        titleBar.Controls.Add(_capMax);
        titleBar.Controls.Add(_capClose);
        _capMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
        _capMax.Click += (s, e) => ToggleMaximize();
        _capClose.Click += (s, e) => Close();
        titleBar.MouseDown += TitleBarMouseDown;
        _titleLogo.MouseDown += TitleBarMouseDown;
        barTitle.MouseDown += TitleBarMouseDown;
        _titleDot.MouseDown += TitleBarMouseDown;


        // ---------- Tengah jendela (v3.4.0) ----------
        // Pemisah SplitContainer PENSIUN: panel kendali kini
        // mengisi seluruh tengah jendela (dirakit di bagian
        // "Panel kendali utama" di bawah), chat pindah ke
        // flyout rel seperti log & Pengaturan.

        // ---------- Log melayang (v3.0.1) ----------
        // Dock bawah pensiun: log kini jendela melayang berwarna
        // tinta di kiri (LogFlyoutForm — konsep user "sama seperti
        // Pengaturan, di kiri dan menyatu dengan tombol"). Kepala
        // + badan log yang sama seperti dulu dipindah ke dalamnya.
        var flyout = _logFlyout = new LogFlyoutForm();
        flyout.VisibleChanged += (s, e) =>
        {
            RefreshRail();
        };

        // ---------- Chat melayang (v3.4.0) ----------
        // Kembaran log: jendela melayang dari rel berisi kartu
        // chat WebView2 (dibangun BuildChatPane ke Host-nya).
        var chatFlyout = _chatFlyout = new ChatFlyoutForm();
        chatFlyout.VisibleChanged += (s, e) =>
        {
            RefreshRail();
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = LogBg,
            ForeColor = Ink,
            Font = new Font("Consolas", 9.5f),
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.None,
        };
        var logBody = _logCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = LogBg,
            Padding = new Padding(12, 8, 12, 8),
        };
        _logBox.MouseWheel += LogBoxMouseWheel;
        logBody.Controls.Add(_logBox);
        var logHair = _logHair = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Hairline,
        };
        var logHeader = _logHeader = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = LogBg,
            Tag = "inkbg",
        };
        var logTitle = _logTitleLabel = new Label
        {
            Text = "LOG AKTIVITAS",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = InkOnDark,
            BackColor = LogBg,
            AutoSize = true,
            Location = new Point(12, 9),
        };
        logHeader.Controls.Add(logTitle);
        _errBadge = new RoundedPanel
        {
            Location = new Point(112, 7),
            Size = new Size(60, 18),
            CornerRadius = 9,
            FillColor = LogBg,
            BorderColor = Hairline,
            BackColor = LogBg,
            Visible = false,
        };
        _errBadgeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            BackColor = Panel,
            ForeColor = Ink,
            Text = "0 error",
        };
        _errBadge.Controls.Add(_errBadgeLabel);
        logHeader.Controls.Add(_errBadge);
        var logClose = new Label
        {
            Text = "\uE711",
            Font = new Font("Segoe MDL2 Assets", 11f),
            ForeColor = InkOnDark,
            BackColor = LogBg,
            Dock = DockStyle.Right,
            Width = 38,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        logClose.Click += (s, e) => ToggleLogPanel();
        logHeader.Controls.Add(logClose);
        flyout.HeaderHost.Controls.Add(logHeader);
        flyout.BodyHost.Controls.Add(logBody);
        flyout.BodyHost.Controls.Add(logHair);

        // ---------- Panel kendali utama (v3.4.0) ----------
        // Dulu panel sempit 264px di Panel1 pemisah; kini panel
        // kendali mengisi SELURUH tengah jendela (didok Fill ke
        // wadah tengah di akhir BuildLayout, di kanan rel +
        // garis pembatas DividerInk). Isinya disebar dua kolom:
        // kolom kiri tetap (kartu kepala + baris status) dan
        // kolom kanan AKTIVITAS TERAKHIR mengisi sisa ruang.
        var leftRoot = _leftRoot = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = LogBg,
            Padding = new Padding(14),
            Tag = "inkbg",
        };

        // Kartu kepala: logo + judul + pill status + alamat + mode.
        // v3.4.1: kartu lebih tinggi — ada pil status relay
        // (mockup Figma) di bawah pil status server.
        var header = _headerCard = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 220,
            FillColor = LogBg,
            BorderColor = LogBg,
            CornerRadius = 8,
            Padding = new Padding(14),
        };
        _logoBox = new PictureBox
        {
            Size = new Size(36, 36),
            Location = new Point(14, 12),
            BackColor = LogBg,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try
        {
            using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null) _logoBox.Image = ic.ToBitmap();
        }
        catch { /* tanpa logo pun tata letak tetap rapi */ }
        header.Controls.Add(_logoBox);

        var title = _titleLabel = new Label
        {
            Text = "MuseLink",
            Font = new Font("Segoe UI Semibold", 13.5f),
            ForeColor = InkOnDark,
            BackColor = LogBg,
            AutoSize = true,
            Location = new Point(60, 11),
        };
        header.Controls.Add(title);

        _statusPill = new RoundedPanel
        {
            Location = new Point(14, 58),
            Size = new Size(208, 26),
            CornerRadius = 13,
            FillColor = LogBg,
            BorderColor = RailHover,
        };
        header.Controls.Add(_statusPill);

        _statusDot = new RoundDot
        {
            Size = new Size(16, 16),
            Location = new Point(7, 5),
            GlowPulse = true,
            BackColor = LogBg,
            DotColor = DotWaiting,
        };
        _statusPill.Controls.Add(_statusDot);

        _statusLabel = new Label
        {
            Text = "Menunggu koneksi",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = InkOnDark,
            BackColor = LogBg,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(146, 18),
            Location = new Point(27, 4),
        };
        _statusPill.Controls.Add(_statusLabel);

        // v3.4.1: pil status RELAY (mockup Figma) — "Terhubung —
        // relay aman" di pil hijau muda saat relay Tailscale
        // terhubung; pil merah muda bertuliskan keadaan sebenarnya
        // bila tidak. Isi & warnanya diperbarui di RefreshTiles
        // lewat RefreshRelayPill.
        _relayPill = new RoundedPanel
        {
            Location = new Point(14, 92),
            Size = new Size(208, 26),
            CornerRadius = 13,
            FillColor = RelayOkBg,
            BorderColor = LiveGreen,
        };
        header.Controls.Add(_relayPill);

        _relayDot = new RoundDot
        {
            Size = new Size(16, 16),
            Location = new Point(7, 5),
            BackColor = RelayOkBg,
            DotColor = DotWaiting,
        };
        _relayPill.Controls.Add(_relayDot);

        _relayLabel = new Label
        {
            Text = "Relay …",
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = RelayOkInk,
            BackColor = RelayOkBg,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(172, 18),
            Location = new Point(27, 4),
        };
        _relayPill.Controls.Add(_relayLabel);

        // v3.2.0 (koreksi #9): info kepala jadi baris terstruktur
        // PORT / JARINGAN / MODE — menggantikan kalimat panjang
        // "Port 8765 • Hanya bisa dijangkau…" yang membungkus acak.
        bool elevatedNow = Settings.IsElevated();
        MakeInfoRow(header, 0, "PORT", "" + AgentServer.Port, out _portValue);
        MakeInfoRow(header, 1, "JARINGAN", "Tailscale — privat", out _netValue);
        MakeInfoRow(header, 2, "MODE", elevatedNow ? "Administrator" : "Pengguna biasa", out _modeValue);

        // Tile status 2x2 (v2.8.0 — panel menyempit, tile jadi grid).
        // v3.4.1: baris status kini berkartu "STATUS" (mockup
        // Figma) — kartu InkCard bertepi RailHover dengan judul
        // mikro; isi tetap 7 baris termasuk sensor RAM/CPU/GPU.
        var tilesWrap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 292,
            BackColor = LogBg,
            Tag = "inkbg",
            Padding = new Padding(0, 12, 0, 0),
        };
        var tiles = _tilesCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = InkCard,
            BorderColor = RailHover,
            CornerRadius = 8,
            BackColor = LogBg,
        };
        tilesWrap.Controls.Add(tiles);
        var statusTitle = new Label
        {
            Text = "STATUS",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = InkCard,
            AutoSize = true,
            Location = new Point(14, 11),
        };
        tiles.Controls.Add(statusTitle);
        _tileCaptions.Add(statusTitle);
        MakeRow(tiles, 0, "RELAY", "…", out _tileRelayValue);
        MakeRow(tiles, 1, "UPTIME", "0m", out _tileUptimeValue);
        MakeRow(tiles, 2, "CAPTURE", "…", out _tileCaptureValue);
        MakeRow(tiles, 3, "RAM", "…", out _tileRamValue);
        MakeRow(tiles, 4, "CPU", "…", out _tileCpuValue);
        MakeRow(tiles, 5, "GPU", "…", out _tileGpuValue);
        MakeRow(tiles, 6, "VERSI", DisplayVersion, out _tileVersionValue);


        // v3.4.1: kartu AKSES (mockup Figma) menempel di BAWAH
        // kolom kiri — isinya HANYA status Jembatan & Watchdog
        // plus satu tombol "Buka Pengaturan". Bukan grup aksi:
        // putus/sambung akses TETAP hanya di Pengaturan (aturan
        // v3.2.0 di bawah tidak berubah).
        var akses = _aksesCard = new RoundedPanel
        {
            Dock = DockStyle.Bottom,
            Height = 148,
            FillColor = InkCard,
            BorderColor = RailHover,
            CornerRadius = 8,
            BackColor = LogBg,
        };
        var aksesTitle = new Label
        {
            Text = "AKSES",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = InkCard,
            AutoSize = true,
            Location = new Point(14, 10),
        };
        akses.Controls.Add(aksesTitle);
        _tileCaptions.Add(aksesTitle);
        MakeAksesRow(akses, 0, "Jembatan", "…", out _aksesServerValue);
        MakeAksesRow(akses, 1, "Watchdog", "…", out _aksesWatchdogValue);
        var btnSettings = new RoundedButton
        {
            Text = "Buka Pengaturan",
            Font = new Font("Segoe UI Semibold", 10.5f),
            BackColor = AccentStrong,
            ForeColor = Color.White,
            CornerRadius = 8,
            Location = new Point(14, 100),
            Size = new Size(392, 36),
        };
        btnSettings.Click += (s, e) => OpenSettings();
        akses.Controls.Add(btnSettings);

        // v3.0.0: tombol Pengaturan PINDAH ke rel ikon (roda gigi
        // di bawah rel) — panel kini murni status + aksi akses.

        // v3.2.0 (koreksi #2): grup AKSES di panel DIHAPUS —
        // putus/sambung akses kini hanya di Pengaturan.
        // v3.4.1: yang kembali hanya KARTU STATUS akses (di atas)
        // — tanpa tombol putus/sambung apa pun.
        // v3.3.2 (koreksi user): separuh bawah panel yang kosong
        // diisi pratinjau AKTIVITAS TERAKHIR — baris log terbaru,
        // diperbarui di Log(). Ditambah PERTAMA agar terdok
        // terakhir (mengisi sisa ruang di bawah tile).
        var miniWrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = LogBg,
            Tag = "inkbg",
            Padding = new Padding(14, 10, 0, 0),
        };
        var miniHair = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Hairline };
        var miniTitle = new Label
        {
            Text = "AKTIVITAS TERAKHIR",
            Font = new Font("Segoe UI", 8f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = LogBg,
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(0, 6, 0, 0),
        };
        _panelLog = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = LogBg,
            ForeColor = InkFaint,
            Font = new Font("Consolas", 8.5f),
            DetectUrls = false,
            ScrollBars = RichTextBoxScrollBars.None,
        };
        miniWrap.Controls.Add(_panelLog);
        miniWrap.Controls.Add(miniTitle);
        miniWrap.Controls.Add(miniHair);
        // v3.4.0: garis tegak pemisah dua kolom — ditambah PALING
        // AKHIR agar terdok pertama (tepi kiri kolom aktivitas).
        var miniDivider = new Panel
        {
            Dock = DockStyle.Left,
            Width = 1,
            BackColor = RailHover,
        };
        miniWrap.Controls.Add(miniDivider);
        // Kolom kiri panel (v3.4.0): lebar sama persis dengan isi
        // panel lama (235px) agar kartu kepala & baris status —
        // yang digambar dengan koordinat tetap — tidak bergeser
        // sedikit pun. Kolom kanan (miniWrap) mengisi sisanya.
        // v3.4.1 (mockup Figma): kolom MELEBAR ke 420px — kartu
        // kepala, kartu STATUS, dan kartu AKSES (dok bawah,
        // ditambah pertama agar terdok terakhir) tinggal di sini.
        var leftCol = new Panel
        {
            Dock = DockStyle.Left,
            Width = 420,
            BackColor = LogBg,
            Tag = "inkbg",
        };
        leftCol.Controls.Add(akses);
        leftCol.Controls.Add(tilesWrap);
        leftCol.Controls.Add(header);
        leftRoot.Controls.Add(miniWrap);
        // Urutan tambah menentukan urutan dok (terbalik).
        leftRoot.Controls.Add(leftCol);

        BuildChatPane();
        // v3.4.0: paksa gagang jendela flyout chat tercipta sejak
        // awal (tanpa menampilkannya) agar WebView2 bisa dibuat &
        // dimuat di latar sebelum flyout pertama kali dibuka.
        try { var chatHandle = _chatFlyout.Handle; } catch { /* abaikan */ }

        BuildRail();

        // Susunan dok form: tengah (rel + pembatas + panel utama),
        // lalu atas. Rel menempel di tuan rumah yang sama agar
        // tingginya penuh sebadan jendela.
        var mid = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        mid.Controls.Add(leftRoot);
        // v3.3.2 (koreksi user): garis pembatas rel <-> panel —
        // ditambah SESUDAH leftRoot agar terdok lebih dulu, persis
        // di tepi kanan rel.
        var railDivider = new Panel
        {
            Dock = DockStyle.Left,
            Width = 1,
            BackColor = DividerInk,
        };
        mid.Controls.Add(railDivider);
        mid.Controls.Add(_rail);
        Controls.Add(mid);
        Controls.Add(titleBar);
    }

    // v3.2.0 (koreksi #4): gradasi lavender bar judul — kiri lebih
    // pekat (keluarga Panel), mencair ke kanvas Bg di kanan, plus
    // garis rambut pemisah di tepi bawah. Ujung kanan sengaja
    // persis Bg agar isian diam tombol jendela menyatu mulus.
    private void TitleBarPaint(object sender, PaintEventArgs e)
    {
        var bar = (Panel)sender;
        if (bar.Width < 2 || bar.Height < 2) return;
        using (var brush = new LinearGradientBrush(
            new Rectangle(0, 0, bar.Width, bar.Height),
            Color.FromArgb(0xDD, 0xD6, 0xF5), Bg, LinearGradientMode.Horizontal))
            e.Graphics.FillRectangle(brush, bar.ClientRectangle);
        using (var pen = new Pen(Hairline))
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
    }

    // ---------- Rel ikon 56px (v3.0.0, blueprint final) ----------
    // Rel tinta di tepi kiri sebadan jendela: logo + titik status
    // (penanda; sejak v3.4.0 pasif karena panel tidak dilipat),
    // tombol log (garis sage saat log terbuka + titik merah saat
    // ada error), tombol chat (v3.4.0 — garis sage saat flyout
    // chat terbuka), roda gigi Pengaturan menempel di bawah.
    private void BuildRail()
    {
        var rail = _rail = new Panel
        {
            Dock = DockStyle.Left,
            Width = 56,
            BackColor = Rail,
        };
        _railLogo = new PictureBox
        {
            Size = new Size(32, 32),
            Location = new Point(12, 12),
            BackColor = Rail,
            SizeMode = PictureBoxSizeMode.Zoom,
        };
        try
        {
            using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                if (ic != null) _railLogo.Image = ic.ToBitmap();
        }
        catch { /* tanpa logo pun rel tetap utuh */ }
        // v3.4.0: klik logo & titik status DULU melipat panel —
        // panel kini penghuni utama yang tidak dilipat, jadi
        // keduanya pasif (murni penanda).
        rail.Controls.Add(_railLogo);

        var statusZone = new Panel
        {
            Location = new Point(0, 56),
            Size = new Size(56, 44),
            BackColor = Rail,
            Tag = "inkbg",
        };
        _railStatusDot = new RoundDot
        {
            Size = new Size(20, 20),
            Location = new Point(18, 12),
            GlowPulse = true,
            BackColor = Rail,
            DotColor = DotWaiting,
        };
        statusZone.Controls.Add(_railStatusDot);
        rail.Controls.Add(statusZone);

        // v3.0.1: tombol log kini RailGlyphButton — glif digambar
        // panelnya sendiri (bukan Label anak) karena versi Label
        // Dock=Fill terbukti tidak menanggapi klik di v3.0.0;
        // klik kini jatuh langsung ke panel. MouseDown dipasang
        // sebagai jalur kedua selain Click (penjaga waktu di
        // ToggleLogPanel mencegah pemicu ganda).
        var logBtn = _railLogBtn = new RailGlyphButton
        {
            Location = new Point(0, 104),
            Size = new Size(56, 48),
            BackColor = Rail,
            Tag = "inkbg",
            Cursor = Cursors.Hand,
            Glyph = "\uE8FD",
            GlyphColor = InkOnDark,
            GlyphSize = 15f,
        };
        _railLogBar = new Panel
        {
            Location = new Point(0, 10),
            Size = new Size(3, 28),
            BackColor = Sage,
            Visible = false,
        };
        logBtn.Controls.Add(_railLogBar);
        _railLogDot = new RoundDot
        {
            Size = new Size(8, 8),
            Location = new Point(38, 5),
            BackColor = Rail,
            DotColor = Red,
            Visible = false,
        };
        logBtn.Controls.Add(_railLogDot);
        logBtn.Click += (s, e) => ToggleLogPanel();
        logBtn.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) ToggleLogPanel(); };
        logBtn.MouseEnter += (s, e) => PaintRailButton(true);
        logBtn.MouseLeave += (s, e) => RefreshRail();
        rail.Controls.Add(logBtn);

        // v3.4.0: tombol CHAT di rel — membuka flyout chat
        // (WebView2) dengan mekanisme persis tombol log: glif
        // gelembung percakapan, bar sage saat flyout terbuka,
        // pasangan Click+MouseDown dengan penjaga pemicu ganda
        // di ToggleChatPanel.
        var chatBtn = _railChatBtn = new RailGlyphButton
        {
            Location = new Point(0, 152),
            Size = new Size(56, 48),
            BackColor = Rail,
            Tag = "inkbg",
            Cursor = Cursors.Hand,
            Glyph = "\uE8BD",
            GlyphColor = InkOnDark,
            GlyphSize = 15f,
        };
        _railChatBar = new Panel
        {
            Location = new Point(0, 10),
            Size = new Size(3, 28),
            BackColor = Sage,
            Visible = false,
        };
        chatBtn.Controls.Add(_railChatBar);
        chatBtn.Click += (s, e) => ToggleChatPanel();
        chatBtn.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) ToggleChatPanel(); };
        chatBtn.MouseEnter += (s, e) => PaintChatButton(true);
        chatBtn.MouseLeave += (s, e) => RefreshRail();
        rail.Controls.Add(chatBtn);

        var setBtn = _railSettingsBtn = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = Rail,
            Tag = "inkbg",
            Cursor = Cursors.Hand,
        };
        _railSettingsGlyph = new Label
        {
            Text = "\uE713",
            Font = new Font("Segoe MDL2 Assets", 15f),
            ForeColor = InkOnDark,
            BackColor = Rail,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        setBtn.Controls.Add(_railSettingsGlyph);
        setBtn.Click += (s, e) => OpenSettings();
        _railSettingsGlyph.Click += (s, e) => OpenSettings();
        setBtn.MouseEnter += (s, e) => { setBtn.BackColor = RailHover; _railSettingsGlyph.BackColor = RailHover; };
        setBtn.MouseLeave += (s, e) => { setBtn.BackColor = Rail; _railSettingsGlyph.BackColor = Rail; };
        _railSettingsGlyph.MouseEnter += (s, e) => { setBtn.BackColor = RailHover; _railSettingsGlyph.BackColor = RailHover; };
        _railSettingsGlyph.MouseLeave += (s, e) => { setBtn.BackColor = Rail; _railSettingsGlyph.BackColor = Rail; };
        rail.Controls.Add(setBtn);
    }

    private void PaintRailButton(bool hover)
    {
        if (_railLogBtn == null) return;
        bool open = _logFlyout != null && _logFlyout.Visible;
        Color bg = open || hover ? RailHover : Rail;
        _railLogBtn.BackColor = bg;
        if (_railLogDot != null) _railLogDot.BackColor = bg;
        if (_railLogBar != null) _railLogBar.Visible = open;
    }

    private void PaintChatButton(bool hover)
    {
        if (_railChatBtn == null) return;
        bool open = _chatFlyout != null && _chatFlyout.Visible;
        _railChatBtn.BackColor = open || hover ? RailHover : Rail;
        if (_railChatBar != null) _railChatBar.Visible = open;
    }

    /// <summary>Warna tombol log di rel mengikuti keadaan panel log &amp; error.</summary>
    internal void RefreshRail()
    {
        PaintRailButton(false);
        PaintChatButton(false);
        if (_railLogDot != null) _railLogDot.Visible = _errorCount > 0;
    }

    // v3.4.0: TogglePanel + animasi lipat PENSIUN — panel kendali
    // tidak lagi bisa dilipat karena ia penghuni utama jendela.

    // Satu baris rincian status (v3.0.0 — pengganti tile 2x2):
    // keterangan mikro di kiri, nilai semibold di sebelahnya,
    // garis rambut memisahkan antar baris.
    private void MakeRow(Control parent, int idx, string caption, string value, out Label valueLabel)
    {
        // v3.4.1: baris mulai di bawah judul kartu STATUS (kolom
        // 420px) — pemisah selebar kartu, nilai RATA KANAN seperti
        // mockup Figma, latar mengikuti kartu (InkCard).
        int y = 42 + idx * 34;
        if (idx > 0)
        {
            var div = new Panel
            {
                Size = new Size(392, 1),
                Location = new Point(14, y - 7),
                BackColor = RailHover,
            };
            parent.Controls.Add(div);
            _tileDividers.Add(div);
        }
        var cap = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = InkCard,
            AutoSize = false,
            Size = new Size(84, 20),
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(14, y),
        };
        parent.Controls.Add(cap);
        _tileCaptions.Add(cap);
        valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 11.5f),
            ForeColor = InkOnDark,
            BackColor = InkCard,
            AutoSize = false,
            Size = new Size(302, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(104, y),
        };
        parent.Controls.Add(valueLabel);
    }

    // Satu baris status di kartu AKSES (v3.4.1): keterangan mikro
    // di kiri, nilai semibold rata kanan — seirama baris STATUS.
    private void MakeAksesRow(Control parent, int idx, string caption, string value, out Label valueLabel)
    {
        int y = 36 + idx * 27;
        var cap = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = InkCard,
            AutoSize = false,
            Size = new Size(84, 20),
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(14, y),
        };
        parent.Controls.Add(cap);
        valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 11.5f),
            ForeColor = InkOnDark,
            BackColor = InkCard,
            AutoSize = false,
            Size = new Size(302, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(104, y),
        };
        parent.Controls.Add(valueLabel);
    }

    // Baris info mini di kartu kepala (v3.2.0, koreksi #9):
    // keterangan mikro + nilai, seirama baris rincian panel —
    // menggantikan kalimat alamat panjang + baris mode lama.
    private void MakeInfoRow(Control parent, int idx, string caption, string value, out Label valueLabel)
    {
        // v3.4.1: baris info mulai di bawah pil relay (y=130);
        // pemisah selebar kartu kepala, nilai rata kanan.
        int y = 130 + idx * 27;
        if (idx > 0)
        {
            var div = new Panel
            {
                Size = new Size(392, 1),
                Location = new Point(14, y - 6),
                BackColor = RailHover,
            };
            parent.Controls.Add(div);
            _tileDividers.Add(div);
        }
        var cap = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = InkFaint,
            BackColor = LogBg,
            AutoSize = false,
            Size = new Size(84, 20),
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(14, y),
        };
        parent.Controls.Add(cap);
        _tileCaptions.Add(cap);
        valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = InkOnDark,
            BackColor = LogBg,
            AutoSize = false,
            Size = new Size(302, 20),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(104, y),
        };
        parent.Controls.Add(valueLabel);
    }

    // ---------- Panel chat Muse ----------
    // Kartu chat berisi situs muse.ai yang ditanam lewat WebView2 —
    // isinya website Muse yang sama persis seperti di browser; user
    // login dengan akunnya SENDIRI di panel ini, aplikasi tidak pernah
    // menyentuh kredensial. Sejak v3.4.0 kartu ini tinggal di flyout
    // chat rel (dibuat saat Load, flyout tersembunyi) — jembatan tetap
    // jalan penuh walau WebView2 gagal/runtimenya tidak ada (flyout
    // hanya menampilkan galat).

    // UA Chrome desktop terkini — tanpa ini sebagian penyedia login
    // menolak browser tertanam.
    private const string DesktopChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private void BuildChatPane()
    {
        var root = _chatRoot = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(12),
        };

        // Badan chat = kartu putih (Dock Fill, ditambahkan PERTAMA
        // agar mengisi sisa ruang) — web view / label galat tinggal
        // di sini dengan bantalan 10px, jadi sudut persegi web view
        // menempel di bidang putih kartu, tidak menabrak lengkung
        // sudut kartu (v2.3.5; sebelumnya bantalan 2px + potongan
        // Region yang bergerigi).
        _chatBody = new RoundedPanel
        {
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 8,
            Padding = new Padding(0),
        };
        root.Controls.Add(_chatBody);

        // v3.0.0 (blueprint): strip kepala tinta 36px DI ATAS kartu
        // chat — bagian dari bingkai "kartu jendela", BUKAN overlay
        // (overlay di atas WebView2 mustahil: airspace HWND). Strip
        // menumpang 10px di atas kartu (tinggi 46, 12px terbawah
        // ditutup panel penutup sesama tinta) agar sudut bundarnya
        // menyatu dengan kartu; susunan manual lewat Resize karena
        // dok WinForms tidak bisa menumpang.
        _chatStrip = new RoundedPanel
        {
            FillColor = Ink,
            BorderColor = Ink,
            CornerRadius = 8,
            BackColor = Bg,
            Height = 54,
        };
        var stripCover = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            BackColor = Ink,
        };
        _chatStrip.Controls.Add(stripCover);
        var stripTitle = new Label
        {
            Text = "Chat Muse",
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = InkOnDark,
            BackColor = Ink,
            AutoSize = true,
            Location = new Point(12, 10),
        };
        _chatStrip.Controls.Add(stripTitle);
        // v3.2.0 (koreksi #3): status + zoom dikurung dalam satu
        // wadah sisi kanan setinggi pita tampak strip (36px).
        // Cara lama (status Dock=Right setinggi penuh 46px) membuat
        // blok status menjorok keluar strip menimpa kartu chat —
        // panel penutup bawah hanya menutup sisa lebar sesudah dok.
        _chatStripRight = new Panel
        {
            BackColor = Ink,
            Size = new Size(334, 36),
            // v3.3.0 (koreksi user): zoom disembunyikan dan status
            // Terhubung dihapus dari strip — seluruh gugus kanan
            // tidak ditampilkan (status tetap ada di panel & bar
            // judul; zoom terakhir tetap berlaku di WebView2).
            Visible = false,
        };
        _chatStrip.Controls.Add(_chatStripRight);
        _chatStripStatus = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 9f),
            ForeColor = InkOnDark,
            BackColor = Ink,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(122, 0),
            Size = new Size(212, 36),
        };
        _chatStripRight.Controls.Add(_chatStripStatus);
        // Gugus zoom teks chat (− persen +), di kiri label status
        // di dalam wadah yang sama, sejajar vertikal dengannya.
        _chatZoomHost = new Panel { BackColor = Ink, Location = new Point(0, 7), Size = new Size(112, 22) };
        var zoomMinus = new Label
        {
            Text = "−",
            Font = new Font("Segoe UI", 10f),
            ForeColor = InkOnDark,
            BackColor = Ink,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(0, 0),
            Size = new Size(28, 22),
            Cursor = Cursors.Hand,
        };
        zoomMinus.Click += (s, e) => AdjustZoom(-10);
        _chatZoomLabel = new Label
        {
            Text = Settings.ChatZoomPercent + "%",
            Font = new Font("Segoe UI", 9f),
            ForeColor = InkOnDark,
            BackColor = Ink,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(28, 0),
            Size = new Size(56, 22),
        };
        var zoomPlus = new Label
        {
            Text = "+",
            Font = new Font("Segoe UI", 10f),
            ForeColor = InkOnDark,
            BackColor = Ink,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(84, 0),
            Size = new Size(28, 22),
            Cursor = Cursors.Hand,
        };
        zoomPlus.Click += (s, e) => AdjustZoom(10);
        _chatZoomHost.Controls.Add(zoomMinus);
        _chatZoomHost.Controls.Add(_chatZoomLabel);
        _chatZoomHost.Controls.Add(zoomPlus);
        _chatStripRight.Controls.Add(_chatZoomHost);
        root.Controls.Add(_chatStrip);
        _chatStrip.BringToFront();
        root.Resize += (s, e) =>
        {
            int w = root.ClientSize.Width - 24;
            int h = root.ClientSize.Height - 24;
            if (w <= 0 || h <= 0) return;
            _chatStrip.SetBounds(12, 12, w, 54);
            _chatStripRight?.SetBounds(w - 334 - 6, 0, 334, 36);
            _chatBody.SetBounds(12, 54, w, Math.Max(60, h - 42));
        };

        _chatErrorLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = ErrorInk,
            BackColor = ErrorBg,
            Font = new Font("Segoe UI", 10.5f),
            Visible = false,
            Padding = new Padding(24),
        };
        _chatBody.Controls.Add(_chatErrorLabel);

        // v2.4.2 (permintaan user): kepala panel ("Chat Muse —
        // login…") dan bilah alat (Muat Ulang / Buka di Chrome)
        // DIHAPUS — keduanya makan dua baris sendiri. Chat kini
        // mengisi kartu sampai ke atas. Muat ulang tetap tersedia
        // lewat klik kanan di dalam chat (menu bawaan WebView2).
        // Sebagai gantinya ada label "Memuat chat Muse…" yang
        // tampil selama halaman pertama dimuat lalu hilang sendiri
        // (NavigationCompleted di InitChatAsync) — tidak ada lagi
        // layar putih kosong yang terlihat seperti macet.
        _chatLoadingLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Memuat chat Muse…",
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = InkDim,
            BackColor = Card,
            Font = new Font("Segoe UI", 10.5f),
            Visible = true,
        };
        _chatBody.Controls.Add(_chatLoadingLabel);

        // v3.4.0: kartu chat tinggal di dalam flyout chat rel —
        // bukan lagi penghuni tetap jendela utama.
        _chatFlyout.Host.Controls.Add(root);
    }

    private async Task InitChatAsync()
    {
        try
        {
            // Folder data persisten: cookie & login awet antar jalan.
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jembatan", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            if (IsDisposed) return;

            _chatErrorLabel.Visible = false;
            // v3.1.0 (wadah): latar bawaan web view = lavender kanvas
            // aplikasi — halaman yang sedang dimuat tidak lagi
            // berkedip putih sebelum isinya tiba.
            _webView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Bg };
            _chatBody.Controls.Add(_webView);
            await _webView.EnsureCoreWebView2Async(env);
            if (IsDisposed) return;

            var core = _webView.CoreWebView2;
            core.Settings.UserAgent = DesktopChromeUa;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            // v3.1.0 (wadah): terapkan zoom teks tersimpan.
            _webView.ZoomFactor = Settings.ChatZoomPercent / 100.0;
            // Jendela baru (mis. popup/redirect login) dibuka di panel
            // yang sama, tidak memunculkan jendela terpisah.
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
            };
            // Halaman pertama selesai dimuat → label memuat hilang.
            // (Status di strip chat adalah status KONEKSI jembatan —
            // tidak ditimpa status muat halaman.)
            core.NavigationCompleted += (s, e) =>
            {
                if (!IsDisposed && _chatLoadingLabel != null)
                    _chatLoadingLabel.Visible = false;
            };
            // v3.1.0 (perilaku): mulai pengamat balasan selesai.
            _chatWatch = new System.Windows.Forms.Timer { Interval = 2500 };
            _chatWatch.Tick += ChatWatchTick;
            _chatWatch.Start();
            core.Navigate("https://muse.ai");
            Log("Panel chat Muse dibuka (muse.ai).");
        }
        catch (Exception ex)
        {
            // Runtime WebView2 tidak ada / gagal — jembatan utama
            // tetap jalan penuh; panel hanya menampilkan pesan galat.
            if (_webView != null)
            {
                _chatBody.Controls.Remove(_webView);
                _webView.Dispose();
                _webView = null;
            }
            if (!IsDisposed)
            {
                Log("Panel chat gagal dibuka: " + ex.Message);
                ShowChatError(ex.Message);
            }
        }
        finally
        {
        }
    }

    private void ShowChatError(string message)
    {
        if (_chatLoadingLabel != null) _chatLoadingLabel.Visible = false;
        _chatErrorLabel.Text =
            "Panel chat gagal dibuka.\n\n" + message +
            "\n\nJembatan utama tetap berjalan normal. Coba buka ulang MuseLink; bila tetap gagal, chat bisa dibuka lewat muse.ai di browser.";
        _chatErrorLabel.Visible = true;
    }

    // v3.1.0: zoom teks chat dari gugus strip (− / persen / +).
    // Nilai persen disimpan di registry; ZoomFactor web view
    // diterapkan seketika. Bila web view belum siap, nilai
    // tersimpan diterapkan saat InitChatAsync selesai.
    private void AdjustZoom(int delta)
    {
        int v = Math.Min(200, Math.Max(50, Settings.ChatZoomPercent + delta));
        Settings.ChatZoomPercent = v;
        if (_chatZoomLabel != null) _chatZoomLabel.Text = v + "%";
        if (_webView != null) _webView.ZoomFactor = v / 100.0;
    }

    // v3.1.0: pengamat balasan selesai. Halaman chat diamati dari
    // LUAR lewat keberadaan tombol "Stop" (tombol berhenti
    // membangkitkan) — muncul = Muse sedang menulis, hilang
    // sesudah muncul = balasan selesai. Ini skrip FUNGSI, bukan
    // gaya: isi & tampilan halaman tidak diubah sama sekali.
    // Rapuh terhadap perubahan situs, jadi kegagalan beruntun
    // menonaktifkan pengamat ini sendiri tanpa mengganggu apa pun.
    private async void ChatWatchTick(object sender, EventArgs e)
    {
        var core = _webView?.CoreWebView2;
        if (core == null || IsDisposed) return;
        try
        {
            string r = await core.ExecuteScriptAsync(
                "!!document.querySelector('button[aria-label*=\"Stop\" i]')");
            bool generating = r != null && r.Trim() == "true";
            _watchFailures = 0;
            if (_wasGenerating && !generating)
            {
                Log("Balasan chat selesai.");
                NotifyReplyDone();
            }
            _wasGenerating = generating;
        }
        catch
        {
            if (++_watchFailures >= 3)
            {
                _chatWatch?.Stop();
                Log("Pengamat balasan selesai dinonaktifkan (bentuk halaman berubah).");
            }
        }
    }

    // Balon tray hanya bila jendela utama bukan yang sedang
    // dipandang user — kalau sedang dipandang, log saja cukup.
    private void NotifyReplyDone()
    {
        if (Visible && WindowState != FormWindowState.Minimized &&
            Form.ActiveForm == this) return;
        _trayIcon?.ShowBalloonTip(4000,
            "MuseLink",
            "Balasan chat Muse selesai.",
            ToolTipIcon.Info);
    }

    /// <summary>PUTUSKAN / Sambung lagi — kini dari jendela Pengaturan.</summary>
    internal void ToggleServer()
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            SetStopped("Terputus — akses Muse berhenti");
            Log("Listener DIMATIKAN oleh kamu — akses Muse berhenti total.");
        }
        else
        {
            try
            {
                _server.Start();
                SetWaiting();
                Log("Listener dinyalakan lagi — Muse bisa terhubung.");
            }
            catch (Exception ex)
            {
                SetStopped("Gagal memulai: " + ex.Message);
                Log("GAGAL memulai layanan: " + ex.Message);
            }
        }
        _settingsForm?.RefreshServerButton();
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
        return "\"" + Application.ExecutablePath + "\" --auto";
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
                      Application.ExecutablePath + "\\\" --auto\" /SC ONLOGON /RL HIGHEST /F";
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
                "    shell.Run \"\"\"" + Application.ExecutablePath + "\"\" --auto\", 0, False\r\n" +
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
                "        shell.Run \"\"\"" + Application.ExecutablePath + "\"\" --auto\", 0, False\r\n" +
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
                    Close();
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

    private void SetWaiting()
    {
        _statusDot.DotColor = DotWaiting;
        _statusTextFull = "Menunggu koneksi";
        _statusLabel.Text = _statusTextFull;
        _statusState = "waiting";
        RefreshStatusPill();
    }

    private void SetStopped(string text)
    {
        _statusDot.DotColor = DotStopped;
        _statusTextFull = text;
        _statusLabel.Text = text;
        _statusState = "stopped";
        RefreshStatusPill();
    }

    // Campur dua warna: t = porsi warna pertama (0..1).
    private static Color Blend(Color a, Color b, double t)
    {
        return Color.FromArgb(
            (int)(a.R * t + b.R * (1 - t)),
            (int)(a.G * t + b.G * (1 - t)),
            (int)(a.B * t + b.B * (1 - t)));
    }

    // Warna pill status mengikuti keadaan: latar = warna titik
    // dipudarkan ke tinta panel (v3.2.0 — panel kini gelap seperti
    // log); teks terang InkOnDark agar terbaca.
    private void RefreshStatusPill()
    {
        if (_statusPill == null) return;
        Color dot = _statusState == "connected" ? LiveGreen
                  : _statusState == "stopped" ? DotStopped : DotWaiting;
        Color tint = Blend(dot, LogBg, 0.30);
        _statusPill.FillColor = tint;
        _statusPill.BorderColor = Blend(dot, LogBg, 0.55);
        if (_statusDot != null) _statusDot.BackColor = tint;
        if (_statusLabel != null) { _statusLabel.BackColor = tint; _statusLabel.ForeColor = InkOnDark; }
        _statusPill.Invalidate();
        // Titik & teks status yang sama dicerminkan ke bar judul,
        // rel, dan strip chat.
        if (_statusDot != null) _statusDot.DotColor = dot;
        if (_titleDot != null) _titleDot.DotColor = dot;
        if (_railStatusDot != null) _railStatusDot.DotColor = dot;
        if (_chatStripStatus != null) _chatStripStatus.Text = _statusTextFull;
        // v2.9.1: teks di dalam pill dipersingkat agar tidak
        // terpotong elipsis (pill hanya 228px).
        if (_statusLabel != null) _statusLabel.Text = ShortenPillText(_statusTextFull);
    }

    // v3.4.1: pil relay di kartu kepala (mockup Figma) — hijau
    // muda bertuliskan "Terhubung — relay aman" bila
    // TailscaleRelay memberi nama relay; selain itu pil merah
    // muda bertuliskan keadaan sebenarnya. Hanya menggambar
    // ulang saat keadaannya berubah.
    private void RefreshRelayPill(string relay)
    {
        if (_relayPill == null) return;
        bool ok = !string.IsNullOrWhiteSpace(relay);
        string state = ok ? "ok" : "off";
        if (state == _relayPillState) return;
        _relayPillState = state;
        if (ok)
        {
            _relayPill.FillColor = RelayOkBg;
            _relayPill.BorderColor = LiveGreen;
            if (_relayDot != null) { _relayDot.BackColor = RelayOkBg; _relayDot.DotColor = LiveGreen; }
            if (_relayLabel != null)
            {
                _relayLabel.BackColor = RelayOkBg;
                _relayLabel.ForeColor = RelayOkInk;
                _relayLabel.Text = "Terhubung — relay aman";
            }
        }
        else
        {
            _relayPill.FillColor = ErrorBg;
            _relayPill.BorderColor = Red;
            if (_relayDot != null) { _relayDot.BackColor = ErrorBg; _relayDot.DotColor = Red; }
            if (_relayLabel != null)
            {
                _relayLabel.BackColor = ErrorBg;
                _relayLabel.ForeColor = ErrorInk;
                _relayLabel.Text = "Relay tidak terhubung";
            }
        }
        _relayPill.Invalidate();
    }

    // v3.2.0 (koreksi #6): denyut titik status saat Terhubung —
    // warna titik bernapas halus antara hijau penuh dan hijau
    // muda, ±3 detik per siklus. Keadaan lain diam (warna dari
    // RefreshStatusPill yang berlaku).
    private void PulseTick(object sender, EventArgs e)
    {
        // v3.3.0 (koreksi user): denyut lama (campuran warna pelan)
        // nyaris tak terlihat — diganti halo menyebar yang digambar
        // RoundDot mode GlowPulse; di sini cukup majukan fasenya.
        double phase = -1.0;
        if (_statusState == "connected")
        {
            _pulsePhase += 0.10;
            phase = (Math.Sin(_pulsePhase) + 1.0) / 2.0;
        }
        if (_statusDot != null) _statusDot.PulsePhase = phase;
        if (_titleDot != null) _titleDot.PulsePhase = phase;
        if (_railStatusDot != null) _railStatusDot.PulsePhase = phase;
    }

    // Satu kolom tile: caption mikro + nilai; kolom ke-2 dst
    // didahului garis pemisah tipis.
    // Satu sel tile grid 2x2: caption mikro huruf besar redup +
    // nilai semibold besar; pemisah vertikal & horizontal digambar
    // sekali dari sel pertama.
    private void MakeTile2(Control parent, int row, int col, string caption, string value, out Label valueLabel)
    {
        int x = 14 + col * 144;
        int y = 12 + row * 56;
        if (row == 0 && col == 0)
        {
            var divV = new Panel
            {
                Size = new Size(1, 92),
                Location = new Point(150, 12),
                BackColor = Hairline,
            };
            parent.Controls.Add(divV);
            _tileDividers.Add(divV);
            var divH = new Panel
            {
                Size = new Size(276, 1),
                Location = new Point(14, 62),
                BackColor = Hairline,
            };
            parent.Controls.Add(divH);
            _tileDividers.Add(divH);
        }
        var cap = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(x, y),
        };
        parent.Controls.Add(cap);
        _tileCaptions.Add(cap);
        valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(x, y + 16),
        };
        parent.Controls.Add(valueLabel);
    }

    // Segarkan tile RELAY & UPTIME (VERSI statis). Relay membaca
    // cache 60 detik di AgentServer — panggilan ini murah.
    private void RefreshTiles()
    {
        try
        {
            if (_tileRelayValue != null)
            {
                string relayText = AgentServer.TailscaleRelay();
                _tileRelayValue.Text = relayText;
                RefreshRelayPill(relayText);
            }
            // v3.4.1: baris kartu AKSES mengikuti keadaan nyata —
            // server dari proses ini, watchdog dari hasil poll
            // berkala PollWatchdogOnce (latar, diringankan 30 dtk).
            if (_aksesServerValue != null)
                _aksesServerValue.Text = _server != null && _server.IsRunning ? "Berjalan" : "Berhenti";
            if (_aksesWatchdogValue != null)
                _aksesWatchdogValue.Text = _watchdogText;
            if (_tileUptimeValue != null)
            {
                var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
                _tileUptimeValue.Text = up.TotalHours >= 1
                    ? (int)up.TotalHours + "j " + up.Minutes + "m"
                    : Math.Max(0, up.Minutes) + "m";
            }
            if (_tileCaptureValue != null)
            {
                // v3.0.1: jangan tampilkan "gdi" sesaat di awal —
                // tampilkan "…" sampai mode tangkap menetap (DXGI
                // hidup, atau 6 detik berlalu = memang jalur GDI).
                if (!_captureSettled)
                {
                    if (DxgiCapture.Mode == "dxgi"
                        || (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds > 6)
                        _captureSettled = true;
                    else
                        _tileCaptureValue.Text = "…";
                }
                if (_captureSettled)
                    _tileCaptureValue.Text = DxgiCapture.ScaleMode == "gpu" ? "gpu" : DxgiCapture.Mode;
            }
            if (_tileRamValue != null) _tileRamValue.Text = SysSensors.RamText();
            if (_tileCpuValue != null) _tileCpuValue.Text = _cpuText;
            if (_tileGpuValue != null) _tileGpuValue.Text = _gpuText;
        }
        catch { /* tile hanya keterangan — jangan ganggu aplikasi */ }
    }

    // ---------- Sensor sistem (v3.0.1) ----------
    // Suhu GPU (nvidia-smi) & CPU (LibreHardwareMonitor) dibaca
    // di utas latar tiap ±10 detik; RefreshTiles hanya menyalin
    // teks terakhir. RAM dibaca langsung di RefreshTiles
    // (P/Invoke ringan). Semua jalur boleh gagal dengan aman —
    // baris menampilkan nilai terakhir yang bagus / "—".

    // v3.0.2: penjaga ditulis ulang setelah v3.0.1 terbukti diam
    // (CPU & GPU berhenti di "…"): timer kini FIELD (timer lokal
    // bisa dipungut GC dan berhenti diam-diam), init CPU berjalan
    // di utasnya sendiri (Open() LibreHardwareMonitor bisa lama /
    // menggantung — tidak lagi menahan polling GPU), dan hasil
    // pertama tiap sensor DICATAT ke log sebagai diagnostik.
    private System.Windows.Forms.Timer _sensorTimer;
    private volatile bool _cpuInitDone;
    private bool _sensorDiagLogged;

    private void StartSensorPoller()
    {
        _cpuSensor = new CpuTempSensor();
        System.Threading.Tasks.Task.Run(PollSensorsOnce);
        System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = false;
            try { ok = _cpuSensor != null && _cpuSensor.TryInit(); } catch { /* diag di sensor */ }
            _cpuInitDone = true;
            if (ok)
            {
                var c = _cpuSensor.Read();
                if (c.HasValue) _cpuText = c.Value + "°C";
                Log("Sensor CPU aktif: " + (c.HasValue ? c.Value + "°C" : "belum terbaca")
                    + " [" + _cpuSensor.InitDiag + "].");
            }
            else
            {
                Log("Sensor CPU tidak aktif: " + (_cpuSensor != null ? _cpuSensor.InitDiag : "?"));
            }
        });
        _sensorTimer = new System.Windows.Forms.Timer { Interval = 10000 };
        _sensorTimer.Tick += (s, e) =>
        {
            if (!_sensorPollBusy)
                System.Threading.Tasks.Task.Run(PollSensorsOnce);
        };
        _sensorTimer.Start();
    }

    private void PollSensorsOnce()
    {
        if (_sensorPollBusy) return;
        _sensorPollBusy = true;
        try
        {
            var g = SysSensors.GpuText();
            if (g != null) _gpuText = g;
            if (_cpuInitDone && _cpuSensor != null)
            {
                var c = _cpuSensor.Read();
                if (c.HasValue) _cpuText = c.Value + "°C";
                else if (_cpuSensor.Failed) _cpuText = "—";
            }
            if (!_sensorDiagLogged)
            {
                _sensorDiagLogged = true;
                Log("Sensor GPU: " + (g != null ? g : "gagal [" + SysSensors.LastDiag + "]") + ".");
            }
            PollWatchdogOnce();
        }
        catch { /* sensor hanya keterangan — jangan ganggu aplikasi */ }
        finally { _sensorPollBusy = false; }
    }

    // v3.4.1: keadaan tugas "MuseLink Watchdog" untuk baris
    // Watchdog di kartu AKSES — dicek lewat schtasks di utas
    // latar, diringankan jadi sekali per ±30 detik; hasilnya
    // hanya teks keterangan ("Aktif" / "Tidak aktif").
    private void PollWatchdogOnce()
    {
        long now = Environment.TickCount64;
        if (now - _watchdogPollTick < 30000) return;
        _watchdogPollTick = now;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/Query /TN \"" + WatchdogTaskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var proc = Process.Start(psi))
            {
                proc.StandardOutput.ReadToEnd();
                if (proc.WaitForExit(4000))
                    _watchdogText = proc.ExitCode == 0 ? "Aktif" : "Tidak aktif";
            }
        }
        catch { /* baris watchdog hanya keterangan */ }
    }

    // ---------- Bar judul, panel log & strip status (v2.8.0) ----------

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    // Seret jendela dari bar judul tanpa bingkai: trik klasik
    // ReleaseCapture + WM_NCLBUTTONDOWN dengan HTCAPTION.
    private void TitleBarMouseDown(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { ToggleMaximize(); return; }
        ReleaseCapture();
        SendMessageDrag(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    // v3.2.1: EntryPoint WAJIB "SendMessage" — nama terkelola
    // "SendMessageDrag" tidak ada di user32.dll, jadi setiap
    // seretan bar judul melempar EntryPointNotFoundException
    // (temuan verifikasi v3.2.0; bug lama yang tak pernah
    // terpicu karena jendela hampir selalu termaksimalkan).
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessage")]
    private static extern IntPtr SendMessageDrag(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessage")]
    private static extern IntPtr SendMessageScroll(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // v3.3.0 (koreksi user): scrollbar log disembunyikan — gulir
    // roda mouse digerakkan manual per 3 baris (EM_LINESCROLL).
    private void LogBoxMouseWheel(object sender, MouseEventArgs e)
    {
        if (_logBox == null) return;
        int lines = e.Delta > 0 ? -3 : 3;
        SendMessageScroll(_logBox.Handle, 0x00B6, (IntPtr)lines, IntPtr.Zero);
    }

    // v3.3.0 (koreksi user): klik tombol taskbar harus men-toggle
    // minimize (WS_MINIMIZEBOX) dan jendela berbingkai ini perlu
    // bayangan (CS_DROPSHADOW) agar terpisah dari desktop.
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x20000;      // WS_MINIMIZEBOX
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyWindowFrame();
    }

    private FormWindowState _frameState = (FormWindowState)(-1);

    // v3.3.0 (koreksi user): mode jendela kecil diberi pembatas
    // tegas terhadap desktop — bingkai 1px + sudut membulat 14px;
    // mode maksimal tetap penuh tanpa bingkai.
    private void ApplyWindowFrame()
    {
        if (WindowState == _frameState && WindowState != FormWindowState.Normal) return;
        _frameState = WindowState;
        if (WindowState == FormWindowState.Maximized)
        {
            Padding = Padding.Empty;
            BackColor = Bg;
            if (Region != null) { Region.Dispose(); Region = null; }
        }
        else if (WindowState == FormWindowState.Normal)
        {
            Padding = new Padding(1);
            BackColor = RailHover;
            using (var path = RoundedRectPath(new Rectangle(0, 0, Width, Height), 14))
            {
                var old = Region;
                Region = new Region(path);
                if (old != null) old.Dispose();
            }
        }
    }

    private static GraphicsPath RoundedRectPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Buka / tutup chat MELAYANG (v3.4.0 — blueprint user: "chat
    /// buat seperti log aktivitas"): flyout WebView2 yang muncul
    /// menempel di kanan tombol chat rel dengan animasi geser +
    /// pudar, setinggi area konten jendela. Penjaga 350ms
    /// mencegah pemicu ganda dari pasangan MouseDown+Click.
    /// Saling eksklusif dengan log & Pengaturan (pola v3.2.2
    /// diperluas): membuka chat menutup keduanya.
    /// </summary>
    private void ToggleChatPanel()
    {
        if (_chatFlyout == null || _railChatBtn == null) return;
        long now = Environment.TickCount64;
        if (now - _lastChatToggleTick < 350) return;
        _lastChatToggleTick = now;
        if (_chatFlyout.Visible)
        {
            _chatFlyout.HideAnimated();
        }
        else
        {
            if (_logFlyout != null && _logFlyout.Visible)
                _logFlyout.HideImmediate();
            if (_settingsForm != null && !_settingsForm.IsDisposed && _settingsForm.Visible)
                _settingsForm.HideImmediate();
            var anchor = _railChatBtn.RectangleToScreen(_railChatBtn.ClientRectangle);
            int titleH = _titleBar != null ? _titleBar.Height : 40;
            int topY = PointToScreen(new Point(0, titleH)).Y;
            int height = Math.Max(200, ClientSize.Height - titleH);
            _chatFlyout.ShowAnchored(anchor, this, height, topY);
        }
        RefreshRail();
    }

    /// <summary>
    /// Buka / tutup log MELAYANG (v3.0.1): jendela tinta yang
    /// muncul menempel di kanan tombol log rel dengan animasi
    /// geser + pudar. Penjaga 350ms mencegah pemicu ganda dari
    /// pasangan MouseDown+Click pada tombol yang sama. Keadaan
    /// strip & rel diselaraskan lewat VisibleChanged flyout.
    /// </summary>
    private void ToggleLogPanel()
    {
        if (_logFlyout == null || _railLogBtn == null) return;
        long now = Environment.TickCount64;
        if (now - _lastLogToggleTick < 350) return;
        _lastLogToggleTick = now;
        if (_logFlyout.Visible)
        {
            _logFlyout.HideAnimated();
        }
        else
        {
            // v3.2.2 (koreksi user): flyout bar kiri SALING EKSKLUSIF
            // — membuka log menutup Pengaturan yang sedang terbuka,
            // tidak boleh menumpuk. v3.4.0: flyout chat ikut ditutup.
            if (_settingsForm != null && !_settingsForm.IsDisposed && _settingsForm.Visible)
                _settingsForm.HideImmediate();
            if (_chatFlyout != null && _chatFlyout.Visible)
                _chatFlyout.HideImmediate();
            var anchor = _railLogBtn.RectangleToScreen(_railLogBtn.ClientRectangle);
            _logFlyout.ShowAnchored(anchor, this);
        }
        RefreshRail();
    }

    // v2.9.1: versi singkat teks status khusus pill.
    private static string ShortenPillText(string text)
    {
        const string prefix = "Terhubung \u2022 aktivitas terakhir ";
        if (text.StartsWith(prefix)) return "Terhubung \u2022 " + text.Substring(prefix.Length);
        if (text.StartsWith("Terputus")) return "Terputus";
        return text;
    }

    private int _errorCount;

    /// <summary>Lencana jumlah error sesi ini di kepala panel log.</summary>
    private void RefreshErrorBadge()
    {
        if (_errBadge == null) return;
        _errBadge.Visible = _errorCount > 0;
        // v3.0.0: lencana duduk di kepala log TINTA — tint dicampur
        // ke LogBg, teks merah diperang ke arah putih, dan titik
        // error di tombol log rel ikut menyala.
        Color tint = Blend(Red, LogBg, 0.30);
        if (_errBadgeLabel != null)
        {
            _errBadgeLabel.Text = _errorCount + " error";
            _errBadgeLabel.ForeColor = Blend(Red, Color.White, 0.45);
            _errBadgeLabel.BackColor = tint;
        }
        _errBadge.FillColor = tint;
        _errBadge.BorderColor = Blend(Red, LogBg, 0.15);
        _errBadge.BackColor = LogBg;
        if (_railLogDot != null) _railLogDot.Visible = _errorCount > 0;
    }

    // ---------- Pengaturan, tema & jangan tidur (v2.5.0) ----------

    internal void OpenSettings()
    {
        // v3.2.0 (koreksi #8): Pengaturan kini flyout yang menempel
        // pada tombol roda gigi rel — klik saat terbuka menutupnya
        // (perilaku saklar, sama seperti tombol log).
        if (_settingsForm != null && !_settingsForm.IsDisposed && _settingsForm.Visible)
        {
            _settingsForm.HideAnimated();
            return;
        }
        if (_settingsForm == null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(this);
            _settingsForm.FormClosed += (s, e) => _settingsForm = null;
        }
        // v3.2.2 (koreksi user): flyout bar kiri SALING EKSKLUSIF —
        // membuka Pengaturan menutup log yang sedang terbuka.
        // v3.4.0: flyout chat ikut ditutup.
        if (_logFlyout != null && _logFlyout.Visible)
            _logFlyout.HideImmediate();
        if (_chatFlyout != null && _chatFlyout.Visible)
            _chatFlyout.HideImmediate();
        var anchor = _railSettingsBtn != null
            ? _railSettingsBtn.RectangleToScreen(_railSettingsBtn.ClientRectangle)
            : RectangleToScreen(ClientRectangle);
        _settingsForm.ShowAnchored(anchor, this);
    }

    /// <summary>Tempelkan roda gigi bar judul ke pojok kanan atas jendela ini.</summary>


    /// <summary>Dipanggil jendela Pengaturan / endpoint /theme saat tema dipilih.</summary>
    internal void SetTheme(string theme)
    {
        try { Settings.Theme = theme; } catch { /* registry gagal — tema sesi ini tetap berganti */ }
        ApplyTheme();
        Log("Tema tampilan: " + (theme == "light" ? "Terang" : theme == "dark" ? "Gelap" : "Ikuti perangkat"));
    }

    /// <summary>Dipanggil jendela Pengaturan saat saklar Jangan tidur berubah.</summary>
    internal void ApplyKeepAwake(bool on)
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

    /// <summary>
    /// Cat ulang jendela utama (dan Pengaturan bila terbuka)
    /// mengikuti tema efektif AppTheme saat ini.
    /// </summary>
    internal void ApplyTheme()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ApplyTheme));
            return;
        }
        BackColor = Bg;
        if (_leftRoot != null) _leftRoot.BackColor = Panel;
        if (_chatFlyout != null)
        {
            _chatFlyout.BackColor = Bg;
            _chatFlyout.Host.BackColor = Bg;
        }
        if (_headerCard != null)
        {
            _headerCard.FillColor = Card;
            _headerCard.BorderColor = Hairline;
            _headerCard.BackColor = Panel;
        }
        if (_logCard != null) _logCard.BackColor = LogBg;
        if (_chatBody != null)
        {
            _chatBody.FillColor = Card;
            _chatBody.BorderColor = Hairline;
            _chatBody.BackColor = Bg;
        }
        if (_chatStrip != null)
        {
            _chatStrip.FillColor = Ink;
            _chatStrip.BorderColor = Ink;
            _chatStrip.BackColor = Bg;
        }
        if (_chatStripStatus != null) { _chatStripStatus.ForeColor = InkOnDark; _chatStripStatus.BackColor = Ink; }
        if (_chatRoot != null) _chatRoot.BackColor = Bg;
        PaintThemedChildren(this);
        // v3.2.0 (koreksi #1): panel kendali = tema Log Aktivitas —
        // latar tinta LogBg, kartu InkCard bertepi RailHover, teks
        // terang. Dicat eksplisit di sini (sesudah PaintThemedChildren).
        if (_leftRoot != null) _leftRoot.BackColor = LogBg;
        if (_headerCard != null) { _headerCard.FillColor = LogBg; _headerCard.BorderColor = LogBg; _headerCard.BackColor = LogBg; }
        if (_titleLabel != null) { _titleLabel.ForeColor = InkOnDark; _titleLabel.BackColor = LogBg; }
        if (_statusLabel != null) { _statusLabel.ForeColor = InkOnDark; _statusLabel.BackColor = _statusPill.FillColor; }
        if (_statusDot != null) _statusDot.BackColor = _statusPill.FillColor;
        if (_portValue != null) { _portValue.ForeColor = InkOnDark; _portValue.BackColor = LogBg; }
        if (_netValue != null) { _netValue.ForeColor = InkOnDark; _netValue.BackColor = LogBg; }
        if (_modeValue != null)
        {
            _modeValue.ForeColor = Settings.IsElevated() ? Gold : InkOnDark;
            _modeValue.BackColor = LogBg;
        }
        if (_tilesCard != null)
        {
            // v3.4.1: kartu STATUS kini kartu sungguhan — isi
            // InkCard bertepi RailHover di atas panel tinta.
            _tilesCard.FillColor = InkCard; _tilesCard.BorderColor = RailHover; _tilesCard.BackColor = LogBg;
            if (_tilesCard.Parent != null) _tilesCard.Parent.BackColor = LogBg;
        }
        // v3.4.1: latar keterangan mengikuti induknya — di dalam
        // kartu STATUS/AKSES = InkCard, di kartu kepala = LogBg.
        foreach (var c in _tileCaptions) { c.ForeColor = InkFaint; c.BackColor = c.Parent == _tilesCard || c.Parent == _aksesCard ? InkCard : LogBg; }
        foreach (var d in _tileDividers) d.BackColor = RailHover;
        if (_tileRelayValue != null) { _tileRelayValue.ForeColor = InkOnDark; _tileRelayValue.BackColor = InkCard; }
        if (_tileUptimeValue != null) { _tileUptimeValue.ForeColor = InkOnDark; _tileUptimeValue.BackColor = InkCard; }
        if (_tileCaptureValue != null) { _tileCaptureValue.ForeColor = InkOnDark; _tileCaptureValue.BackColor = InkCard; }
        if (_tileVersionValue != null) { _tileVersionValue.ForeColor = InkOnDark; _tileVersionValue.BackColor = InkCard; }
        if (_tileRamValue != null) { _tileRamValue.ForeColor = InkOnDark; _tileRamValue.BackColor = InkCard; }
        if (_tileCpuValue != null) { _tileCpuValue.ForeColor = InkOnDark; _tileCpuValue.BackColor = InkCard; }
        if (_tileGpuValue != null) { _tileGpuValue.ForeColor = InkOnDark; _tileGpuValue.BackColor = InkCard; }
        if (_logoBox != null) _logoBox.BackColor = LogBg;
        if (_chatStripRight != null) _chatStripRight.BackColor = Ink;
        RefreshStatusPill();
        // v3.4.1: pil relay & kartu AKSES — latar titik/teks pil
        // disamakan dengan isi pilnya (PaintThemedChildren
        // mengecat RoundDot polos ke Card), kartu AKSES dicat
        // seperti kartu STATUS, lalu keadaan relay digambar ulang.
        if (_relayDot != null && _relayPill != null) _relayDot.BackColor = _relayPill.FillColor;
        if (_relayLabel != null && _relayPill != null) _relayLabel.BackColor = _relayPill.FillColor;
        if (_aksesCard != null) { _aksesCard.FillColor = InkCard; _aksesCard.BorderColor = RailHover; _aksesCard.BackColor = LogBg; }
        _relayPillState = "";
        RefreshRelayPill(AgentServer.TailscaleRelay());
        if (_logBox != null)
        {
            _logBox.BackColor = LogBg;
            _logBox.ForeColor = InkOnDark;
            int pos = _logBox.SelectionStart;
            int len = _logBox.SelectionLength;
            _logBox.SelectAll();
            _logBox.SelectionColor = InkOnDark;
            _logBox.Select(pos, len);
        }
        if (_chatLoadingLabel != null) { _chatLoadingLabel.ForeColor = InkDim; _chatLoadingLabel.BackColor = Card; }
        if (_chatErrorLabel != null) { _chatErrorLabel.ForeColor = ErrorInk; _chatErrorLabel.BackColor = ErrorBg; }
        // v3.0.0: bar judul = kanvas bergradasi (v3.2.0); rel dan
        // panel log = tinta — dicat SETELAH PaintThemedChildren
        // agar tidak ditimpa aturan panel polosnya. Strip status
        // bawah PENSIUN di v3.2.0 (koreksi #5 — isinya duplikat).
        if (_titleBar != null) { _titleBar.BackColor = Bg; _titleBar.Invalidate(); }
        if (_barTitle != null) { _barTitle.ForeColor = Ink; _barTitle.BackColor = Color.Transparent; }
        if (_titleLogo != null) _titleLogo.BackColor = Color.Transparent;
        if (_titleDot != null) _titleDot.BackColor = TitleDotBg;
        if (_rail != null) _rail.BackColor = Rail;
        if (_railLogo != null) _railLogo.BackColor = Rail;
        if (_railStatusDot != null) _railStatusDot.BackColor = Rail;
        if (_railSettingsBtn != null) _railSettingsBtn.BackColor = Rail;
        if (_railSettingsGlyph != null) { _railSettingsGlyph.ForeColor = InkOnDark; _railSettingsGlyph.BackColor = Rail; }
        RefreshRail();
        if (_logFlyout != null)
        {
            _logFlyout.BackColor = LogBg;
            _logFlyout.HeaderHost.BackColor = LogBg;
            _logFlyout.BodyHost.BackColor = LogBg;
        }
        if (_logTitleLabel != null) { _logTitleLabel.ForeColor = InkOnDark; _logTitleLabel.BackColor = LogBg; }
        if (_logHeader != null) _logHeader.BackColor = LogBg;
        if (_logHair != null) _logHair.BackColor = RailHover;
        if (_logCard != null) _logCard.BackColor = LogBg;
        RefreshErrorBadge();
        _settingsForm?.ApplyTheme();
        Invalidate(true);
    }

    private void PaintThemedChildren(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is ToggleSwitch ts) ts.BackColor = Card;
            else if (c is RoundDot rd) rd.BackColor = Card;
            else if (c is Panel p && p is not RoundedPanel)
            {
                string tag = p.Tag as string;
                p.BackColor = tag == "cardbg" ? Card : tag == "panelbg" ? Panel : tag == "inkbg" ? LogBg : Bg;
            }
            PaintThemedChildren(c);
        }
    }

    // Clipboard berubah → catatan kejadian (isi tidak dicatat,
    // hanya panjang teksnya; isinya urusan endpoint /clipboard).
    private const int WmClipboardUpdate = 0x031D;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { AddClipboardFormatListener(Handle); } catch { /* pemantau clipboard hanya bonus */ }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { RemoveClipboardFormatListener(Handle); } catch { /* abaikan */ }
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
        {
            int len = -1;
            try { if (Clipboard.ContainsText()) len = Clipboard.GetText().Length; } catch { /* clipboard terkunci sesaat */ }
            EventLog.Add("clipboard", len >= 0
                ? "teks clipboard berubah (" + len + " karakter)"
                : "clipboard berubah (bukan teks)");
        }
        // v2.8.0: tanpa bingkai Windows — tepi jendela tetap bisa
        // diseret untuk mengubah ukuran lewat WM_NCHITTEST
        // (HTLEFT=10 … HTBOTTOMRIGHT=17).
        if (m.Msg == 0x0084 && WindowState == FormWindowState.Normal)
        {
            int lp = m.LParam.ToInt32();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            const int g = 6;
            bool l = p.X <= g, r = p.X >= Width - g - 1, t = p.Y <= g, b = p.Y >= Height - g - 1;
            int code = 0;
            if (t && l) code = 13; else if (t && r) code = 14;
            else if (b && l) code = 16; else if (b && r) code = 17;
            else if (l) code = 10; else if (r) code = 11;
            else if (t) code = 12; else if (b) code = 15;
            if (code != 0) { m.Result = (IntPtr)code; return; }
        }
        base.WndProc(ref m);
    }

    // Dipanggil AgentServer tiap ada permintaan masuk dari Muse.
    private void OnActivity()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(OnActivity));
            return;
        }
        _statusDot.DotColor = LiveGreen;
        _statusTextFull = "Terhubung • aktivitas terakhir " + DateTime.Now.ToString("HH:mm:ss");
        _statusLabel.Text = _statusTextFull;
        _statusState = "connected";
        RefreshStatusPill();
    }

    /// <summary>Menulis satu baris ke log jendela (aman dari thread mana pun).</summary>
    public void Log(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Log(message)));
            return;
        }
        // v2.4.2: baris log dipotong rapi — perintah yang sangat
        // panjang (mis. naskah PowerShell) tidak lagi membanjiri
        // panel; intinya tetap terbaca.
        // v2.5.7: token raksasa tanpa spasi (mis. Base64) diringkas
        // lebih dulu, lalu pewarnaan ala Vercel — cap waktu redup,
        // isi netral, galat merah, baris "selesai" redup. Berkas
        // log menerima baris yang sama ringkasnya.
        if (message == null) message = "";
        message = SummarizeTokens(message);
        if (message.Length > 140)
            message = message.Substring(0, 140) + "…";
        if (!_logSpacingApplied) { _logSpacingApplied = true; ApplyLogSpacing(); }
        string stamp = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = InkFaint;
        _logBox.AppendText(stamp);
        var lineColor = LogColorFor(message);
        _logBox.SelectionColor = lineColor;
        _logBox.AppendText(message + Environment.NewLine);
        if (lineColor == RedOnDark) { _errorCount++; RefreshErrorBadge(); }
        AppendLogToFile(stamp + message);
        if (_logBox.Lines.Length > 500)
        {
            var lines = _logBox.Lines;
            _logBox.Lines = lines.Skip(lines.Length - 400).ToArray();
            _logSpacingApplied = false; // teks diganti total — terapkan ulang di Log berikutnya
        }
        // v3.3.2: pratinjau panel mengikuti log — baris-baris
        // terakhir yang tak kosong. v3.4.0: kolom AKTIVITAS
        // TERAKHIR kini lebar & tinggi (kolom kanan panel utama),
        // jadi pratinjau diisi sampai 60 baris agar kolom terisi.
        if (_panelLog != null)
        {
            var ll = _logBox.Lines.Where(s => s.Length > 0).ToArray();
            int take = Math.Min(60, ll.Length);
            _panelLog.Text = take > 0
                ? string.Join(Environment.NewLine, ll.Skip(ll.Length - take))
                : "";
            // v3.5.0: panel AKTIVITAS TERAKHIR selalu mengikuti
            // baris terbaru (auto-follow) — karet diparkir di akhir
            // teks lalu digulir ke sana setiap baris baru masuk.
            _panelLog.SelectionStart = _panelLog.TextLength;
            _panelLog.ScrollToCaret();
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // Token tanpa spasi yang sangat panjang (Base64 dsb) diganti
    // ringkasan — panel log tidak dibanjiri teks acak.
    private static string SummarizeTokens(string s)
    {
        var parts = s.Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 60)
                parts[i] = "(diringkas " + parts[i].Length + " karakter)";
        return string.Join(" ", parts);
    }

    // Warna isi baris log — v3.1.0 MEMPERBAIKI skema lama: skema
    // v2.5.7 ditulis untuk latar TERANG (Ink/InkDim/Red di atas
    // putih), tapi sejak v3.0.0 log duduk di sumur TINTA gelap —
    // baris normal dicat Ink yang SAMA PERSIS dengan LogBg
    // (tak terlihat sama sekali), baris rutin InkDim ±1,9:1 dan
    // galat Red ±1,7:1 (nyaris tak terbaca). Skema gelap kini:
    // galat salmon terang, rutin lavender redup, normal nyaris
    // putih — cap waktu tetap InkFaint.
    private Color LogColorFor(string m)
    {
        string l = m.ToLowerInvariant();
        if (l.Contains("gagal") || l.Contains("error") || l.Contains("timeout")) return RedOnDark;
        // Baris rutin bervolume tinggi diredupkan supaya kejadian
        // penting (stream, koneksi, galat) yang menonjol.
        if (l.StartsWith("selesai") || l.StartsWith("===") ||
            l.StartsWith("jalanin:") || l.StartsWith("geser mouse") ||
            l.StartsWith("klik ") || l.StartsWith("bingkai instan") ||
            l.StartsWith("daftar jendela") || l.StartsWith("konteks instan") ||
            l.StartsWith("info sistem") || l.StartsWith("fokus ke jendela") ||
            l.StartsWith("unggah berkas") || l.StartsWith("stream berhenti"))
            return InkFaint;
        return InkOnDark;
    }

    // ---------- Jarak antar baris log (v2.7.6) ----------
    // RichTextBox tidak punya API terkelola untuk jarak baris —
    // diatur lewat PARAFORMAT2 (EM_SETPARAFORMAT) sekali di Log
    // pertama. Kosmetik murni: kegagalan apa pun diabaikan.
    private bool _logSpacingApplied;

    private void ApplyLogSpacing()
    {
        try
        {
            var fmt = new PARAFORMAT2
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(PARAFORMAT2)),
                dwMask = 0x00000100, // PFM_LINESPACING
                bLineSpacingRule = 5, // persis — dyLineSpacing dlm 1/20 pt
                dyLineSpacing = 300, // 15pt untuk font Consolas 9,5pt
                rgxTabs = new int[32],
            };
            SendMessagePara(_logBox.Handle, 0x0444, new IntPtr(4), ref fmt); // EM_SETPARAFORMAT + SCF_ALL
        }
        catch { /* abaikan */ }
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private struct PARAFORMAT2
    {
        public int cbSize;
        public uint dwMask;
        public short wNumbering;
        public short wReserved;
        public int dxStartIndent;
        public int dxRightIndent;
        public int dxOffset;
        public short wAlignment;
        public short cTabCount;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValArray, SizeConst = 32)]
        public int[] rgxTabs;
        public int dySpaceBefore;
        public int dySpaceAfter;
        public int dyLineSpacing;
        public short sStyle;
        public byte bLineSpacingRule;
        public byte bOutlineLevel;
        public short wShadingWeight;
        public short wShadingStyle;
        public short wNumberingStart;
        public short wNumberingStyle;
        public short wNumberingTab;
        public short wBorderSpace;
        public short wBorderWidth;
        public short wBorders;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern IntPtr SendMessagePara(IntPtr hWnd, int msg, IntPtr wParam, ref PARAFORMAT2 lParam);

    // ---------- Log persisten (v2.3.2) ----------
    // Setiap baris log jendela juga ditulis ke berkas
    // %LOCALAPPDATA%\Jembatan\jembatan.log (folder internal — nama
    // tidak ikut berganti) agar riwayat tidak hilang saat aplikasi
    // ditutup. Semua IO dibungkus try/catch yang menelan galat:
    // menulis log TIDAK BOLEH menjatuhkan aplikasi.

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
}
