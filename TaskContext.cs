using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jembatan;

/// <summary>
/// Task Memory &amp; Context (v5.12.0 — butir 5 rencana "Tangan
/// Stabil &amp; Ingat", dari daftar prioritas user #4): ingatan
/// kerja bersama antara agent dan aplikasi tentang TUGAS yang
/// sedang berjalan — judul + catatan langkah — tersimpan sebagai
/// JSON di %LOCALAPPDATA%\Jembatan\task-context.json sehingga
/// tahan restart aplikasi maupun listener.
///
/// Sifatnya PASIF: tidak mengubah perilaku tangan lain. Agent
/// membuka tugas dengan /task/start, menambah catatan dengan
/// /task/note, menutup dengan /task/done; GET /context ikut
/// menyertakan tugas aktif supaya sesi baru (atau sesudah
/// restart) langsung tahu pekerjaan apa yang sedang terbuka.
/// </summary>
internal static class TaskContext
{
    private static readonly object _lock = new();
    private static State _state;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "task-context.json");

    private sealed class State
    {
        public bool Active { get; set; }
        public string Title { get; set; } = "";
        public List<string> Notes { get; set; } = new();
        public string StartedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string LastDoneTitle { get; set; } = "";
        public string LastDoneAt { get; set; } = "";
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static State Load()
    {
        if (_state != null) return _state;
        try
        {
            if (File.Exists(FilePath))
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath));
        }
        catch { /* berkas rusak -> mulai bersih */ }
        return _state ??= new State();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Load(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ingatan gagal disimpan tidak boleh menjatuhkan aplikasi */ }
    }

    public static void Start(string title)
    {
        lock (_lock)
        {
            var s = Load();
            s.Active = true;
            s.Title = string.IsNullOrWhiteSpace(title) ? "tanpa judul" : title.Trim();
            s.Notes = new List<string>();
            s.StartedAt = Now();
            s.UpdatedAt = s.StartedAt;
            Save();
        }
    }

    public static void Note(string text)
    {
        lock (_lock)
        {
            var s = Load();
            if (!s.Active)
            {
                // Ramah: catatan tanpa tugas aktif membuka tugas
                // tanpa judul daripada hilang.
                s.Active = true;
                s.Title = "tanpa judul";
                s.StartedAt = Now();
            }
            if (!string.IsNullOrWhiteSpace(text))
                s.Notes.Add(Now() + "  " + text.Trim());
            if (s.Notes.Count > 200) s.Notes.RemoveAt(0);
            s.UpdatedAt = Now();
            Save();
        }
    }

    public static void Done()
    {
        lock (_lock)
        {
            var s = Load();
            if (s.Active)
            {
                s.LastDoneTitle = s.Title;
                s.LastDoneAt = Now();
            }
            s.Active = false;
            s.Title = "";
            s.Notes = new List<string>();
            s.UpdatedAt = Now();
            Save();
        }
    }

    /// <summary>Potret untuk respons JSON; null bila belum pernah ada tugas.</summary>
    public static Dictionary<string, object> Snapshot()
    {
        lock (_lock)
        {
            var s = Load();
            return new Dictionary<string, object>
            {
                ["active"] = s.Active,
                ["title"] = s.Title,
                ["notes"] = new List<string>(s.Notes),
                ["startedAt"] = s.StartedAt,
                ["updatedAt"] = s.UpdatedAt,
                ["lastDoneTitle"] = s.LastDoneTitle,
                ["lastDoneAt"] = s.LastDoneAt,
            };
        }
    }
}
