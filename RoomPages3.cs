using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jembatan;

// Halaman Control Room tahap 3 (v4.2.0): Automation, Connections,
// Permissions & Security — terdaftar di RoomShell.

/// <summary>Halaman Automation (v4.2.0): mengelola rutinitas yang
/// tersimpan (Routines) — daftar, lihat isi aksi, hapus. Rutinitas
/// dibuat &amp; dijalankan agen lewat endpoint bridge; halaman ini
/// jujur soal batas itu.</summary>
public sealed class AutomationPage : UserControl
{
    private readonly ListView _list;
    private readonly TextBox _detail;

    public AutomationPage()
    {
        BackColor = RoomTheme.Bg;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 46,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(18, 10, 18, 0),
        };
        var refreshBtn = RoomUi.Btn("Segarkan");
        refreshBtn.Width = 92; refreshBtn.Height = 28;
        refreshBtn.Click += (s, e) => RefreshList();
        var deleteBtn = RoomUi.Btn("Hapus");
        deleteBtn.Width = 80; deleteBtn.Height = 28;
        deleteBtn.Margin = new Padding(10, 0, 0, 0);
        deleteBtn.Click += (s, e) => DeleteSelected();
        var note = new Label
        {
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin = new Padding(14, 7, 0, 0),
            BackColor = RoomTheme.Bg,
            Text = "Rutinitas dibuat & dijalankan agen lewat bridge — di sini kamu mengelola yang tersimpan.",
        };
        top.Controls.Add(refreshBtn);
        top.Controls.Add(deleteBtn);
        top.Controls.Add(note);
        Controls.Add(top);

        _detail = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 150,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Dim,
            BorderStyle = BorderStyle.None,
            Text = "Pilih rutinitas untuk melihat isi aksinya.",
        };
        Controls.Add(_detail);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
        };
        _list.Columns.Add("Nama rutinitas", 320);
        _list.Columns.Add("Jumlah aksi", 120);
        _list.SelectedIndexChanged += (s, e) => ShowSelected();
        Controls.Add(_list);
        _list.BringToFront();
        top.BringToFront();
    }

    public void RefreshList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var kv in Routines.List())
        {
            var item = new ListViewItem(kv.Key);
            item.SubItems.Add(kv.Value.ToString());
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private string SelectedName()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return _list.SelectedItems[0].Text;
    }

    private void ShowSelected()
    {
        string name = SelectedName();
        if (name == null) return;
        if (Routines.TryGet(name, out string json))
            _detail.Text = json;
        else
            _detail.Text = "(isi rutinitas tidak terbaca)";
    }

    private void DeleteSelected()
    {
        string name = SelectedName();
        if (name == null) return;
        var jawab = MessageBox.Show(
            "Hapus rutinitas \"" + name + "\" secara permanen?",
            "Automation", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (jawab != DialogResult.Yes) return;
        Routines.Delete(name);
        _detail.Text = "Rutinitas \"" + name + "\" dihapus.";
        RefreshList();
    }
}

/// <summary>Halaman Connections (v4.2.0): status bridge, relay
/// Tailscale, dan kesehatan server dari BuildHealth() — dipanggil
/// di utas latar karena probe UI-nya bisa memblokir 1,5 detik.</summary>
public sealed class ConnectionsPage : UserControl
{
    private readonly MainForm _owner;
    private readonly AgentServer _server;
    private readonly Label _bridgeBody, _tsBody, _healthBody;
    private readonly Button _toggleBtn;
    private bool _refreshing;

    public ConnectionsPage(MainForm owner, AgentServer server)
    {
        _owner = owner;
        _server = server;
        BackColor = RoomTheme.Bg;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(18, 14, 18, 18),
        };
        Controls.Add(flow);

