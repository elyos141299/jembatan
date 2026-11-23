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
/// Jendela utama Muse Windows (nama internal tetap "Jembatan").
/// Selama jendela ini terbuka dan status
/// tersambung, layanan agen (AgentServer) mendengarkan di port 8765
/// dan Muse bisa bekerja di PC ini lewat jaringan Tailscale user.
/// Menekan PUTUSKAN menghentikan akses seketika. Tombol X hanya
/// menyembunyikan jendela ke system tray (layanan tetap berjalan);
/// keluar total lewat menu tray → Keluar.
/// Semua aktivitas Muse dicatat di log jendela ini, real-time.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(13, 17, 23);
    private static readonly Color Panel = Color.FromArgb(22, 27, 34);
    private static readonly Color Ink = Color.FromArgb(230, 237, 243);
    private static readonly Color InkDim = Color.FromArgb(139, 148, 158);
    private static readonly Color Green = Color.FromArgb(46, 160, 67);
    private static readonly Color Amber = Color.FromArgb(210, 153, 34);
    private static readonly Color Red = Color.FromArgb(185, 55, 45);
    private static readonly Color Gray = Color.FromArgb(110, 118, 129);
    private static readonly Color Divider = Color.FromArgb(48, 54, 61);

    private readonly AgentServer _server;
    private Panel _statusDot;
    private Label _statusLabel;
    private RichTextBox _logBox;
    private Button _toggleButton;
    private CheckBox _autoStartCheck;
    private CheckBox _adminCheck;
    private Label _modeLabel;
    private bool _claudeSeen;

    // Panel chat Muse (v2.2): SplitContainer membagi jendela —
    // Panel1 (kiri) = tampilan lama, Panel2 (kanan) = chat WebView2.
    private SplitContainer _split;
    private Button _chatButton;
    private Panel _chatBody;
    private Label _chatErrorLabel;
    private WebView2 _webView;
    private bool _chatOpen;
    private bool _chatInitializing;
    private Size _compactSize;
    private Size _compactMinimumSize;

    // System tray: tombol X hanya menyembunyikan jendela ke tray —
    // layanan tetap berjalan. Keluar sungguhan hanya lewat menu tray.
    private NotifyIcon _trayIcon;
    private bool _exitRequested;
    private bool _trayTipShown;

    public MainForm()
    {
        Text = "Muse Windows";
        Width = 660;
        Height = 540;
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
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
            Log("=== Muse Windows v2.3.2 dimulai ===");
            InitStartOptions();
            try
            {
                _server.Start();
                SetWaiting();
                // Jalan sehat (bukan berhenti-sengaja): bersihkan
                // bendera UserStopped agar penjaga (watchdog) kembali
                // melindungi proses ini.
                try { Settings.UserStopped = false; } catch { /* abaikan */ }
                Log("Muse Windows berjalan di port " + AgentServer.Port + ".");
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

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Buka Muse Windows");
        openItem.Click += (s, e) => ShowFromTray();
        var exitItem = new ToolStripMenuItem("Keluar");
        exitItem.Click += (s, e) => ExitFromTray();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "Muse Windows",
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
    // Jembatan: jendela disembunyikan ke tray dan layanan agen terus
    // berjalan. Penutupan bukan dari user (Windows shutdown, Task
    // Manager) serta jalan Keluar dari tray dibiarkan lewat.
    private void OnMainFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_exitRequested) return;
        if (e.CloseReason != CloseReason.UserClosing) return;

        e.Cancel = true;
        Hide();
        Log("Jendela disembunyikan ke system tray — Muse Windows tetap berjalan.");
        if (!_trayTipShown)
        {
            _trayTipShown = true;
            _trayIcon?.ShowBalloonTip(5000,
                "Muse Windows tetap berjalan",
                "Muse Windows tetap aktif di system tray. Klik ganda ikon tray untuk membuka lagi, atau klik kanan lalu pilih Keluar untuk berhenti total.",
                ToolTipIcon.Info);
        }
    }

    private void BuildLayout()
    {
        // Tata letak v2.2: SplitContainer — Panel1 (kiri, ±380px) berisi
        // semua yang sudah ada (kepala, log, tombol PUTUSKAN); Panel2
        // (kanan) berisi panel chat Muse dan AWALNYA disembunyikan
        // (Panel2Collapsed) sehingga jendela tampil ringkas seperti dulu.
        // SplitterDistance sengaja TIDAK diatur di sini (lebar kontrol
        // belum final saat konstruksi) — diatur saat chat pertama dibuka.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = true,
            Panel2Collapsed = true,
            BackColor = Bg,
        };
        Controls.Add(_split);

        // Log di tengah (Dock Fill) ditambahkan PERTAMA agar mengisi
        // sisa ruang. Kotak log dibungkus panel ber-padding kecil agar
        // teks punya bantalan dalam yang rapi (RichTextBox sendiri
        // tidak punya padding teks yang bisa diatur).
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Panel,
            ForeColor = Ink,
            Font = new Font("Consolas", 9.5f),
            DetectUrls = false,
        };
        var logWrap = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Panel,
            Padding = new Padding(10, 8, 10, 8),
        };
        logWrap.Controls.Add(_logBox);
        _split.Panel1.Controls.Add(logWrap);

        // Garis pemisah 1px di bawah kepala. Ditambahkan SEBELUM panel
        // kepala: urutan Dock WinForms menaruh kontrol yang ditambahkan
        // lebih dulu lebih jauh dari tepi, jadi garis ini mendarat
        // tepat di antara kepala dan area log.
        var dividerTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = Divider,
        };
        _split.Panel1.Controls.Add(dividerTop);

        // Kepala jendela: judul + status + alamat. Semua elemen kiri
        // rata di garis x=12 (padding luar seragam 12px) dengan jarak
        // vertikal antarbaris yang rata.
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 198,
            BackColor = Bg,
            Padding = new Padding(12),
        };
        var title = new Label
        {
            Text = "Muse Windows",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(12, 12),
        };
        header.Controls.Add(title);

        _statusDot = new Panel
        {
            Size = new Size(12, 12),
            Location = new Point(12, 53),
            BackColor = Amber,
        };
        header.Controls.Add(_statusDot);

        _statusLabel = new Label
        {
            Text = "Menunggu koneksi",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(30, 49),
        };
        header.Controls.Add(_statusLabel);

        // MaximumSize membuat teks panjang ini membungkus (wrap) dua
        // baris saat panel kiri menyempit ke ±380px (chat terbuka).
        var addr = new Label
        {
            Text = "Port " + AgentServer.Port + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.",
            ForeColor = InkDim,
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Location = new Point(12, 79),
        };
        header.Controls.Add(addr);

        // Wadah sisi kanan kepala (perbaikan v2.3.1): panel ini di-Dock
        // ke tepi kanan kepala sehingga selalu menempel di kanan pada
        // lebar Panel1 berapa pun (660 tertutup, ±380 chat terbuka).
        // Dulu tombol chat & label versi memakai Anchor=Right dengan
        // posisi absolut yang dihitung terhadap lebar awal panel kepala
        // (200px bawaan konstruksi) — WinForms mengunci jarak tepi kanan
        // terhadap lebar awal itu, jadi saat kepala melebar ke ukuran
        // sebenarnya kedua kontrol terdorong ratusan piksel keluar tepi
        // kanan dan tidak pernah terlihat. Di dalam wadah ter-Dock ini
        // cukup posisi kiri-atas biasa.
        var headerRight = new Panel
        {
            Dock = DockStyle.Right,
            Width = 116,
            BackColor = Bg,
        };
        header.Controls.Add(headerRight);

        // Label versi: lebar tetap + rata kanan, menempel rapi di
        // pojok kanan bawah kepala (sejajar baris label mode di kiri).
        var version = new Label
        {
            Text = "v2.3.2",
            ForeColor = InkDim,
            AutoSize = false,
            Size = new Size(112, 22),
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(0, 168),
        };
        headerRight.Controls.Add(version);

        // Tombol buka/tutup panel chat Muse (v2.2) — pojok kanan
        // atas, sejajar garis atas judul di kiri.
        _chatButton = new Button
        {
            Text = "💬 Chat Muse",
            Font = new Font("Segoe UI", 9.5f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Ink,
            Size = new Size(108, 30),
            Location = new Point(4, 11),
        };
        _chatButton.FlatAppearance.BorderSize = 0;
        _chatButton.Click += ChatToggleClicked;
        headerRight.Controls.Add(_chatButton);

        // Jalan otomatis saat Windows menyala (v2.1) — status awalnya
        // diisi InitStartOptions() saat jendela dimuat; handler perubahan
        // dipasang di sana juga agar pengisian awal tidak menulis registry.
        // Lebar TETAP 252px (bukan AutoSize): saat panel chat terbuka
        // panel kiri menyempit ke ±380px dan sisa ruang di kiri wadah
        // kanan kepala adalah 252px — teks Indonesia-nya muat utuh di
        // kedua tata letak, tidak terpotong seperti sebelumnya.
        _autoStartCheck = new CheckBox
        {
            Text = "Jalan otomatis saat Windows menyala",
            ForeColor = Ink,
            BackColor = Bg,
            AutoSize = false,
            Size = new Size(252, 22),
            Location = new Point(12, 121),
        };
        header.Controls.Add(_autoStartCheck);

        // Mode Administrator (v2.3) — checkbox kedua, tepat di bawah
        // jalan otomatis. Status awalnya juga diisi InitStartOptions().
        _adminCheck = new CheckBox
        {
            Text = "Mode Administrator (akses penuh)",
            ForeColor = Ink,
            BackColor = Bg,
            AutoSize = false,
            Size = new Size(252, 22),
            Location = new Point(12, 147),
        };
        header.Controls.Add(_adminCheck);

        // Penanda level proses INI apa adanya (dari hak sebenarnya,
        // bukan dari pengaturan): hak proses tidak berubah selama
        // berjalan, jadi teksnya diisi sekali di sini.
        bool elevatedNow = Settings.IsElevated();
        _modeLabel = new Label
        {
            Text = elevatedNow ? "Mode: Administrator" : "Mode: Pengguna biasa",
            ForeColor = elevatedNow ? Amber : InkDim,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 171),
        };
        header.Controls.Add(_modeLabel);
        _split.Panel1.Controls.Add(header);

        // Garis pemisah 1px di atas baris PUTUSKAN. Ditambahkan
        // SEBELUM panel bawah agar urutan Dock menaruhnya tepat di
        // antara area log dan baris tombol.
        var dividerBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 1,
            BackColor = Divider,
        };
        _split.Panel1.Controls.Add(dividerBottom);

        // Tombol besar PUTUSKAN / Sambung lagi di bawah — panel 68px
        // dengan padding 12px membuat tombolnya pas 44px.
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            BackColor = Bg,
            Padding = new Padding(12),
        };
        _toggleButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "PUTUSKAN",
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            FlatStyle = FlatStyle.Flat,
            BackColor = Red,
            ForeColor = Color.White,
        };
        _toggleButton.FlatAppearance.BorderSize = 0;
        _toggleButton.Click += ToggleClicked;
        bottom.Controls.Add(_toggleButton);
        _split.Panel1.Controls.Add(bottom);

        BuildChatPane();
    }

    // ---------- Panel chat Muse (v2.2) ----------
    // Panel kanan berisi situs muse.ai yang ditanam lewat WebView2 —
    // isinya website Muse yang sama persis seperti di browser; user
    // login dengan akunnya SENDIRI di panel ini, aplikasi tidak pernah
    // menyentuh kredensial. WebView2 dibuat MALAS (saat chat pertama
    // dibuka) supaya start aplikasi tetap cepat dan jembatan tetap
    // jalan penuh walau WebView2 gagal/runtimenya tidak ada.

    // UA Chrome desktop terkini — tanpa ini sebagian penyedia login
    // menolak browser tertanam.
    private const string DesktopChromeUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private Button SmallButton(string text)
    {
        var b = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 9.5f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Ink,
            Height = 30,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private void BuildChatPane()
    {
        var root = new Panel { Dock = DockStyle.Fill, BackColor = Bg };

        // Badan chat (Dock Fill) ditambahkan PERTAMA agar mengisi sisa
        // ruang — web view / label galat tinggal di sini.
        _chatBody = new Panel { Dock = DockStyle.Fill, BackColor = Panel };
        root.Controls.Add(_chatBody);

        _chatErrorLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Amber,
            Font = new Font("Segoe UI", 10.5f),
            Visible = false,
            Padding = new Padding(24),
        };
        _chatBody.Controls.Add(_chatErrorLabel);

        // Bilah alat kecil di atas web view.
        var toolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 44,
            BackColor = Bg,
        };
        var reload = SmallButton("↻ Muat Ulang");
        reload.Location = new Point(12, 7);
        reload.Width = 110;
        reload.Click += ChatReloadClicked;
        toolbar.Controls.Add(reload);
        var chrome = SmallButton("🌐 Buka di Chrome");
        chrome.Location = new Point(130, 7);
        chrome.Width = 150;
        chrome.Click += ChatOpenChromeClicked;
        toolbar.Controls.Add(chrome);
        root.Controls.Add(toolbar);

        // Kepala panel chat.
        var head = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = Bg,
        };
        var headLabel = new Label
        {
            Text = "Chat Muse — login dengan akunmu sendiri di panel ini",
            ForeColor = InkDim,
            AutoSize = true,
            Location = new Point(12, 9),
        };
        head.Controls.Add(headLabel);
        root.Controls.Add(head);

        _split.Panel2.Controls.Add(root);
    }

    private async void ChatToggleClicked(object sender, EventArgs e)
    {
        try
        {
            if (_chatOpen) CloseChatPane();
            else await OpenChatPaneAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Log("Panel chat gagal dibuka: " + ex.Message);
        }
    }

    private async Task OpenChatPaneAsync()
    {
        _chatOpen = true;

        // Ingat ukuran ringkas, lalu lebarkan jendela untuk panel chat.
        _compactSize = Size;
        _compactMinimumSize = MinimumSize;
        MinimumSize = new Size(980, 640);
        Size = new Size(Math.Max(Width, 1280), Math.Max(Height, 800));
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(
            area.X + Math.Max(0, (area.Width - Width) / 2),
            area.Y + Math.Max(0, (area.Height - Height) / 2));

        if (_split.Width > 480) _split.SplitterDistance = 380;
        _split.Panel2Collapsed = false;

        if (_webView == null && !_chatInitializing)
            await InitChatAsync();
    }

    private void CloseChatPane()
    {
        _chatOpen = false;
        _split.Panel2Collapsed = true;
        // WebView TIDAK dibuang — cukup disembunyikan bersama panelnya,
        // supaya membuka lagi cepat dan sesi login tetap awet.
        MinimumSize = _compactMinimumSize;
        if (_compactSize.Width > 0) Size = _compactSize;
    }

    private async Task InitChatAsync()
    {
        _chatInitializing = true;
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
            _chatInitializing = false;
        }
    }

    private void ShowChatError(string message)
    {
        _chatErrorLabel.Text =
            "Panel chat gagal dibuka.\n\n" + message +
            "\n\nChat tetap bisa dipakai lewat tombol \"🌐 Buka di Chrome\" di atas.";
        _chatErrorLabel.Visible = true;
    }

    private async void ChatReloadClicked(object sender, EventArgs e)
    {
        try
        {
            if (_webView != null && _webView.CoreWebView2 != null)
                _webView.CoreWebView2.Reload();
            else if (!_chatInitializing)
                await InitChatAsync();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) Log("Gagal memuat ulang panel chat: " + ex.Message);
        }
    }

    private void ChatOpenChromeClicked(object sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://muse.ai") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("Gagal membuka muse.ai di browser: " + ex.Message);
        }
    }

    private void ToggleClicked(object sender, EventArgs e)
    {
        if (_server.IsRunning)
        {
            _server.Stop();
            _claudeSeen = false;
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
            _toggleButton.BackColor = Green;
        }
    }

    // ---------- Jalan otomatis & Mode Administrator (v2.1 + v2.3) ----------
    // Dua mekanisme jalan otomatis, dipilih oleh SyncAutoStart():
    //  - Nilai "Muse Windows" di HKCU\...\CurrentVersion\Run — jalan
    //    sebagai pengguna biasa (perilaku v2.1, tanpa admin).
    //  - Tugas Terjadwal "Muse Windows" berhak tertinggi (schtasks) —
    //    jalan sebagai ADMINISTRATOR saat login, tanpa prompt UAC.
    //    Hanya bisa dibuat/dihapus oleh proses yang sedang admin.
    // Mode Administrator sendiri disimpan di HKCU\Software\Jembatan
    // (kelas Settings); kenaikan hak prosesnya diatur di Program.cs.
    //
    // Sejak v2.3.2 nama tampil aplikasi adalah "Muse Windows" (nama
    // internal tetap "Jembatan": berkas exe, kunci registry, mutex).
    // Tugas & nilai Run lama bernama "Jembatan" dibersihkan oleh
    // SyncAutoStart() sebagai migrasi nama. Di mode terjadwal juga
    // ada tugas penjaga "Muse Windows Watchdog" (EnsureWatchdogTask).

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Muse Windows";
    private const string LegacyRunValueName = "Jembatan";
    private const string TaskName = "Muse Windows";
    private const string LegacyTaskName = "Jembatan";
    private const string WatchdogTaskName = "Muse Windows Watchdog";

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
                // Nilai lama "Jembatan" juga dihitung ada (migrasi nama).
                return key?.GetValue(RunValueName) != null
                    || key?.GetValue(LegacyRunValueName) != null;
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
            // Nilai lama bernama "Jembatan" selalu dibersihkan.
            key.DeleteValue(LegacyRunValueName, false);
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

    // Penjaga anti-crash (v2.3.2): tugas "Muse Windows Watchdog"
    // berjalan tiap 5 menit menjalankan PowerShell TERSEMBUNYI yang
    // menyalakan ulang Jembatan bila prosesnya tidak ada — KECUALI
    // bendera UserStopped terpasang (user berhenti sengaja lewat menu
    // tray → Keluar). Penutupan oleh Windows shutdown / Task Manager
    // tidak memasang bendera itu, jadi penjaga tetap menyalakan
    // ulang. Didaftarkan/dihapus bersama tugas login di SyncAutoStart,
    // dengan syarat yang sama (jalan otomatis + admin + proses admin).
    private void EnsureWatchdogTask()
    {
        string psCommand =
            "if ((Get-ItemProperty 'HKCU:\\Software\\Jembatan' -Name UserStopped -ErrorAction SilentlyContinue).UserStopped -ne 1) " +
            "{ if (-not (Get-Process Jembatan -ErrorAction SilentlyContinue)) { Start-Process '" +
            Application.ExecutablePath + "' } }";
        // Kutip dalam -Command "..." ditulis \" agar utuh melewati
        // penguraian baris perintah schtasks (pola yang sama seperti
        // /TR tugas login di atas).
        string tr = "powershell -NoProfile -WindowStyle Hidden -Command \\\"" + psCommand + "\\\"";
        string args = "/Create /TN \"" + WatchdogTaskName + "\" /TR \"" + tr +
                      "\" /SC MINUTE /MO 5 /RL HIGHEST /F";
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
    /// checkbox jalan otomatis + pengaturan Mode Administrator + hak
    /// proses ini. Dipanggil sekali saat jendela dimuat dan tiap kali
    /// SALAH SATU checkbox berubah. Semua kegagalan hanya dicatat di
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
                // Run dicabut agar tidak dobel. Tugas lama bernama
                // "Jembatan" ikut dibersihkan (migrasi nama v2.3.2).
                EnsureScheduledTask();
                EnsureWatchdogTask();
                if (ScheduledTaskExists(LegacyTaskName))
                    DeleteScheduledTask(LegacyTaskName);
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

    // Hapus tugas-tugas mode admin (login "Muse Windows", penjaga,
    // dan nama lama "Jembatan") bila masih terdaftar. Menghapus tugas
    // butuh proses admin; bila proses ini belum admin, cukup catat di
    // log — perilaku yang sama seperti v2.3 untuk tugas login.
    private void DeleteAdminTasks(bool elevated)
    {
        foreach (var name in new[] { TaskName, WatchdogTaskName, LegacyTaskName })
        {
            if (!ScheduledTaskExists(name)) continue;
            if (elevated) DeleteScheduledTask(name);
            else Log("Tugas terjadwal '" + name + "' masih terdaftar — penghapusannya butuh Muse Windows berjalan sebagai administrator.");
        }
    }

    private void InitStartOptions()
    {
        bool taskExists = ScheduledTaskExists(TaskName) || ScheduledTaskExists(LegacyTaskName);
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
            Log("Mode Administrator dimatikan — berlaku penuh setelah Muse Windows dibuka ulang");
        }
    }

    private void SetWaiting()
    {
        _statusDot.BackColor = Amber;
        _statusLabel.Text = "Menunggu koneksi";
    }

    private void SetStopped(string text)
    {
        _statusDot.BackColor = Gray;
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
        _claudeSeen = true;
        _statusDot.BackColor = Green;
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
