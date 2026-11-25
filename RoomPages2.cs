using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Jembatan;

/// <summary>Halaman Command Center (v4.1.0): menjalankan perintah
/// PowerShell dari dalam aplikasi — padanan internal /exec. Gerbang
/// izin brief Fase 4: saat Emergency Stop aktif atau bridge berhenti,
/// perintah ditolak di muka dengan catatan alasannya.</summary>
public sealed class CommandPage : UserControl
{
    private readonly AgentServer _server;
    private readonly TextBox _cmd;
    private readonly Button _runBtn;
    private readonly Label _gateNote;
    private readonly TextBox _output;
    private readonly ListBox _history;
    private readonly System.Windows.Forms.Timer _gateTimer;
    private bool _running;

    public CommandPage(AgentServer server)
    {
        _server = server;
        BackColor = RoomTheme.Bg;

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(18, 10, 18, 0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _cmd = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 10.5f),
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 8, 0),
        };
        _cmd.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunCurrent(); }
        };
        top.Controls.Add(_cmd, 0, 0);

        _runBtn = RoomUi.Btn("Jalankan");
        _runBtn.Margin = new Padding(0, 0, 8, 0);
        _runBtn.Click += (s, e) => RunCurrent();
        top.Controls.Add(_runBtn, 1, 0);

        var clearBtn = RoomUi.Btn("Bersihkan");
        clearBtn.Click += (s, e) => { _output.Clear(); };
        top.Controls.Add(clearBtn, 2, 0);
        Controls.Add(top);

        _gateNote = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 9f),
            Padding = new Padding(18, 4, 18, 0),
            BackColor = RoomTheme.Bg,
            Text = "",
        };
        Controls.Add(_gateNote);
        _gateNote.BringToFront();
        top.BringToFront();

        _history = new ListBox
        {
            Dock = DockStyle.Bottom,
            Height = 104,
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Dim,
            BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5f),
            IntegralHeight = false,
        };
        _history.DoubleClick += (s, e) =>
        {
            if (_history.SelectedItem is string h) { _cmd.Text = h; RunCurrent(); }
        };
        Controls.Add(_history);

        var histCap = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = "RIWAYAT SESI INI — klik ganda untuk menjalankan lagi",
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
            Padding = new Padding(18, 6, 0, 0),
            BackColor = RoomTheme.Bg,
        };
        Controls.Add(histCap);

        _output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font("Consolas", 9.5f),
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(_output);
        _output.BringToFront();

        _gateTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _gateTimer.Tick += (s, e) => RefreshGate();
        _gateTimer.Start();
        Disposed += (s, e) => { _gateTimer.Stop(); _gateTimer.Dispose(); };
        RefreshGate();
        AppendOut("Command Center siap. Ketik perintah PowerShell lalu Enter.\r\n" +
                  "Padanan internal /exec — keluaran ditangkap di sini, batas 60 detik.\r\n");
    }

    private void RefreshGate()
    {
        bool halted = _server.InputHalted;
        bool jalan = _server.IsRunning;
        _runBtn.Enabled = !_running && !halted && jalan;
        _gateNote.Text = halted
            ? "Emergency Stop AKTIF — perintah ditahan sampai Stop dilepas dari aplikasi."
            : !jalan
                ? "Bridge berhenti — sambungkan lagi dari kartu status di kiri bawah."
                : "Izin: perintah berjalan sebagai proses aplikasi (admin: " +
                  (IsAdmin() ? "ya" : "tidak") + ").";
        _gateNote.ForeColor = halted ? RoomTheme.Red : RoomTheme.Faint;
    }

    private static bool IsAdmin()
    {
        try
        {
            using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void AppendOut(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => AppendOut(text))); return; }
        _output.AppendText(text);
    }

    private void RunCurrent()
    {
        string cmd = _cmd.Text.Trim();
        if (cmd.Length == 0 || _running) return;
        if (_server.InputHalted) { RefreshGate(); return; }
        _running = true;
        RefreshGate();
        if (!_history.Items.Contains(cmd)) _history.Items.Insert(0, cmd);
        AppendOut("\r\n> " + cmd + "\r\n");
        Task.Run(() =>
        {
            string hasil;
            try { hasil = RunPowerShell(cmd); }
            catch (Exception ex) { hasil = "[gagal memulai] " + ex.Message + "\r\n"; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    AppendOut(hasil);
                    _running = false;
                    RefreshGate();
                }));
            }
            catch { /* halaman sudah dibuang */ }
        });
    }

    private static string RunPowerShell(string cmd)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" +
                        cmd.Replace("\"", "\\\"") + "\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using (var p = new Process { StartInfo = psi })
        {
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            bool selesai = p.WaitForExit(60000);
            if (!selesai)
            {
                try { p.Kill(); } catch { }
                return "[dibunuh — melewati batas 60 detik]\r\n" +
                       SafeResult(outTask) + SafeResult(errTask);
            }
            string o = SafeResult(outTask), e = SafeResult(errTask);
            var sb = new StringBuilder();
            if (o.Length > 0) sb.Append(o);
            if (e.Length > 0) sb.Append(e);
            sb.Append("[selesai — kode keluar ").Append(p.ExitCode).Append("]\r\n");
            return sb.ToString();
        }
    }

    private static string SafeResult(Task<string> t)
    {
        try { return t.Wait(1500) ? (t.Result ?? "") : ""; }
        catch { return ""; }
    }
}

