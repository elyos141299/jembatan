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
/// Jendela utama Jembatan. Selama jendela ini terbuka dan status
/// tersambung, layanan agen (AgentServer) mendengarkan di port 8765
/// dan Muse bisa bekerja di PC ini lewat jaringan Tailscale user.
/// Menutup jendela atau menekan PUTUSKAN menghentikan akses seketika.
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

    private readonly AgentServer _server;
    private Panel _statusDot;
    private Label _statusLabel;
    private RichTextBox _logBox;
    private Button _toggleButton;
    private CheckBox _autoStartCheck;
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

    public MainForm()
    {
        Text = "Jembatan — Akses Muse";
        Width = 660;
        Height = 540;
        MinimumSize = new Size(520, 420);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 10f);

        BuildLayout();

        _server = new AgentServer(this, Log, OnActivity);

        Load += (s, e) =>
        {
            InitAutoStart();
            try
            {
                _server.Start();
                SetWaiting();
                Log("Jembatan berjalan di port " + AgentServer.Port + ".");
                Log("Menunggu Muse terhubung lewat Tailscale…");
            }
            catch (Exception ex)
            {
                SetStopped("Gagal memulai: " + ex.Message);
                Log("GAGAL memulai layanan: " + ex.Message);
            }
        };
        FormClosed += (s, e) => _server.Stop();
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

        // Log di tengah (Dock Fill) ditambahkan PERTAMA agar mengisi sisa ruang.
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
        _split.Panel1.Controls.Add(_logBox);

        // Kepala jendela: judul + status + alamat.
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 144,
            BackColor = Bg,
            Padding = new Padding(14, 10, 14, 6),
        };
        var title = new Label
        {
            Text = "Jembatan — Akses Muse",
            Font = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(14, 10),
        };
        header.Controls.Add(title);

        _statusDot = new Panel
        {
            Size = new Size(12, 12),
            Location = new Point(16, 55),
            BackColor = Amber,
        };
        header.Controls.Add(_statusDot);

        _statusLabel = new Label
        {
            Text = "Menunggu koneksi",
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Ink,
            AutoSize = true,
            Location = new Point(36, 50),
        };
        header.Controls.Add(_statusLabel);

        // MaximumSize membuat teks panjang ini membungkus (wrap) dua
        // baris saat panel kiri menyempit ke ±380px (chat terbuka).
        var addr = new Label
        {
            Text = "Port " + AgentServer.Port + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.",
            ForeColor = InkDim,
            AutoSize = true,
            MaximumSize = new Size(352, 0),
            Location = new Point(14, 80),
        };
        header.Controls.Add(addr);

        var version = new Label
        {
            Text = "v2.2",
            ForeColor = InkDim,
            AutoSize = true,
            Location = new Point(612, 116),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        header.Controls.Add(version);

        // Tombol buka/tutup panel chat Muse (v2.2) — pojok kanan atas.
        _chatButton = new Button
        {
            Text = "💬 Chat Muse",
            Font = new Font("Segoe UI", 9.5f),
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Ink,
            Size = new Size(108, 30),
            Location = new Point(528, 9),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _chatButton.FlatAppearance.BorderSize = 0;
        _chatButton.Click += ChatToggleClicked;
        header.Controls.Add(_chatButton);

        // Jalan otomatis saat Windows menyala (v2.1) — status awalnya
        // diisi InitAutoStart() saat jendela dimuat; handler perubahan
        // dipasang di sana juga agar pengisian awal tidak menulis registry.
        _autoStartCheck = new CheckBox
        {
            Text = "Jalan otomatis saat Windows menyala",
            ForeColor = Ink,
            BackColor = Bg,
            AutoSize = true,
            Location = new Point(14, 116),
        };
        header.Controls.Add(_autoStartCheck);
        _split.Panel1.Controls.Add(header);

        // Tombol besar PUTUSKAN / Sambung lagi di bawah.
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 72,
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
        reload.Location = new Point(8, 7);
        reload.Width = 110;
        reload.Click += ChatReloadClicked;
        toolbar.Controls.Add(reload);
        var chrome = SmallButton("🌐 Buka di Chrome");
        chrome.Location = new Point(126, 7);
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
            Location = new Point(10, 8),
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

    // ---------- Jalan otomatis saat Windows menyala (v2.1) ----------
    // Ditopang nilai "Jembatan" di HKCU\...\CurrentVersion\Run —
    // per-user, tanpa admin, selaras dengan installer per-user.

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Jembatan";

    private static string ExpectedRunCommand()
    {
        // Path exe dikutip agar aman dari spasi di nama folder.
        return "\"" + Application.ExecutablePath + "\"";
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                var v = key?.GetValue(RunValueName) as string;
                return v != null &&
                    string.Equals(v.Trim(), ExpectedRunCommand(), StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { return false; }
    }

    private static bool HasAutoStartValue()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                return key?.GetValue(RunValueName) != null;
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
        }
    }

    private void InitAutoStart()
    {
        // Jalan pertama (nilai Run belum ada sama sekali): aktifkan
        // otomatis — memang itu yang diminta user.
        if (!HasAutoStartValue())
        {
            try
            {
                SetAutoStart(true);
                Log("Jalan otomatis saat Windows menyala: AKTIF (diaktifkan di jalan pertama).");
            }
            catch (Exception ex)
            {
                Log("GAGAL mengaktifkan jalan otomatis: " + ex.Message);
            }
        }
        _autoStartCheck.Checked = IsAutoStartEnabled();
        // Handler dipasang SETELAH status awal terisi.
        _autoStartCheck.CheckedChanged += AutoStartChanged;
    }

    private void AutoStartChanged(object sender, EventArgs e)
    {
        bool on = _autoStartCheck.Checked;
        try
        {
            SetAutoStart(on);
            Log(on
                ? "Jalan otomatis saat Windows menyala: AKTIF"
                : "Jalan otomatis saat Windows menyala: MATI");
        }
        catch (Exception ex)
        {
            Log("GAGAL mengubah jalan otomatis: " + ex.Message);
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
        _logBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        if (_logBox.Lines.Length > 500)
        {
            var lines = _logBox.Lines;
            _logBox.Lines = lines.Skip(lines.Length - 400).ToArray();
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }
}
