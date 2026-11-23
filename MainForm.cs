using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
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
        Controls.Add(_logBox);

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

        var addr = new Label
        {
            Text = "Port " + AgentServer.Port + " • Hanya bisa dijangkau lewat jaringan Tailscale kamu — tidak terbuka ke internet.",
            ForeColor = InkDim,
            AutoSize = true,
            Location = new Point(14, 84),
        };
        header.Controls.Add(addr);

        var version = new Label
        {
            Text = "v2.1",
            ForeColor = InkDim,
            AutoSize = true,
            Location = new Point(612, 84),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        header.Controls.Add(version);

        // Jalan otomatis saat Windows menyala (v2.1) — status awalnya
        // diisi InitAutoStart() saat jendela dimuat; handler perubahan
        // dipasang di sana juga agar pengisian awal tidak menulis registry.
        _autoStartCheck = new CheckBox
        {
            Text = "Jalan otomatis saat Windows menyala",
            ForeColor = Ink,
            BackColor = Bg,
            AutoSize = true,
            Location = new Point(14, 110),
        };
        header.Controls.Add(_autoStartCheck);
        Controls.Add(header);

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
        Controls.Add(bottom);
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
