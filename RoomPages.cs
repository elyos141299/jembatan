using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>Halaman Overview Control Room (v4.0.0): empat kartu
/// status, Active Task, System Health, Desktop Context, dan
/// Recent Activity — semua dari data live yang sama dengan
/// GET /health, EventLog, dan sensor MainForm.</summary>
public sealed class OverviewPage : UserControl
{
    private readonly MainForm _owner;
    private readonly AgentServer _server;
    private readonly List<RoundedPanel> _cards = new List<RoundedPanel>();
    private readonly Label _claudeValue, _claudeSub;
    private readonly Label _bridgeValue, _bridgeSub;
    private readonly Label _sessionValue, _sessionSub;
    private readonly Label _permValue, _permSub;
    private readonly Label _taskBody;
    private readonly Label _healthBody;
    private readonly Label _contextBody;
    private readonly Label _recentBody;
    private bool _refreshing;

    public OverviewPage(MainForm owner, AgentServer server)
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

        // Baris 4 kartu status.
        var row = new TableLayoutPanel
        {
            ColumnCount = 4,
            RowCount = 1,
            Height = 96,
            BackColor = RoomTheme.Bg,
            Margin = new Padding(0, 0, 0, 12),
        };
        for (int i = 0; i < 4; i++) row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _claudeValue = MiniCard(row, 0, "CLAUDE AI", out _claudeSub);
        _bridgeValue = MiniCard(row, 1, "BRIDGE", out _bridgeSub);
        _sessionValue = MiniCard(row, 2, "SESI DESKTOP", out _sessionSub);
        _permValue = MiniCard(row, 3, "IZIN & KENDALI", out _permSub);
        flow.Controls.Add(row);
        _cards.Add(null); // penanda baris status untuk penyesuaian lebar

        _taskBody = BigCard(flow, "ACTIVE TASK", out _);
        _healthBody = BigCard(flow, "SYSTEM HEALTH", out _);
        _contextBody = BigCard(flow, "DESKTOP CONTEXT", out _);
        _recentBody = BigCard(flow, "RECENT ACTIVITY", out _);