        _bridgeBody = Card(flow, "BRIDGE");
        _toggleBtn = RoomUi.Btn("Putuskan Akses");
        _toggleBtn.Width = 150; _toggleBtn.Height = 30;
        _toggleBtn.Dock = DockStyle.None;
        _toggleBtn.Margin = new Padding(14, 0, 0, 12);
        _toggleBtn.Click += (s, e) => { _owner.ToggleServer(); RefreshData(); };
        flow.Controls.Add(_toggleBtn);
        _tsBody = Card(flow, "TAILSCALE");
        _healthBody = Card(flow, "KESEHATAN SERVER");

        flow.Resize += (s, e) =>
        {
            int w = flow.ClientSize.Width - 44;
            if (w < 300) return;
            foreach (Control c in flow.Controls)
                if (c is RoundedPanel rp) rp.Width = w;
        };
    }

    private Label Card(FlowLayoutPanel flow, string caption)
    {
        var card = new RoundedPanel
        {
            CornerRadius = 12,
            FillColor = RoomTheme.Card,
            BorderColor = RoomTheme.CardEdge,
            BackColor = RoomTheme.Bg,
            Width = 600,
            Height = 108,
            Margin = new Padding(0, 0, 0, 12),
        };
        var cap = new Label
        {
            Text = caption,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 12),
            BackColor = RoomTheme.Card,
        };
        var body = new Label
        {
            Text = "…",
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 10f),
            AutoSize = true,
            Location = new Point(14, 36),
            BackColor = RoomTheme.Card,
        };
        card.Controls.Add(cap);
        card.Controls.Add(body);
        flow.Controls.Add(card);
        return body;
    }

    public void RefreshIfVisible()
    {
        if (Visible) RefreshData();
    }

    public void RefreshData()
    {
        if (_refreshing) return;
        _refreshing = true;
        Task.Run(() =>
        {
            Dictionary<string, object> h = null;
            string relay = "";
            try { h = _server.BuildHealth(); } catch { }
            try { relay = AgentServer.TailscaleRelay(); } catch { }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _refreshing = false;
                    if (IsDisposed) return;
                    bool jalan = _server.IsRunning;
                    string up = "—";
                    if (h != null && h.TryGetValue("uptimeSec", out var u))
                        up = ((long)u / 60) + " mnt";
                    _bridgeBody.Text =
                        (jalan ? "Mendengarkan" : "Berhenti") +
                        "  •  port " + AgentServer.Port +
                        "  •  v" + MainForm.DisplayVersion +
                        "\nUptime " + up;
                    _toggleBtn.Text = jalan ? "Putuskan Akses" : "Sambungkan";
                    _tsBody.Text = "Relay: " + (string.IsNullOrEmpty(relay) ? "—" : relay) +
                        "\nLantai RTT lewat relay Tailscale ±490 ms — kecepatan dijaga dengan mengurangi panggilan, bukan mengejar relay.";
                    if (h != null)
                    {
                        var ui = h["ui"] as Dictionary<string, object>;
                        string resp = ui != null && ui.TryGetValue("responsiveMs", out var r) ? r.ToString() : "—";
                        _healthBody.Text =
                            "UI responsif " + resp + " ms  •  Shell " + (SafeBool(h, "shellAlive") ? "hidup" : "mati") +
                            "\nOps UI berjalan " + h["uiOpsRunning"] + "  •  Seq event terakhir " + h["eventsLastSeq"];
                    }
                }));
            }
            catch { _refreshing = false; }
        });
    }

    private static bool SafeBool(Dictionary<string, object> h, string key)
    {
        try { return h.TryGetValue(key, out var v) && v is bool b && b; }
        catch { return false; }
    }
}

/// <summary>Halaman Permissions &amp; Security (v4.2.0): mode
/// kendali MuseLink sendiri — admin, lease input, Emergency Stop.
/// Kartu Allow adalah milik runtime Muse di luar aplikasi;
/// halaman ini jujur soal batas itu (gap Fase 1).</summary>
public sealed class SecurityPage : UserControl
{
    private readonly AgentServer _server;
    private readonly Label _modeBody, _haltBody;
    private readonly Button _haltBtn;
    private bool _refreshing;

