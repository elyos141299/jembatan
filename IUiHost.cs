using System;

namespace Jembatan;

/// <summary>
/// Host UI untuk engine (v5.0.0 — bangun ulang WPF). AgentServer &
/// EventLog tidak lagi mengenal kelas jendela konkret: mereka hanya
/// butuh penyaluran aksi ke utas UI + dua permintaan balik (tema &
/// jangan-tidur) yang dulu dicoreng lewat coretan "is MainForm".
/// Implementasi WPF menyalurkan lewat Dispatcher.
/// </summary>
public interface IUiHost
{
    /// <summary>True bila pemanggil BUKAN utas UI.</summary>
    bool InvokeRequired { get; }

    /// <summary>Antrekan aksi di utas UI (tidak menunggu).</summary>
    void BeginInvoke(Action action);

    /// <summary>Permintaan ganti tema dari bridge (/theme).</summary>
    void SetTheme(string choice);

    /// <summary>Permintaan saklar jangan-tidur dari bridge (/keepawake).</summary>
    void ApplyKeepAwake(bool on);
}

/// <summary>
/// Keadaan tema yang benar-benar dirender UI saat ini. Control Room
/// v5 gelap secara rancangan (brief: tema Dark), jadi nilai ini true;
/// dulu dibaca dari AppTheme.IsDark (WinForms, sudah dihapus).
/// </summary>
public static class UiThemeState
{
    public static volatile bool IsDark = true;
}