/// <summary>Halaman Desktop Control (v4.1.0): daftar jendela live
/// (padanan internal /windows) dengan Fokus &amp; Tutup, plus
/// pratinjau desktop yang diperbarui selama halaman terbuka.</summary>
public sealed class DesktopPage : UserControl
{
    private readonly ListView _list;
    private readonly PictureBox _preview;
    private readonly Label _note;
    private bool _capturing;

    public DesktopPage()
    {
        BackColor = RoomTheme.Bg;

        // v4.1.1: tata letak diganti TableLayoutPanel murni (pola
        // Command Center yang terbukti rapi) — versi FlowLayout +
        // Dock sebelumnya membuat tombol bilah alat tergeser &
        // teksnya terpotong.
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48f));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Controls.Add(root);

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(18, 10, 18, 6),
            Margin = new Padding(0),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var refreshBtn = RoomUi.Btn("Segarkan");
        refreshBtn.Margin = new Padding(0, 0, 8, 0);
        refreshBtn.Click += (s, e) => RefreshAll();
        top.Controls.Add(refreshBtn, 0, 0);
        var focusBtn = RoomUi.Btn("Fokus");
        focusBtn.Margin = new Padding(0, 0, 8, 0);
        focusBtn.Click += (s, e) => FocusSelected();
        top.Controls.Add(focusBtn, 1, 0);
        var closeBtn = RoomUi.Btn("Tutup");
        closeBtn.Margin = new Padding(0, 0, 8, 0);
        closeBtn.Click += (s, e) => CloseSelected();
        top.Controls.Add(closeBtn, 2, 0);
        _note = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = RoomTheme.Bg,
            Text = "Pratinjau diperbarui otomatis saat halaman terbuka.",
        };
        top.Controls.Add(_note, 3, 0);
        root.Controls.Add(top, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = RoomTheme.Bg,
            Padding = new Padding(0),
            Margin = new Padding(0),
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470f));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.Controls.Add(body, 0, 1);

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
            Margin = new Padding(0),
        };
        _list.Columns.Add("Jendela", 240);
        _list.Columns.Add("Proses", 110);
        _list.Columns.Add("PID", 56);
        _list.Columns.Add("Keadaan", 60);
        _list.DoubleClick += (s, e) => FocusSelected();
        body.Controls.Add(_list, 0, 0);

        _preview = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = RoomTheme.Panel,
            Margin = new Padding(0),
        };
        body.Controls.Add(_preview, 1, 0);
    }

    public void RefreshAll()
    {
        RefreshWindows();
        RefreshPreview();
    }

    /// <summary>Dipanggil timer kerangka saat halaman ini tampil.</summary>
    public void RefreshIfVisible()
    {
        if (!Visible) return;
        RefreshPreview();
    }

    private void RefreshWindows()
    {
        List<NativeInput.WindowInfo> wins;
        try { wins = NativeInput.ListWindows(); }
        catch { return; }
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var w in wins)
        {
            if (string.IsNullOrEmpty(w.Title)) continue;
            var item = new ListViewItem(w.Title);
            item.SubItems.Add(w.Process ?? "");
            item.SubItems.Add(w.Pid.ToString());
            item.SubItems.Add(w.State ?? "");
            item.Tag = w;
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        _note.Text = wins.Count + " jendela • pratinjau diperbarui otomatis saat halaman terbuka.";
    }

    private NativeInput.WindowInfo Selected()
    {
        if (_list.SelectedItems.Count == 0) return null;
        return _list.SelectedItems[0].Tag as NativeInput.WindowInfo;
    }

    private void FocusSelected()
    {
        var w = Selected();
        if (w == null) return;
        try { NativeInput.FocusWindow(w.Handle); } catch { }
        RefreshWindows();
    }

    private void CloseSelected()
    {
        var w = Selected();
        if (w == null) return;
        try
        {
            using (var p = Process.GetProcessById(w.Pid))
                p.CloseMainWindow();
        }
        catch { }
        var t = new System.Windows.Forms.Timer { Interval = 700 };
        t.Tick += (s, e) => { t.Stop(); t.Dispose(); RefreshWindows(); };
        t.Start();
    }

    private void RefreshPreview()
    {
        if (_capturing || !Visible) return;
        _capturing = true;
        Task.Run(() =>
        {
            Bitmap bmp = null;
            try { bmp = NativeInput.CaptureScreenScaled(880); } catch { }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _capturing = false;
                    if (IsDisposed) { bmp?.Dispose(); return; }
                    var old = _preview.Image;
                    _preview.Image = bmp;
                    old?.Dispose();
                }));
            }
            catch { _capturing = false; bmp?.Dispose(); }
        });
    }
}