        flow.Resize += (s, e) =>
        {
            int w = flow.ClientSize.Width - 44;
            if (w < 300) return;
            row.Width = w;
            foreach (Control c in flow.Controls)
                if (c is RoundedPanel rp) rp.Width = w;
        };
    }

    private Label MiniCard(TableLayoutPanel row, int col, string caption, out Label sub)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 12,
            FillColor = RoomTheme.Card,
            BorderColor = RoomTheme.CardEdge,
            BackColor = RoomTheme.Bg,
            Margin = new Padding(col == 0 ? 0 : 6, 0, col == 3 ? 0 : 6, 0),
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
        var val = new Label
        {
            Text = "…",
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(13, 32),
            BackColor = RoomTheme.Card,
        };
        sub = new Label
        {
            Text = "",
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Location = new Point(14, 60),
            BackColor = RoomTheme.Card,
        };
        card.Controls.Add(cap);
        card.Controls.Add(val);
        card.Controls.Add(sub);
        row.Controls.Add(card, col, 0);
        return val;
    }

    private Label BigCard(FlowLayoutPanel flow, string title, out RoundedPanel card)
    {
        card = new RoundedPanel
        {
            Width = 900,
            Height = 118,
            CornerRadius = 12,
            FillColor = RoomTheme.Card,
            BorderColor = RoomTheme.CardEdge,
            BackColor = RoomTheme.Bg,
            Margin = new Padding(0, 0, 0, 12),
        };
        var cap = new Label
        {
            Text = title,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(14, 10),
            BackColor = RoomTheme.Card,
        };
        var body = new Label
        {
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 10f),
            Location = new Point(14, 32),
            Size = new Size(860, 78),
            BackColor = RoomTheme.Card,
        };
        card.Controls.Add(cap);
        card.Controls.Add(body);
        flow.Controls.Add(card);
        _cards.Add(card);
        return body;
    }

    /// <summary>Penyegaran asinkron: BuildHealth memuat probe UI
    /// yang tidak boleh dipanggil dari utas UI itu sendiri.</summary>
    public void RefreshAsync()
    {
        if (_refreshing || IsDisposed) return;
        _refreshing = true;
        Task.Run(() =>
        {
            Dictionary<string, object> health = null;
            try { health = _server.BuildHealth(); } catch { /* abaikan */ }
            if (IsDisposed) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    _refreshing = false;
                    if (health != null) Apply(health);
                }));
            }
            catch { _refreshing = false; }
        });
    }

    private static string Str(Dictionary<string, object> d, string key)
        => d != null && d.TryGetValue(key, out var v) && v != null ? v.ToString() : "";

    private static long Num(Dictionary<string, object> d, string key)
    {
        if (d != null && d.TryGetValue(key, out var v) && v != null)
        {
            try { return Convert.ToInt64(v); } catch { return 0; }
        }
        return 0;
    }

    private void Apply(Dictionary<string, object> h)
    {
        bool jalan = _server.IsRunning;
        bool halted = _server.InputHalted;
        var ui = h["ui"] as Dictionary<string, object>;
        long probe = Num(ui, "responsiveMs");
        long upDetik = Num(h, "uptimeSec");
        string fg = Str(h, "foreground");
        string lease = Str(h, "inputLease");

        _bridgeValue.Text = halted ? "STOP DARURAT" : jalan ? "Mendengarkan" : "Berhenti";
        _bridgeValue.ForeColor = halted ? RoomTheme.Red : jalan ? RoomTheme.Green : RoomTheme.Dim;
        _bridgeSub.Text = "v" + Str(h, "version") + " • port " + AgentServer.Port;

        _claudeValue.Text = jalan ? "Aktif" : "—";
        _claudeValue.ForeColor = jalan ? RoomTheme.Text : RoomTheme.Dim;
        _claudeSub.Text = "seq event " + Num(h, "eventsLastSeq") + " • ops UI jalan " + Num(h, "uiOpsRunning");

        string fgTampil = string.IsNullOrEmpty(fg) ? "(tidak ada jendela depan)" : fg;
        if (fgTampil.Length > 30) fgTampil = fgTampil.Substring(0, 30) + "…";
        _sessionValue.Text = fgTampil;
        _sessionSub.Text = "shell " + (Str(h, "shellAlive") == "True" ? "hidup" : "mati");

        _permValue.Text = halted ? "Input DITAHAN" : "Input terbuka";
        _permValue.ForeColor = halted ? RoomTheme.Red : RoomTheme.Text;
        _permSub.Text = "admin " + (Settings.IsElevated() ? "ya" : "tidak")
            + " • lease " + (string.IsNullOrEmpty(lease) ? "—" : lease);

        var task = _server.LatestTaskActivity();
        _taskBody.Text = task == null
            ? "Tidak ada tugas berlabel taskId baru-baru ini."
            : "Task  " + task[0] + "\n" + task[1] + " operasi • terakhir: " + task[2] + " • " + task[3];

        string timeoutInfo = "tidak ada";
        if (h["lastUiTimeout"] is Dictionary<string, object> lt)
            timeoutInfo = Str(lt, "name") + " (" + Num(lt, "agoSec") + " dtk lalu)";
        _healthBody.Text =
            "UI responsif: " + (probe < 0 ? "tidak terjangkau" : probe + " ms")
            + "   •   Uptime: " + (upDetik >= 3600 ? (upDetik / 3600) + " j " + ((upDetik % 3600) / 60) + " m" : (upDetik / 60) + " m")
            + "\nCPU: " + _owner.RoomCpuText + "   •   GPU: " + _owner.RoomGpuText
            + "\nRAM: " + SysSensors.RamText() + "   •   Timeout UI terakhir: " + timeoutInfo;

        string layar;
        try
        {
            var scr = Screen.PrimaryScreen.Bounds;
            layar = scr.Width + "×" + scr.Height;
        }
        catch { layar = "—"; }
        _contextBody.Text =
            "Jendela depan: " + (string.IsNullOrEmpty(fg) ? "—" : fg)
            + "\nLayar utama: " + layar + "   •   Mode tangkap: " + DxgiCapture.Mode + "/" + DxgiCapture.ScaleMode;

        var ev = EventLog.Since(0, 200);
        var baris = new List<string>();
        for (int i = ev.Count - 1; i >= 0 && baris.Count < 5; i--)
            baris.Add(ev[i].Time + "  " + ev[i].Kind + " — " + ev[i].Detail);
        _recentBody.Text = baris.Count == 0 ? "Belum ada kejadian tercatat." : string.Join("\n", baris);
    }
}

