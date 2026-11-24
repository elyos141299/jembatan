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
    // Palet terang gaya iCloud (v2.3.3).
    private static readonly Color Bg = Color.FromArgb(245, 245, 247);
    private static readonly Color Card = Color.FromArgb(255, 255, 255);
    private static readonly Color Ink = Color.FromArgb(29, 29, 31);
    private static readonly Color InkDim = Color.FromArgb(110, 110, 115);
    private static readonly Color Hairline = Color.FromArgb(227, 227, 232);
    private static readonly Color Accent = Color.FromArgb(0, 113, 227);
    private static readonly Color Green = Color.FromArgb(52, 199, 89);
    private static readonly Color Red = Color.FromArgb(255, 59, 48);
    private static readonly Color LightFill = Color.FromArgb(232, 232, 237);
    private static readonly Color DotWaiting = Color.FromArgb(255, 159, 10);
    private static readonly Color DotStopped = Color.FromArgb(174, 174, 178);
    private static readonly Color ErrorBg = Color.FromArgb(255, 244, 204);
    private static readonly Color ErrorInk = Color.FromArgb(138, 109, 0);

    private readonly AgentServer _server;
    private RoundDot _statusDot;
    private Label _statusLabel;
    private RichTextBox _logBox;
    private RoundedButton _toggleButton;
    private ToggleSwitch _autoStartCheck;
    private ToggleSwitch _adminCheck;
    private Label _modeLabel;

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

        Load += (s, e) =>
        {
            RotateLogFileIfNeeded();
            Log("=== MuseLink v2.4.5 dimulai ===");
            InitStartOptions();
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
        var exitItem = new ToolStripMenuItem("Keluar");
        exitItem.Click += (s, e) => ExitFromTray();
        menu.Items.Add(openItem);
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
        var leftRoot = new Panel
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
        var logCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
            Padding = new Padding(12, 10, 12, 10),
        };
        logCard.Controls.Add(_logBox);
        leftRoot.Controls.Add(logCard);

        // Baris tombol PUTUSKAN / Sambung lagi di bawah kartu log:
        // panel 56px (jarak 12px di atas tombol 44px).
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Bg,
            Padding = new Padding(0, 12, 0, 0),
        };
        _toggleButton = new RoundedButton
        {
            Dock = DockStyle.Fill,
            Text = "PUTUSKAN",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            BackColor = Red,
            ForeColor = Color.White,
            CornerRadius = 12,
        };
        _toggleButton.Click += ToggleClicked;
        bottom.Controls.Add(_toggleButton);
        leftRoot.Controls.Add(bottom);

        // Kartu kepala: judul + status + alamat + dua saklar + label
        // mode. Semua elemen kiri rata di garis x=16 (padding dalam
        // kartu) dengan jarak vertikal antarbaris yang rata; baris
        // saklar memakai lebar kartu penuh (tidak berebut ruang
        // dengan wadah kanan), jadi teks labelnya muat utuh juga
        // saat panel kiri menyempit ke ±380px (chat terbuka).
        var header = new RoundedPanel
        {
            Dock = DockStyle.Top,
            Height = 250,
            FillColor = Card,
            BorderColor = Hairline,
            CornerRadius = 16,
            Padding = new Padding(16),
        };
        var title = new Label
        {
            Text = "MuseLink",
            Font = new Font("Segoe UI Semibold", 15f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(16, 14),
        };
        header.Controls.Add(title);

        _statusDot = new RoundDot
        {
            Size = new Size(10, 10),
            Location = new Point(16, 55),
            BackColor = Card,
            DotColor = DotWaiting,
        };
        header.Controls.Add(_statusDot);

        _statusLabel = new Label
        {
            Text = "Menunggu koneksi",
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(34, 49),
        };
        header.Controls.Add(_statusLabel);

        // MaximumSize membuat teks panjang ini membungkus (wrap) saat
        // panel kiri menyempit (chat terbuka) — tingginya sudah
        // dihitung dalam tinggi kartu.
        var addr = new Label
        {
            Text = "Port " + AgentServer.Port + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.",
            Font = new Font("Segoe UI", 9f),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = true,
            MaximumSize = new Size(330, 0),
            Location = new Point(16, 78),
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
        };
        header.Controls.Add(headerRight);

        // Label versi: kecil abu-abu, rata kanan, di pojok kanan
        // bawah kartu kepala. Tinggi wadah kanan pasti (250 kartu −
        // 2×16 padding = 218), jadi posisi bawahnya tetap.
        var version = new Label
        {
            Text = "v2.4.5",
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = InkDim,
            BackColor = Card,
            AutoSize = false,
            Size = new Size(112, 22),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(2, 196),
        };
        headerRight.Controls.Add(version);

        // (Tombol "💬 Chat Muse" DIHAPUS di v2.4.1: chat kini
        // menyatu permanen di sebelah panel — tidak ada lagi yang
        // perlu dibuka lewat tombol.)

        // Jalan otomatis saat Windows menyala (v2.1) — sejak v2.3.3
        // berupa saklar ala iOS + label di kanannya. Status awalnya
        // diisi InitStartOptions() saat jendela dimuat; handler
        // perubahan dipasang di sana juga agar pengisian awal tidak
        // menulis registry. Mengklik labelnya ikut membalik saklar
        // (perilaku teks checkbox lama).
        _autoStartCheck = new ToggleSwitch
        {
            BackColor = Card,
            Location = new Point(16, 146),
        };
        header.Controls.Add(_autoStartCheck);
        var autoStartLabel = new Label
        {
            Text = "Jalan otomatis",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(70, 149),
        };
        autoStartLabel.Click += (s, e) => _autoStartCheck.Checked = !_autoStartCheck.Checked;
        header.Controls.Add(autoStartLabel);

        // Mode Administrator (v2.3) — saklar kedua, tepat di bawah
        // jalan otomatis. Status awalnya juga diisi InitStartOptions().
        _adminCheck = new ToggleSwitch
        {
            BackColor = Card,
            Location = new Point(16, 186),
        };
        header.Controls.Add(_adminCheck);
        var adminLabel = new Label
        {
            Text = "Mode Administrator",
            Font = new Font("Segoe UI", 10f),
            ForeColor = Ink,
            BackColor = Card,
            AutoSize = true,
            Location = new Point(70, 189),
        };
        adminLabel.Click += (s, e) => _adminCheck.Checked = !_adminCheck.Checked;
        header.Controls.Add(adminLabel);

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
            Location = new Point(16, 222),
        };
        header.Controls.Add(_modeLabel);
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
        var root = new Panel
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

    private void ToggleClicked(object sender, EventArgs e)
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            SetStopped("Terputus — Muse tidak bisa mengakses PC ini");
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
        RefreshToggleButton();
    }

    private void RefreshToggleButton()
    {
        if (_server.IsRunning)
        {
            _toggleButton.Text = "PUTUSKAN";
            _toggleButton.BackColor = Red;
        }
        else
        {
            _toggleButton.Text = "Sambung lagi";
            _toggleButton.BackColor = Accent;
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
        return "\"" + Application.ExecutablePath + "\"";
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
                      Application.ExecutablePath + "\\\"\" /SC ONLOGON /RL HIGHEST /F";
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
        // v2.3.4: naskah penjaga ditulis ke berkas .ps1 dan tugas
        // menjalankannya dengan -File. Cara lama (-Command sebaris)
        // GAGAL di v2.3.3: nilai /TR schtasks mentok 261 karakter,
        // perintah sebarisnya lebih panjang dari itu.
        string scriptPath;
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Jembatan");
            Directory.CreateDirectory(dir);
            scriptPath = Path.Combine(dir, "watchdog.ps1");
            string script =
                "if ((Get-ItemProperty 'HKCU:\\Software\\Jembatan' -Name UserStopped -ErrorAction SilentlyContinue).UserStopped -ne 1) {\r\n" +
                "  if (-not (Get-Process Jembatan -ErrorAction SilentlyContinue)) { Start-Process '" +
                Application.ExecutablePath + "' }\r\n}\r\n";
            File.WriteAllText(scriptPath, script);
        }
        catch (Exception ex)
        {
            Log("GAGAL menulis naskah penjaga: " + ex.Message);
            return;
        }
        string tr = "powershell -NoProfile -WindowStyle Hidden -File \\\"" + scriptPath + "\\\"";
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
        bool autoStart = _autoStartCheck.Checked;
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

        _autoStartCheck.Checked = autoStart;
        _adminCheck.Checked = Settings.AdminMode;
        // Handler dipasang SETELAH status awal terisi.
        _autoStartCheck.CheckedChanged += AutoStartChanged;
        _adminCheck.CheckedChanged += AdminModeChanged;

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

    private void AutoStartChanged(object sender, EventArgs e)
    {
        Log(_autoStartCheck.Checked
            ? "Jalan otomatis saat Windows menyala: AKTIF"
            : "Jalan otomatis saat Windows menyala: MATI");
        SyncAutoStart();
    }

    private void AdminModeChanged(object sender, EventArgs e)
    {
        bool on = _adminCheck.Checked;
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
    }

    private void SetStopped(string text)
    {
        _statusDot.DotColor = DotStopped;
        _statusLabel.Text = text;
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
        _statusLabel.Text = "Muse terhubung • aktivitas terakhir " + DateTime.Now.ToString("HH:mm:ss");
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
        if (message != null && message.Length > 140)
            message = message.Substring(0, 140) + "…";
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
        _logBox.AppendText(line + Environment.NewLine);
        AppendLogToFile(line);
        if (_logBox.Lines.Length > 500)
        {
            var lines = _logBox.Lines;
            _logBox.Lines = lines.Skip(lines.Length - 400).ToArray();
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
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