/// <summary>Halaman System Logs (v4.1.0): tiga sumber log dalam
/// satu tampilan — Event (EventLog), Operasi (snapshot server),
/// Log aplikasi (buffer cincin MainForm) — dengan ekspor.</summary>
public sealed class LogsPage : UserControl
{
    private readonly MainForm _owner;
    private readonly AgentServer _server;
    private readonly ComboBox _source;
    private readonly CheckBox _auto;
    private readonly ListView _list;
    private readonly Label _count;

    public LogsPage(MainForm owner, AgentServer server)
    {
        _owner = owner;
        _server = server;
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
        _source = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 150,
            Font = new Font("Segoe UI", 9.5f),
            BackColor = RoomTheme.Panel,
            ForeColor = RoomTheme.Text,
        };
        _source.Items.AddRange(new object[] { "Event", "Operasi", "Log aplikasi" });
        _source.SelectedIndex = 0;
        _source.SelectedIndexChanged += (s, e) => RefreshList();
        top.Controls.Add(_source);

        var refreshBtn = RoomUi.Btn("Segarkan");
        refreshBtn.Width = 92; refreshBtn.Height = 28;
        refreshBtn.Margin = new Padding(10, 0, 0, 0);
        refreshBtn.Click += (s, e) => RefreshList();
        top.Controls.Add(refreshBtn);

        _auto = new CheckBox
        {
            Text = "Ikuti otomatis",
            ForeColor = RoomTheme.Dim,
            Font = new Font("Segoe UI", 9.5f),
            AutoSize = true,
            Checked = true,
            Margin = new Padding(12, 5, 0, 0),
            BackColor = RoomTheme.Bg,
        };
        top.Controls.Add(_auto);

        var exportBtn = RoomUi.Btn("Ekspor…");
        exportBtn.Width = 92; exportBtn.Height = 28;
        exportBtn.Margin = new Padding(10, 0, 0, 0);
        exportBtn.Click += (s, e) => Export();
        top.Controls.Add(exportBtn);