/// <summary>Halaman Activity Monitor (v4.0.0): gabungan EventLog
/// + operasi Route + operasi UI, dengan filter sumber, pencarian,
/// detail baris, dan ekspor JSON.</summary>
public sealed class ActivityPage : UserControl
{
    private readonly AgentServer _server;
    private readonly ListView _list;
    private readonly TextBox _search;
    private readonly TextBox _detail;
    private readonly Label _countLabel;
    private readonly CheckBox _autoCheck;
    private readonly List<Button> _filterBtns = new List<Button>();
    private string _filter = "Semua";
    private List<string[]> _rows = new List<string[]>(); // [waktu, sumber, jenis, detail, taskId, info]

    public ActivityPage(AgentServer server)
    {
        _server = server;
        BackColor = RoomTheme.Bg;
        Padding = new Padding(18, 14, 18, 14);

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = RoomTheme.Bg };
        int x = 0;
        foreach (var f in new[] { "Semua", "Event", "Operasi", "UI" })
        {
            var b = new Button
            {
                Text = f,
                Location = new Point(x, 4),
                Size = new Size(84, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = f == "Semua" ? RoomTheme.AccentDeep : RoomTheme.Panel,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderColor = RoomTheme.CardEdge;
            string nama = f;
            b.Click += (s, e) => { _filter = nama; PaintFilters(); RefreshList(); };
            toolbar.Controls.Add(b);
            _filterBtns.Add(b);
            x += 90;
        }
        _search = new TextBox
        {
            Location = new Point(x + 8, 7),
            Width = 220,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
        };
        _search.TextChanged += (s, e) => RefreshList();
        toolbar.Controls.Add(_search);
        _autoCheck = new CheckBox
        {
            Text = "Ikuti langsung",
            Checked = true,
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Location = new Point(x + 240, 10),
            BackColor = RoomTheme.Bg,
        };
        toolbar.Controls.Add(_autoCheck);
        var exportBtn = new Button
        {
            Text = "Export Logs (JSON)",
            Size = new Size(140, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 9f),
            Cursor = Cursors.Hand,
        };
        exportBtn.FlatAppearance.BorderColor = RoomTheme.CardEdge;
        exportBtn.Location = new Point(toolbar.Width - 140, 4);
        toolbar.Resize += (s, e) => exportBtn.Location = new Point(toolbar.Width - 140, 4);
        exportBtn.Click += (s, e) => Export();
        toolbar.Controls.Add(exportBtn);
        Controls.Add(toolbar);

        _detail = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 58,
            Multiline = true,
            ReadOnly = true,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Dim,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 9f),
            Text = "Pilih satu baris untuk melihat detailnya.",
        };
        Controls.Add(_detail);

