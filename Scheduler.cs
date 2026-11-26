using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Jembatan;

/// <summary>
/// Penyimpanan jadwal (v5.6.0 — Scheduler Engine): SQLite lewat
/// Microsoft.Data.Sqlite di %LOCALAPPDATA%\Jembatan\schedules.db
/// (pilihan penyimpanan terstruktur untuk data yang tumbuh —
/// menggantikan pola JSON bila nanti data membesar). Satu baris
/// per jadwal: rutinitas yang dijalankan, pola waktunya
/// (daily/weekly/interval/once), status aktif, catatan jalan
/// terakhir, dan slot berikutnya (nextRun) hasil ComputeNext.
/// Mesin penjalanannya (thread penilik + eksekusi rutinitas)
/// hidup di AgentServer; kelas ini murni simpan + hitung.
/// </summary>
public static class Schedules
{
    private static readonly object Lock = new object();
    private static bool _ready;

    public sealed class Row
    {
        public string Name = "";
        public string Routine = "";
        public string Kind = "";      // daily | weekly | interval | once
        public string At = "";        // "HH:mm" (daily/weekly) atau ISO lokal (once)
        public int Day = -1;          // 0=Minggu..6=Sabtu (weekly)
        public int Minutes = 0;       // interval (menit)
        public bool Enabled = true;
        public string LastRun = "";   // ISO lokal atau ""
        public string LastStatus = "";// sukses | gagal | terlewat | rutinitas hilang | ""
        public string NextRun = "";   // ISO lokal atau "" (tidak ada slot)
    }

    public static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss");