        _count = new Label
        {
            ForeColor = RoomTheme.Faint,
            Font = new Font("Segoe UI", 9f),
            AutoSize = true,
            Margin = new Padding(14, 7, 0, 0),
            BackColor = RoomTheme.Bg,
            Text = "",
        };
        top.Controls.Add(_count);
        Controls.Add(top);

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
        Controls.Add(_list);
        _list.BringToFront();
        top.BringToFront();
    }

    private string Source => _source.SelectedItem as string ?? "Event";

    /// <summary>Dipanggil timer kerangka saat halaman ini tampil.</summary>
    public void RefreshIfAuto()
    {
        if (Visible && _auto.Checked) RefreshList();
    }

    public void RefreshList()
    {
        _list.BeginUpdate();
        _list.Columns.Clear();
        _list.Items.Clear();
        int n = 0;
        if (Source == "Event")
        {
            _list.Columns.Add("Waktu", 84);
            _list.Columns.Add("Jenis", 150);
            _list.Columns.Add("Detail", 640);
            var ev = EventLog.Since(0, 300);
            for (int i = ev.Count - 1; i >= 0; i--)
            {
                var item = new ListViewItem(ev[i].Time);
                item.SubItems.Add(ev[i].Kind);
                item.SubItems.Add(ev[i].Detail);
                _list.Items.Add(item);
                n++;
            }
        }
        else if (Source == "Operasi")
        {
            _list.Columns.Add("Waktu", 84);
            _list.Columns.Add("Perintah", 240);
            _list.Columns.Add("Status", 110);
            _list.Columns.Add("TaskId", 110);
            _list.Columns.Add("Ringkasan", 330);
            foreach (var o in _server.RecentOpsSnapshot(300))
            {
                var item = new ListViewItem(o[0]);
                item.SubItems.Add(o[1] + " " + o[2]);
                item.SubItems.Add(o[3] + " • " + o[4] + " ms");
                item.SubItems.Add(o[5]);
                item.SubItems.Add(o[6]);
                _list.Items.Add(item);
                n++;
            }
        }
        else
        {
            _list.Columns.Add("Waktu", 84);
            _list.Columns.Add("Isi log", 790);
            foreach (var line in _owner.RecentLogLines(300))
            {
                int tab = line.IndexOf('\t');
                var item = new ListViewItem(tab >= 0 ? line.Substring(0, tab) : "");
                item.SubItems.Add(tab >= 0 ? line.Substring(tab + 1) : line);
                _list.Items.Add(item);
                n++;
            }
        }
        _list.EndUpdate();
        _count.Text = n + " baris • sumber: " + Source;
    }

    private void Export()
    {
        using (var dlg = new SaveFileDialog())
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (Source == "Log aplikasi")
            {
                dlg.Filter = "Teks (*.txt)|*.txt";
                dlg.FileName = "muselink-log-" + stamp + ".txt";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                var lines = _owner.RecentLogLines(300);
                File.WriteAllText(dlg.FileName, string.Join("\r\n", lines));
                _count.Text = "Terekspor " + lines.Count + " baris ke " + Path.GetFileName(dlg.FileName);
                return;
            }
            dlg.Filter = "JSON (*.json)|*.json";
            dlg.FileName = "muselink-" + (Source == "Event" ? "events-" : "ops-") + stamp + ".json";
            if (dlg.ShowDialog() != DialogResult.OK) return;
            var data = new List<Dictionary<string, string>>();
            if (Source == "Event")
            {
                foreach (var e in EventLog.Since(0, 300))
                    data.Add(new Dictionary<string, string>
                    { ["time"] = e.Time, ["kind"] = e.Kind, ["detail"] = e.Detail });
            }
            else
            {
                foreach (var o in _server.RecentOpsSnapshot(300))
                    data.Add(new Dictionary<string, string>
                    {
                        ["time"] = o[0], ["method"] = o[1], ["path"] = o[2],
                        ["status"] = o[3], ["ms"] = o[4], ["taskId"] = o[5], ["summary"] = o[6],
                    });
            }
            File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
            _count.Text = "Terekspor " + data.Count + " baris ke " + Path.GetFileName(dlg.FileName);
        }
    }
}

/// <summary>Tombol utilitas bersama halaman-halaman Control Room
/// tahap 2 (gaya sama dengan tombol kerangka).</summary>
internal static class RoomUi
{
    public static Button Btn(string text)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = RoomTheme.Card,
            ForeColor = RoomTheme.Text,
            Font = new Font("Segoe UI", 9.5f),
            Cursor = Cursors.Hand,
            Dock = DockStyle.Fill,
        };
        b.FlatAppearance.BorderColor = RoomTheme.CardEdge;
        return b;
    }
}
