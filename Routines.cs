using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Jembatan;

/// <summary>
/// Rutinitas tersimpan (v2.7.0): rangkaian aksi bernama yang
/// disimpan DI PC (berkas routines.json di folder data aplikasi)
/// dan bisa dijalankan dengan SATU panggilan /routines/run.
/// Gunanya: rangkaian yang sering diulang tidak lagi dikirim
/// langkah demi langkah menyeberangi relay — PC menyimpannya
/// dan menjalankannya lokal lewat mesin /batch yang sama.
///
/// Isi tiap rutinitas = array aksi persis format /batch
/// ({"do":"click",...}). Validasi nama & isi dilakukan di
/// AgentServer sebelum sampai ke sini; kelas ini murni
/// penyimpanan (muat malas, simpan seketika, tahan berkas rusak).
/// </summary>
public static class Routines
{
    private static readonly object _lock = new object();
    private static Dictionary<string, string> _map; // nama → JSON array aksi (teks mentah)

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jembatan", "routines.json");

    private static void EnsureLoaded()
    {
        if (_map != null) return;
        _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(FilePath)))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in doc.RootElement.EnumerateObject())
                        {
                            if (p.Value.ValueKind == JsonValueKind.Array)
                                _map[p.Name] = p.Value.GetRawText();
                        }
                    }
                }
            }
        }
        catch { /* berkas rusak → mulai dari kosong, jangan jatuhkan server */ }
    }

    private static void Persist()
    {
        try
        {
            string dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    foreach (var kv in _map)
                    {
                        w.WritePropertyName(kv.Key);
                        using (var d = JsonDocument.Parse(kv.Value))
                            d.RootElement.WriteTo(w);
                    }
                    w.WriteEndObject();
                }
                File.WriteAllBytes(FilePath, ms.ToArray());
            }
        }
        catch { /* gagal simpan tidak menjatuhkan server */ }
    }

    /// <summary>Daftar rutinitas: nama → jumlah langkah.</summary>
    public static List<KeyValuePair<string, int>> List()
    {
        lock (_lock)
        {
            EnsureLoaded();
            var hasil = new List<KeyValuePair<string, int>>();
            foreach (var kv in _map)
            {
                int n = 0;
                try
                {
                    using (var d = JsonDocument.Parse(kv.Value))
                        n = d.RootElement.GetArrayLength();
                }
                catch { /* abaikan */ }
                hasil.Add(new KeyValuePair<string, int>(kv.Key, n));
            }
            hasil.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            return hasil;
        }
    }

    public static bool TryGet(string name, out string actionsJson)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _map.TryGetValue(name ?? "", out actionsJson);
        }
    }

    public static void Save(string name, string actionsJson)
    {
        lock (_lock)
        {
            EnsureLoaded();
            _map[name] = actionsJson;
            Persist();
        }
    }

    public static bool Delete(string name)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_map.Remove(name ?? "")) return false;
            Persist();
            return true;
        }
    }
}