        _countLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f),
            Text = "",
            BackColor = RoomTheme.Bg,
        };
        Controls.Add(_countLabel);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            BackColor = RoomTheme.Card,
            ForeColor = RoomTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f),
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Waktu", 72);
        _list.Columns.Add("Sumber", 66);
        _list.Columns.Add("Jenis", 170);
        _list.Columns.Add("Detail", 430);
        _list.Columns.Add("TaskId", 120);
        _list.Columns.Add("Info", 130);
        _list.SelectedIndexChanged += (s, e) =>
        {
            if (_list.SelectedItems.Count == 0) return;
            var it = _list.SelectedItems[0];
            _detail.Text = "[" + it.SubItems[0].Text + "] " + it.SubItems[1].Text + " • " + it.SubItems[2].Text
                + "\r\n" + it.SubItems[3].Text
                + (it.SubItems[4].Text.Length > 0 ? "\r\nTaskId: " + it.SubItems[4].Text : "")
                + "\r\n" + it.SubItems[5].Text;
        };
        Controls.Add(_list);
        RefreshList();
    }

    private void PaintFilters()
    {
        foreach (var b in _filterBtns)
        {
            bool aktif = b.Text == _filter;
            b.BackColor = aktif ? RoomTheme.AccentDeep : RoomTheme.Panel;
        }
    }

    public void RefreshIfAuto()
    {
        if (_autoCheck != null && _autoCheck.Checked) RefreshList();
    }

    public void RefreshList()
    {
        if (IsDisposed) return;
        var semua = new List<string[]>();
        var ev = EventLog.Since(0, 200);
        for (int i = ev.Count - 1; i >= 0; i--)
            semua.Add(new[] { ev[i].Time, "Event", ev[i].Kind, ev[i].Detail, "", "" });
        foreach (var o in _server.RecentOpsSnapshot(50))
            semua.Add(new[] { o[0], "Operasi", o[1] + " " + o[2], o[6], o[5], o[3] + " • " + o[4] + " ms" });
        foreach (var u in _server.RecentUiOpsSnapshot(30))
            semua.Add(new[] { "", "UI", u[1], "operasi UI #" + u[0], u[4], u[2] + " • " + u[3] + " ms" });
        semua.Sort((a, b) => string.Compare(b[0], a[0], StringComparison.Ordinal));

        string cari = _search.Text.Trim();
        _rows = new List<string[]>();
        foreach (var r in semua)
        {
            if (_filter != "Semua" && r[1] != _filter) continue;
            if (cari.Length > 0 &&
                (r[2] + " " + r[3] + " " + r[4]).IndexOf(cari, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _rows.Add(r);
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var r in _rows)
        {
            var item = new ListViewItem(r[0]);
            for (int i = 1; i < 6; i++) item.SubItems.Add(r[i]);
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _countLabel.Text = _rows.Count + " baris • EventLog seq " + EventLog.LastSeq;
    }

    private void Export()
    {
        using (var dlg = new SaveFileDialog())
        {
            dlg.Filter = "JSON (*.json)|*.json";
            dlg.FileName = "muselink-activity-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            var data = new List<Dictionary<string, string>>();
            foreach (var r in _rows)
            {
                data.Add(new Dictionary<string, string>
                {
                    ["time"] = r[0], ["source"] = r[1], ["kind"] = r[2],
                    ["detail"] = r[3], ["taskId"] = r[4], ["info"] = r[5],
                });
            }
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
            _countLabel.Text = "Terekspor " + data.Count + " baris ke " + Path.GetFileName(dlg.FileName);
        }
    }
}

/// <summary>Halaman penanda tahap (v4.0.0): halaman yang belum
/// dibangun ditampilkan jujur dengan tahap bangunnya — tidak ada
/// isi pura-pura.</summary>
public sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(string title, string note)
    {
        BackColor = RoomTheme.Bg;
        var t = new Label
        {
            Text = title,
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            BackColor = RoomTheme.Bg,
        };
        var n = new Label
        {
            Text = note,
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 10.5f),
            BackColor = RoomTheme.Bg,
            AutoSize = true,
            MaximumSize = new Size(560, 0),
        };
        Controls.Add(t);
        Controls.Add(n);
        Resize += (s, e) =>
        {
            t.Location = new Point(Math.Max(24, (Width - t.Width) / 2), Math.Max(40, Height / 2 - 60));
            n.Location = new Point(Math.Max(24, (Width - n.Width) / 2), t.Bottom + 12);
        };
    }
}