    private static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "schedules.db");

    private static SqliteConnection Open()
    {
        lock (Lock)
        {
            if (!_ready)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DbPath));
                try { SQLitePCL.Batteries_V2.Init(); } catch { /* provider bawaan paket biasanya sudah init */ }
                _ready = true;
            }
        }
        var conn = new SqliteConnection("Data Source=" + DbPath);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "CREATE TABLE IF NOT EXISTS schedules(" +
                "name TEXT PRIMARY KEY, routine TEXT NOT NULL, kind TEXT NOT NULL," +
                "at TEXT NOT NULL DEFAULT '', day INTEGER NOT NULL DEFAULT -1," +
                "minutes INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1," +
                "lastRun TEXT NOT NULL DEFAULT '', lastStatus TEXT NOT NULL DEFAULT ''," +
                "nextRun TEXT NOT NULL DEFAULT '')";
            cmd.ExecuteNonQuery();
        }
        return conn;
    }

    private static Row ReadRow(SqliteDataReader rd) => new Row
    {
        Name = rd.GetString(0),
        Routine = rd.GetString(1),
        Kind = rd.GetString(2),
        At = rd.GetString(3),
        Day = rd.GetInt32(4),
        Minutes = rd.GetInt32(5),
        Enabled = rd.GetInt32(6) != 0,
        LastRun = rd.GetString(7),
        LastStatus = rd.GetString(8),
        NextRun = rd.GetString(9),
    };

    private const string Cols = "name, routine, kind, at, day, minutes, enabled, lastRun, lastStatus, nextRun";

    public static List<Row> List()
    {
        var hasil = new List<Row>();
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + Cols + " FROM schedules ORDER BY name";
                using (var rd = cmd.ExecuteReader())
                    while (rd.Read()) hasil.Add(ReadRow(rd));
            }
        }
        return hasil;
    }

    public static Row Get(string name)
    {
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT " + Cols + " FROM schedules WHERE name = $n";
                cmd.Parameters.AddWithValue("$n", name);
                using (var rd = cmd.ExecuteReader())
                    return rd.Read() ? ReadRow(rd) : null;
            }
        }
    }

    public static void Save(Row r)
    {
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO schedules(" + Cols + ") VALUES($n,$r,$k,$a,$d,$m,$e,$lr,$ls,$nx) " +
                    "ON CONFLICT(name) DO UPDATE SET routine=$r, kind=$k, at=$a, day=$d, " +
                    "minutes=$m, enabled=$e, nextRun=$nx";
                cmd.Parameters.AddWithValue("$n", r.Name);
                cmd.Parameters.AddWithValue("$r", r.Routine);
                cmd.Parameters.AddWithValue("$k", r.Kind);
                cmd.Parameters.AddWithValue("$a", r.At ?? "");
                cmd.Parameters.AddWithValue("$d", r.Day);
                cmd.Parameters.AddWithValue("$m", r.Minutes);
                cmd.Parameters.AddWithValue("$e", r.Enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$lr", r.LastRun ?? "");
                cmd.Parameters.AddWithValue("$ls", r.LastStatus ?? "");
                cmd.Parameters.AddWithValue("$nx", r.NextRun ?? "");
                cmd.ExecuteNonQuery();
            }
        }
    }

    public static bool Delete(string name)
    {
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM schedules WHERE name = $n";
                cmd.Parameters.AddWithValue("$n", name);
                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }

    public static bool SetEnabled(string name, bool enabled)
    {
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE schedules SET enabled = $e WHERE name = $n";
                cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0);
                cmd.Parameters.AddWithValue("$n", name);
                return cmd.ExecuteNonQuery() > 0;
            }
        }
    }

    public static void UpdateRun(string name, string lastRun, string lastStatus, string nextRun)
    {
        lock (Lock)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "UPDATE schedules SET lastRun = $lr, lastStatus = $ls, nextRun = $nx WHERE name = $n";
                cmd.Parameters.AddWithValue("$lr", lastRun ?? "");
                cmd.Parameters.AddWithValue("$ls", lastStatus ?? "");
                cmd.Parameters.AddWithValue("$nx", nextRun ?? "");
                cmd.Parameters.AddWithValue("$n", name);
                cmd.ExecuteNonQuery();
            }
        }
    }

    /// <summary>Slot berikutnya SESUDAH <paramref name="from"/> (ISO
    /// lokal), atau "" bila tidak ada (once yang sudah lewat).</summary>
    public static string ComputeNext(string kind, string at, int day, int minutes, DateTime from)
    {
        try
        {
            switch (kind)
            {
                case "daily":
                {
                    if (!TryHm(at, out var hm)) return "";
                    var cand = from.Date.Add(hm);
                    if (cand <= from) cand = cand.AddDays(1);
                    return Iso(cand);
                }
                case "weekly":
                {
                    if (!TryHm(at, out var hm) || day < 0 || day > 6) return "";
                    for (int i = 0; i < 8; i++)
                    {
                        var d = from.Date.AddDays(i);
                        if ((int)d.DayOfWeek != day) continue;
                        var cand = d.Add(hm);
                        if (cand > from) return Iso(cand);
                    }
                    return "";
                }
                case "interval":
                {
                    if (minutes <= 0) return "";
                    return Iso(from.AddMinutes(minutes));
                }
                case "once":
                {
                    if (DateTime.TryParse(at, out var t) && t > from) return Iso(t);
                    return "";
                }
            }
        }
        catch { /* pola tak sah — tanpa slot */ }
        return "";
    }

    public static bool TryHm(string at, out TimeSpan hm)
    {
        hm = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(at)) return false;
        var p = at.Trim().Split(':');
        if (p.Length != 2) return false;
        if (!int.TryParse(p[0], out int h) || !int.TryParse(p[1], out int m)) return false;
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        hm = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>Ringkasan untuk /health: jumlah + slot terdekat.</summary>
    public static Dictionary<string, object> HealthInfo()
    {
        try
        {
            var rows = List();
            string nextRun = "", nextName = "";
            int enabled = 0;
            foreach (var r in rows)
            {
                if (!r.Enabled) continue;
                enabled++;
                if (r.NextRun.Length > 0 && (nextRun.Length == 0 || string.CompareOrdinal(r.NextRun, nextRun) < 0))
                {
                    nextRun = r.NextRun;
                    nextName = r.Name;
                }
            }
            return new Dictionary<string, object>
            {
                ["count"] = rows.Count,
                ["enabled"] = enabled,
                ["nextRun"] = nextRun,
                ["nextName"] = nextName,
            };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object>
            {
                ["count"] = 0, ["enabled"] = 0, ["nextRun"] = "", ["nextName"] = "",
                ["error"] = ex.Message,
            };
        }
    }

    /// <summary>Baris sebagai objek JSON untuk endpoint /schedules.</summary>
    public static Dictionary<string, object> RowJson(Row r) => new Dictionary<string, object>
    {
        ["name"] = r.Name,
        ["routine"] = r.Routine,
        ["kind"] = r.Kind,
        ["at"] = r.At,
        ["day"] = r.Day,
        ["minutes"] = r.Minutes,
        ["enabled"] = r.Enabled,
        ["lastRun"] = r.LastRun,
        ["lastStatus"] = r.LastStatus,
        ["nextRun"] = r.NextRun,
    };
}