    public SecurityPage(AgentServer server)
    {
        _server = server;
        BackColor = RoomTheme.Bg;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(18, 14, 18, 18),
        };
        Controls.Add(flow);

        _modeBody = Card(flow, "MODE KENDALI", 96);
        _haltBody = Card(flow, "EMERGENCY STOP", 96);
        _haltBtn = RoomUi.Btn("Hentikan Input");
        _haltBtn.Width = 170; _haltBtn.Height = 32;
        _haltBtn.Margin = new Padding(14, 0, 0, 12);
        _haltBtn.Click += (s, e) => ToggleHalt();
        flow.Controls.Add(_haltBtn);
        Card(flow, "CATATAN IZIN", 96).Text =
            "Kartu Allow (persetujuan tindakan agen) diterbitkan runtime Muse di luar aplikasi ini —\n" +
            "tidak bisa ditampilkan atau ditekan dari sini. Yang dikendalikan halaman ini: mode admin,\n" +
            "lease input eksklusif, dan Emergency Stop milik MuseLink sendiri.";

        flow.Resize += (s, e) =>
        {
            int w = flow.ClientSize.Width - 44;
            if (w < 300) return;
            foreach (Control c in flow.Controls)
                if (c is RoundedPanel rp) rp.Width = w;
        };
    }

    private Label Card(FlowLayoutPanel flow, string caption, int height)
    {
        var card = new RoundedPanel
        {
            CornerRadius = 12,
            FillColor = RoomTheme.Card,
            BorderColor = RoomTheme.CardEdge,
            BackColor = RoomTheme.Bg,
            Width = 600,
            Height = height,
            Margin = new Padding(0, 0, 0, 12),
        };
        var cap = new Label
        {
            Text = caption,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 12),
            BackColor = RoomTheme.Card,
        };
        var body = new Label
        {
            Text = "…",
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 10f),
            AutoSize = true,
            Location = new Point(14, 36),
            BackColor = RoomTheme.Card,
        };
        card.Controls.Add(cap);
        card.Controls.Add(body);
        flow.Controls.Add(card);
        return body;
    }

    private void ToggleHalt()
    {
        if (!_server.InputHalted)
        {
            var jawab = MessageBox.Show(
                "Hentikan SEMUA input Muse sekarang?\n\nOperasi yang antre dibatalkan, shell dihentikan, dan semua kendali ditolak sampai Stop dilepas dari aplikasi ini.",
                "Emergency Stop", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (jawab != DialogResult.Yes) return;
            _server.SetHalt(true, "aplikasi (halaman Permissions & Security)");
        }
        else
        {
            _server.SetHalt(false, "aplikasi (halaman Permissions & Security)");
        }
        RefreshData();
    }

    public void RefreshIfVisible()
    {
        if (Visible) RefreshData();
    }

    public void RefreshData()
    {
        if (_refreshing) return;
        _refreshing = true;
        Task.Run(() =>
        {
            Dictionary<string, object> h = null;
            try { h = _server.BuildHealth(); } catch { }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _refreshing = false;
                    if (IsDisposed) return;
                    bool admin = false;
                    try
                    {
                        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                            admin = new System.Security.Principal.WindowsPrincipal(id)
                                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                    }
                    catch { }
                    string lease = "—";
                    if (h != null && h.TryGetValue("inputLease", out var l) && l != null)
                        lease = l.ToString();
                    _modeBody.Text =
                        "Admin: " + (admin ? "ya" : "tidak") +
                        "  •  Lease input: " + lease +
                        "\nSatu pemilik input pada satu waktu — batch lain ditolak 429 input_leased.";
                    bool halted = _server.InputHalted;
                    _haltBody.Text = halted
                        ? "AKTIF — semua input agen ditahan (423 input_halted)."
                        : "Tidak aktif — input berjalan normal.";
                    _haltBody.ForeColor = halted ? RoomTheme.Red : RoomTheme.Dim;
                    _haltBtn.Text = halted ? "Lepas Stop" : "Hentikan Input";
                }));
            }
            catch { _refreshing = false; }
        });
    }
}
