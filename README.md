# Jembatan v1 — catatan pengembang

Aplikasi Windows (.NET 8, WinForms) yang memberi Muse akses kerja ke PC
user lewat jaringan privat Tailscale user. Satu jendela = satu-satunya
saklar: jendela terbuka + status tersambung → Muse bisa bekerja;
jendela ditutup / tombol PUTUSKAN → akses berhenti total.

Panduan untuk user ada di `../README-INSTAL.md`.

## Struktur

| Berkas | Isi |
|---|---|
| `Program.cs` | Titik masuk aplikasi (STAThread). |
| `MainForm.cs` | Jendela utama: status, log aktivitas real-time, tombol PUTUSKAN/Sambung lagi. |
| `AgentServer.cs` | Layanan agen HTTP (port 8765) + semua endpoint. |
| `NativeInput.cs` | Mouse/keyboard: user32 `SetCursorPos`/`mouse_event` + `SendKeys`. |
| `installer/jembatan.iss` | Inno Setup 6 → `Jembatan-Setup.exe` (per-user, tanpa admin). |
| `.github/workflows/build-windows.yml` | CI: publish self-contained single-file + bangun installer, artifact `jembatan-setup`. |

## Endpoint (JSON, port 8765)

| Metode & path | Body | Hasil |
|---|---|---|
| `GET /ping` | — | `{ok:true, app:"jembatan", version:"1.0"}` |
| `GET /screenshot` | — | PNG layar utama (binary `image/png`) |
| `POST /move` | `{x, y}` | `{ok:true}` |
| `POST /click` | `{x, y, button}` (`left`/`right`/`middle`) | `{ok:true}` |
| `POST /type` | `{text}` | `{ok:true}` — teks diketik; karakter khusus SendKeys di-escape |
| `POST /key` | `{keys}` | `{ok:true}` — nama tombol (`ENTER`, `F5`, …) atau sintaks SendKeys (`^c`, `%{F4}`) |
| `POST /exec` | `{command, args}` | `{ok, exitCode, timeout, stdout, stderr}` — timeout 60 dtk, keluaran dipotong 8.000 karakter |
| `POST /open` | `{path}` | `{ok:true}` — `Process.Start` dengan shell |

Semua permintaan dicatat ke log jendela (jam + ringkasan).

## Keputusan teknis penting

- **TcpListener, bukan HttpListener.** HttpListener dengan prefix
  `http://+:8765/` butuh reservasi URL (netsh urlacl) yang hanya bisa
  dibuat admin, sedangkan installer ini per-user tanpa admin.
  TcpListener cukup meminta izin Windows Firewall satu kali.
- **Aksi layar (screenshot/mouse/keyboard) dijalankan di thread UI**
  lewat `Control.Invoke` — SendKeys butuh message loop, dan ini juga
  membuat urutan aksi selalu rapi.
- **Keamanan v1 bertumpu pada Tailscale** (sesuai desain yang disetujui):
  tidak ada password/token di aplikasi. Hanya perangkat yang disetujui
  user di admin Tailscale-nya yang bisa mencapai port 8765.
  Jangan pernah mengekspos port ini ke internet publik.

## Build

- Lokal (butuh .NET 8 SDK di Windows): `dotnet publish Jembatan.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish`, lalu jalankan ISCC atas `installer/jembatan.iss`.
- CI: jalankan workflow **Build Windows** (workflow_dispatch) → artifact `jembatan-setup`.
