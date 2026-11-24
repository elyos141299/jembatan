using System;
using System.Diagnostics;
using System.Drawing;
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
    private static Color Hairline => AppTheme.Hairline;
    private static Color Accent => AppTheme.Accent;
    private static Color Green => AppTheme.Green;
    private static Color Red => AppTheme.Red;
    private static Color LightFill => AppTheme.LightFill;
    private static Color DotWaiting => AppTheme.DotWaiting;
    private static Color DotStopped => AppTheme.DotStopped;
    private static Color ErrorBg => AppTheme.ErrorBg;
    private static Color ErrorInk => AppTheme.ErrorInk;

    private readonly AgentServer _server;
    private RoundDot _statusDot;
    private Label _statusLabel;
    private RichTextBox _logBox;
    private Label _modeLabel;
    private RoundedPanel _headerCard;
    private RoundedPanel _logCard;
    private Label _titleLabel;
    private Label _addrLabel;
    private Label _versionLabel;
    private RoundedPanel _statusPill;
    private PictureBox _logoBox;
    private RoundedPanel _tilesCard;
    private Label _tileRelayValue;
    private Label _tileUptimeValue;
    private Label _tileVersionValue;
    private readonly System.Collections.Generic.List<Label> _tileCaptions = new System.Collections.Generic.List<Label>();
    private readonly System.Collections.Generic.List<Panel> _tileDividers = new System.Collections.Generic.List<Panel>();
    private System.Windows.Forms.Timer _uiTimer;
    private string _statusState = "waiting";
    private Panel _leftRoot;
    private Panel _chatRoot;
    private SettingsForm _settingsForm;
    private CaptionGearForm _captionGear;
    private bool _autoStartEnabled;

    /// <summary>Keadaan jalan otomatis saat ini (untuk jendela Pengaturan).</summary>
    internal bool AutoStartEnabled => _autoStartEnabled;

    /// <summary>True bila listener sedang berjalan (untuk jendela Pengaturan).</summary>
    internal bool ServerRunning => _server.IsRunning;

    // Panel chat Muse: SplitContainer membagi jendela — Panel1
    // (kiri) = panel kendali, Panel2 (kanan) = chat WebView2.
    // Sejak v2.4.1 chat MENYATU permanen (permintaan pengguna):
    // tidak ada tombol buka/tutup — jendela selalu tampil lebar
    // dengan chat langsung di sebelah panel kendali.
    private SplitContainer _split;
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
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        var savedBounds = Settings.WindowBounds;
        if (savedBounds.HasValue && IsMostlyOnScreen(savedBounds.Value))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = savedBounds.Value;
            if (Settings.WindowMaximized)
                WindowState = FormWindowState.Maximized;
        }
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);

        BuildLayout();
        InitTrayIcon();
        FormClosing += OnMainFormClosing;

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
            Log("=== MuseLink v2.7.5 dimulai ===");
            InitStartOptions();
            if (Settings.KeepAwake)
            {
                NativeInput.SetKeepAwake(true);
                Log("Jangan tidur: AKTIF (dari pengaturan tersimpan).");
            }
            EventLog.Add("app-start", "MuseLink v2.7.5 dimulai");
            // Roda gigi melayang di bar judul, sebelah kiri minimize
            // (v2.5.1) — mengikuti gerak jendela utama.
            _captionGear = new CaptionGearForm(this);
            _captionGear.Show(this);
            SyncCaptionGear();
            LocationChanged += (s2, e2) => SyncCaptionGear();
            SizeChanged += (s2, e2) => SyncCaptionGear();
            VisibleChanged += (s2, e2) => SyncCaptionGear();
            ApplyTheme();
            RefreshTiles();
            _uiTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _uiTimer.Tick += (s2, e2) => RefreshTiles();
            _uiTimer.Start();
            // v2.4.1 (permintaan pengguna): chat Muse MENYATU —
            // panel kanan selalu tampil di sebelah panel kendali
            // sejak aplikasi mulai, tanpa tombol buka/tutup.
            // Lebar panel kiri dikunci ±380px, lalu WebView2 chat
            // langsung dibuat & dibuka ke muse.ai. InitChatAsync
            // menelan semua galatnya sendiri (panel menampilkan
            // pesan galat; jembatan utama tidak terganggu).
            if (_split.Width > 480) _split.SplitterDistance = 380;
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
        // Tata letak: SplitContainer — Panel1 (kiri, ±380px) berisi
        // panel kendali (kepala, log, tombol PUTUSKAN); Panel2
        // (kanan) berisi panel chat Muse yang MENYATU permanen
        // sejak v2.4.1 (tidak lagi disembunyikan/dibuka via tombol).
        // SplitterDistance diatur di Load (lebar final diketahui
        // setelah jendela terbentuk).
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = true,
            Panel2Collapsed = false,
            BackColor = Bg,
        };
        Controls.Add(_split);

        // Panel kiri dibungkus panel luar ber-padding 12px (v2.3.3):
        // kartu-kartu putih melayang di atas latar #F5F5F7 dengan
        // jarak tepi dan jarak antar kartu yang seragam.
        var leftRoot = _leftRoot = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = new Padding(12),
        };
        _split.Panel1.Controls.Add(leftRoot);

        // Kartu log di tengah (Dock Fill) ditambahkan PERTAMA agar
        // mengisi sisa ruang. Kotak log tinggal langsung di dalam
        // kartu dengan padding kartu sebagai bantalan teksnya.
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Card,
            ForeColor = Ink,
            Font = new Font("Consolas", 9.5f),
            DetectUrls = false,
            // v2.4.2 (permintaan user): batang gulir disembunyikan —
            // log tetap bisa digulir roda mouse dan Log() selalu
            // menggulir ke entri terbaru, jadi tidak ada yang hilang.
            ScrollBars = RichTextBoxScrollBars.None,
        };
        var logCard = _logCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
            Padding = new Padding(12, 10, 12, 10),
        };
        logCard.Controls.Add(_logBox);
        leftRoot.Controls.Add(logCard);

        // (Panel bawah + tombol PUTUSKAN besar DIHAPUS di v2.5.0
        // atas permintaan user: tombolnya pindah ke jendela
        // Pengaturan. Kartu log kini mengisi penuh sampai bawah.)

        // Kartu kepala: judul + status + alamat + dua saklar + label
        // mode. Semua elemen kiri rata di garis x=16 (padding dalam
        // kartu) dengan jarak vertikal antarbaris yang rata; baris
        // saklar memakai lebar kartu penuh (tidak berebut ruang
        // dengan wadah kanan), jadi teks labelnya muat utuh juga
        // saat panel kiri menyempit ke ±380px (chat terbuka).
        var header = _headerCard = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 168,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
            Padding = new Padding(16),
        };
        // Logo rantai-M di kartu kepala (v2.5.7 — identitas ala
        // sidebar Apple System Settings): ikon aplikasi sendiri
        // digambar 40x40 di kiri judul.
        _logoBox = new PictureBox
        {
            Size = new Size(40, 40),
            Location = new Point(16, 14),
            BackColor = Card,
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
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(66, 12),
        };
        header.Controls.Add(title);

        // Pill status (v2.5.7 — ala Vercel/Linear: status adalah
        // titik + kata di dalam pil berlatar warna semantik pudar;
        // warna hanya dipakai untuk status, sisanya netral).
        _statusPill = new RoundedPanel
        {
            Location = new Point(66, 46),
            Size = new Size(300, 27),
            CornerRadius = 13,
            FillColor = Card,
            BorderColor = Hairline,
        };
        header.Controls.Add(_statusPill);

        _statusDot = new RoundDot
        {
            Size = new Size(10, 10),
            Location = new Point(11, 8),
            BackColor = Card,
            DotColor = DotWaiting,
        };
        _statusPill.Controls.Add(_statusDot);

        _statusLabel = new Label
        {
            Text = "Menunggu koneksi",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Size = new Size(262, 18),
            Location = new Point(29, 4),
        };
        _statusPill.Controls.Add(_statusLabel);

        // MaximumSize membuat teks panjang ini membungkus (wrap) saat
        // panel kiri menyempit (chat terbuka) — tingginya sudah
        // dihitung dalam tinggi kartu.
        var addr = _addrLabel = new Label
        {
            Text = "Port " + AgentServer.Port + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = true,
            MaximumSize = new Size(282, 0),
            Location = new Point(66, 82),
        };
        header.Controls.Add(addr);

        // Wadah sisi kanan kepala (perbaikan v2.3.1, dipertahankan):
        // panel ini di-Dock ke tepi kanan kartu sehingga selalu
        // menempel di kanan pada lebar Panel1 berapa pun. Di dalam
        // wadah ter-Dock ini cukup posisi kiri-atas biasa — jangan
        // kembali ke Anchor=Right pada panel lebar (itu bug v2.3.0:
        // jarak tepi kanan terkunci terhadap lebar awal 200px dan
        // kontrolnya terlempar keluar layar).
        var headerRight = new Panel
        {
            Dock = DockStyle.Right,
            Width = 116,
            BackColor = Card,
            Tag = "cardbg",
        };
        header.Controls.Add(headerRight);

        // Label versi: kecil abu-abu, rata kanan, di pojok kanan
        // bawah kartu kepala. Tinggi wadah kanan pasti (250 kartu −
        // 2×16 padding = 218), jadi posisi bawahnya tetap.
        var version = _versionLabel = new Label
        {
            Text = "v2.7.5",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = false,
            Size = new Size(112, 22),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(2, 108),
        };
        headerRight.Controls.Add(version);

        // (Tombol roda gigi PINDAH ke bar judul di v2.5.1 —
        // lihat CaptionGearForm; di sini tinggal label versi.)

        // (Tombol "💬 Chat Muse" DIHAPUS di v2.4.1: chat kini
        // menyatu permanen di sebelah panel — tidak ada lagi yang
        // perlu dibuka lewat tombol.)

        // (Saklar Jalan otomatis & Mode Administrator PINDAH ke
        // jendela Pengaturan di v2.5.0 — lihat SettingsForm.)

        // Penanda level proses INI apa adanya (dari hak sebenarnya,
        // bukan dari pengaturan): hak proses tidak berubah selama
        // berjalan, jadi teksnya diisi sekali di sini. Gayanya
        // mengikuti tema terang: abu sekunder, atau biru aksen saat
        // proses ini administrator.
        bool elevatedNow = Settings.IsElevated();
        _modeLabel = new Label
        {
            Text = elevatedNow ? "Mode: Administrator" : "Mode: Pengguna biasa",
            ForeColor = elevatedNow ? Accent : InkDim,
            BackColor = Card,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(66, 126),
        };
        header.Controls.Add(_modeLabel);

        // Tile status mini (v2.5.7 — ala kartu KPI Linear/Vercel:
        // caption mikro huruf besar redup + nilai semibold).
        // Dibungkus panel polos ber-padding atas untuk jarak 12px
        // dari kartu kepala. Urutan Dock: tiles ditambahkan
        // SEBELUM header agar header menempel paling atas.
        var tilesWrap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 70,
            BackColor = Bg,
            Padding = new Padding(0, 12, 0, 0),
        };
        var tiles = _tilesCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
        };
        tilesWrap.Controls.Add(tiles);
        MakeTile(tiles, 0, "RELAY", "…", out _tileRelayValue);
        MakeTile(tiles, 1, "UPTIME", "0m", out _tileUptimeValue);
        MakeTile(tiles, 2, "VERSI", "2.7.5", out _tileVersionValue);
        leftRoot.Controls.Add(tilesWrap);
        leftRoot.Controls.Add(header);

        BuildChatPane();
    }

    // ---------- Panel chat Muse ----------
    // Panel kanan berisi situs muse.ai yang ditanam lewat WebView2 —
    // isinya website Muse yang sama persis seperti di browser; user
    // login dengan akunnya SENDIRI di panel ini, aplikasi tidak pernah
    // menyentuh kredensial. Sejak v2.4.1 panel ini MENYATU permanen
    // (dibuat saat Load) — jembatan tetap jalan penuh walau WebView2
    // gagal/runtimenya tidak ada (panel hanya menampilkan galat).

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
            Padding = new Padding(0, 12, 12, 12),
        };

        // Badan chat = kartu putih (Dock Fill, ditambahkan PERTAMA
        // agar mengisi sisa ruang) — web view / label galat tinggal
        // di sini dengan bantalan 10px, jadi sudut persegi web view
        // menempel di bidang putih kartu, tidak menabrak lengkung
        // sudut kartu (v2.3.5; sebelumnya bantalan 2px + potongan
        // Region yang bergerigi).
        _chatBody = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
            Padding = new Padding(10),
        };
        root.Controls.Add(_chatBody);

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

        _split.Panel2.Controls.Add(root);
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
            _webView = new WebView2 { Dock = DockStyle.Fill };
            _chatBody.Controls.Add(_webView);
            await _webView.EnsureCoreWebView2Async(env);
            if (IsDisposed) return;

            var core = _webView.CoreWebView2;
            core.Settings.UserAgent = DesktopChromeUa;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            // Jendela baru (mis. popup/redirect login) dibuka di panel
            // yang sama, tidak memunculkan jendela terpisah.
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrEmpty(e.Uri)) core.Navigate(e.Uri);
            };
            // Halaman pertama selesai dimuat → label memuat hilang.
            core.NavigationCompleted += (s, e) =>
            {
                if (!IsDisposed && _chatLoadingLabel != null)
                    _chatLoadingLabel.Visible = false;
            };
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
    // berjalan tiap 5 menit menjalankan PowerShell TERSEMBUNYI yang
    // menyalakan ulang Jembatan bila prosesnya tidak ada — KECUALI
    // bendera UserStopped terpasang (user berhenti sengaja lewat menu
    // tray → Keluar). Penutupan oleh Windows shutdown / Task Manager
    // tidak memasang bendera itu, jadi penjaga tetap menyalakan
    // ulang. Didaftarkan/dihapus bersama tugas login di SyncAutoStart,
    // dengan syarat yang sama (jalan otomatis + admin + proses admin).
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
            string script =
                "' Penjaga MuseLink (v2.5.2): VBScript - tanpa konsol sama sekali.\r\n" +
                "On Error Resume Next\r\n" +
                "Dim shell, stopped, procs\r\n" +
                "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
                "stopped = 0\r\n" +
                "stopped = shell.RegRead(\"HKCU\\Software\\Jembatan\\UserStopped\")\r\n" +
                "If stopped <> 1 Then\r\n" +
                "  Set procs = GetObject(\"winmgmts:\").ExecQuery(\"SELECT Name FROM Win32_Process WHERE Name = 'Jembatan.exe'\")\r\n" +
                "  If procs.Count = 0 Then\r\n" +
                "    shell.Run \"\"\"" + Application.ExecutablePath + "\"\" --auto\", 0, False\r\n" +
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
        _statusLabel.Text = "Menunggu koneksi";
        _statusState = "waiting";
        RefreshStatusPill();
    }

    private void SetStopped(string text)
    {
        _statusDot.DotColor = DotStopped;
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
    // dipudarkan ke warna kartu; teks tetap Ink agar terbaca.
    private void RefreshStatusPill()
    {
        if (_statusPill == null) return;
        Color dot = _statusState == "connected" ? Green
                  : _statusState == "stopped" ? DotStopped : DotWaiting;
        Color tint = Blend(dot, Card, AppTheme.IsDark ? 0.28 : 0.16);
        _statusPill.FillColor = tint;
        _statusPill.BorderColor = Blend(dot, Card, AppTheme.IsDark ? 0.45 : 0.35);
        if (_statusDot != null) _statusDot.BackColor = tint;
        if (_statusLabel != null) { _statusLabel.BackColor = tint; _statusLabel.ForeColor = Ink; }
        _statusPill.Invalidate();
    }

    // Satu kolom tile: caption mikro + nilai; kolom ke-2 dst
    // didahului garis pemisah tipis.
    private void MakeTile(Control parent, int col, string caption, string value, out Label valueLabel)
    {
        int x = 16 + col * 118;
        if (col > 0)
        {
            var div = new Panel
            {
                Size = new Size(1, 34),
                Location = new Point(x - 13, 12),
                BackColor = Hairline,
            };
            parent.Controls.Add(div);
            _tileDividers.Add(div);
        }
        var cap = new Label
        {
            Text = caption,
            Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(x, 10),
        };
        parent.Controls.Add(cap);
        _tileCaptions.Add(cap);
        valueLabel = new Label
        {
            Text = value,
            Font = new Font("Segoe UI Semibold", 10.5f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(x, 27),
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
                _tileRelayValue.Text = AgentServer.TailscaleRelay();
            if (_tileUptimeValue != null)
            {
                var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
                _tileUptimeValue.Text = up.TotalHours >= 1
                    ? (int)up.TotalHours + "j " + up.Minutes + "m"
                    : Math.Max(0, up.Minutes) + "m";
            }
        }
        catch { /* tile hanya keterangan — jangan ganggu aplikasi */ }
    }

    // ---------- Pengaturan, tema & jangan tidur (v2.5.0) ----------

    internal void OpenSettings()
    {
        if (_settingsForm != null && !_settingsForm.IsDisposed)
        {
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(this);
        _settingsForm.FormClosed += (s, e) => _settingsForm = null;
        // Posisi dihitung manual (v2.5.1): CenterParent bawaan
        // menaruhnya di pojok layar pada susunan panel ini.
        _settingsForm.StartPosition = FormStartPosition.Manual;
        var b = Bounds;
        _settingsForm.Location = new Point(
            b.Left + Math.Max(0, (b.Width - _settingsForm.Width) / 2),
            b.Top + Math.Max(0, (b.Height - _settingsForm.Height) / 2));
        _settingsForm.Show(this);
    }

    /// <summary>Tempelkan roda gigi bar judul ke pojok kanan atas jendela ini.</summary>
    private void SyncCaptionGear()
    {
        _captionGear?.SyncTo(Bounds, Visible && WindowState != FormWindowState.Minimized);
    }

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
        if (_leftRoot != null) _leftRoot.BackColor = Bg;
        if (_split != null)
        {
            _split.BackColor = Bg;
            _split.Panel1.BackColor = Bg;
            _split.Panel2.BackColor = Bg;
        }
        if (_headerCard != null)
        {
            _headerCard.FillColor = Card;
            _headerCard.BorderColor = Hairline;
            _headerCard.BackColor = Bg;
        }
        if (_logCard != null)
        {
            _logCard.FillColor = Card;
            _logCard.BorderColor = Hairline;
            _logCard.BackColor = Bg;
        }
        if (_chatBody != null)
        {
            _chatBody.FillColor = Card;
            _chatBody.BorderColor = Hairline;
            _chatBody.BackColor = Bg;
        }
        if (_chatRoot != null) _chatRoot.BackColor = Bg;
        PaintThemedChildren(this);
        if (_titleLabel != null) { _titleLabel.ForeColor = Ink; _titleLabel.BackColor = Card; }
        if (_statusLabel != null) { _statusLabel.ForeColor = Ink; _statusLabel.BackColor = Card; }
        if (_statusDot != null) _statusDot.BackColor = Card;
        if (_addrLabel != null) { _addrLabel.ForeColor = InkDim; _addrLabel.BackColor = Card; }
        if (_versionLabel != null) { _versionLabel.ForeColor = InkDim; _versionLabel.BackColor = Card; }
        if (_modeLabel != null)
        {
            _modeLabel.ForeColor = Settings.IsElevated() ? Accent : InkDim;
            _modeLabel.BackColor = Card;
        }
        if (_tilesCard != null) { _tilesCard.FillColor = Card; _tilesCard.BorderColor = Hairline; }
        foreach (var c in _tileCaptions) { c.ForeColor = InkDim; c.BackColor = Card; }
        foreach (var d in _tileDividers) d.BackColor = Hairline;
        if (_tileRelayValue != null) { _tileRelayValue.ForeColor = Ink; _tileRelayValue.BackColor = Card; }
        if (_tileUptimeValue != null) { _tileUptimeValue.ForeColor = Ink; _tileUptimeValue.BackColor = Card; }
        if (_tileVersionValue != null) { _tileVersionValue.ForeColor = Ink; _tileVersionValue.BackColor = Card; }
        if (_logoBox != null) _logoBox.BackColor = Card;
        RefreshStatusPill();
        if (_logBox != null)
        {
            _logBox.BackColor = Card;
            _logBox.ForeColor = Ink;
            int pos = _logBox.SelectionStart;
            int len = _logBox.SelectionLength;
            _logBox.SelectAll();
            _logBox.SelectionColor = Ink;
            _logBox.Select(pos, len);
        }
        if (_chatLoadingLabel != null) { _chatLoadingLabel.ForeColor = InkDim; _chatLoadingLabel.BackColor = Card; }
        if (_chatErrorLabel != null) { _chatErrorLabel.ForeColor = ErrorInk; _chatErrorLabel.BackColor = ErrorBg; }
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
                p.BackColor = (p.Tag as string) == "cardbg" ? Card : Bg;
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
        _statusDot.DotColor = Green;
        _statusLabel.Text = "Terhubung • aktivitas terakhir " + DateTime.Now.ToString("HH:mm:ss");
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
        string stamp = "[" + DateTime.Now.ToString("HH:mm:ss") + "] ";
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = InkDim;
        _logBox.AppendText(stamp);
        _logBox.SelectionColor = LogColorFor(message);
        _logBox.AppendText(message + Environment.NewLine);
        AppendLogToFile(stamp + message);
        if (_logBox.Lines.Length > 500)
        {
            var lines = _logBox.Lines;
            _logBox.Lines = lines.Skip(lines.Length - 400).ToArray();
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

    // Warna isi baris log: hanya galat yang merah; baris hasil
    // akhir ("selesai…") dan penanda mulai dibuat redup; sisanya
    // warna teks utama. Warna hemat, hanya untuk makna.
    private Color LogColorFor(string m)
    {
        string l = m.ToLowerInvariant();
        if (l.Contains("gagal") || l.Contains("error") || l.Contains("timeout")) return Red;
        if (l.StartsWith("selesai") || l.StartsWith("===")) return InkDim;
        return Ink;
    }

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
