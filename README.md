# Jembatan v2 — catatan pengembang

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
| `app.manifest` | Manifest aplikasi: DPI-aware PerMonitorV2 (rujukan dari csproj). |
| `installer/jembatan.iss` | Inno Setup 6 → `Jembatan-Setup.exe` (per-user, tanpa admin). |
| `.github/workflows/build-windows.yml` | CI: publish self-contained single-file + bangun installer, artifact `jembatan-setup`. |

## Endpoint (JSON, port 8765)

| Metode & path | Body | Hasil |
|---|---|---|
| `GET /ping` | — | `{ok:true, app:"jembatan", version:"2.0"}` |
| `GET /screenshot` | — | PNG layar utama (binary `image/png`) |
| `GET /file?path=<urlencoded>` | — | Binary mentah berkas (`application/octet-stream`, `Content-Disposition: attachment`); 404 JSON bila berkas tidak ada |
| `GET /stream` | — | MJPEG `multipart/x-mixed-replace; boundary=frame` — layar utama, lebar maks 1280, JPEG q60, ±150 ms/frame |
| `POST /move` | `{x, y}` | `{ok:true}` |
| `POST /click` | `{x, y, button}` (`left`/`right`/`middle`) | `{ok:true}` |
| `POST /scroll` | `{x, y, delta}` | `{ok:true}` — delta positif = scroll ke atas (120 per "klik" roda) |
| `POST /drag` | `{x1, y1, x2, y2, button?}` | `{ok:true}` — seret bertahap dari titik awal ke titik akhir |
| `POST /type` | `{text}` | `{ok:true}` — teks diketik; karakter khusus SendKeys di-escape |
| `POST /key` | `{keys}` | `{ok:true}` — nama tombol (`ENTER`, `F5`, …) atau sintaks SendKeys (`^c`, `%{F4}`) |
| `POST /exec` | `{command, args, timeoutMs?}` | `{ok, exitCode, timeout, stdout, stderr}` — timeout default 60 dtk (maks 300 dtk via `timeoutMs`), keluaran dipotong 100.000 karakter |
| `POST /open` | `{path}` | `{ok:true}` — `Process.Start` dengan shell |
| `POST /upload?path=<urlencoded>` | badan mentah (bukan JSON) | `{ok:true, bytes}` — badan ditulis jadi berkas di `path`; folder induk dibuat bila belum ada |

Semua permintaan dicatat ke log jendela (jam + ringkasan). Khusus
`/stream`: hanya dua baris log — "stream dimulai" dan "stream berhenti",
bukan per frame.

## Perubahan v2 (dari v1)

- **Transfer berkas dua arah**: `GET /file` (unduh, dialirkan per blok
  64 KB) dan `POST /upload` (unggah, badan mentah dialirkan langsung
  ke berkas). Menggantikan akrobat potong-potong base64 lewat `/exec`.
- **Layar live**: `GET /stream` (MJPEG) untuk memantau layar nyaris
  real-time; `/screenshot` tetap ada untuk jepretan tunggal PNG.
- **Mouse lebih lengkap**: `/scroll` (roda mouse) dan `/drag` (seret
  bertahap 16 langkah).
- **`/exec` lebih longgar**: potongan keluaran naik 8.000 → 100.000
  karakter; `timeoutMs` opsional sampai 300.000 ms (5 menit).
- **DPI-aware**: `app.manifest` menyatakan `dpiAwareness PerMonitorV2`
  (+ `Program.cs` memanggil `Application.SetHighDpiMode(PerMonitorV2)`),
  jadi koordinat screenshot & klik selalu piksel fisik — tidak lagi
  bergeser di PC dengan scaling 125%/150%.
- Versi tampil di tiga tempat yang selaras: `/ping` (`"2.0"`),
  `installer/jembatan.iss` (`AppVersion=2.0`), dan label kecil `v2.0`
  di jendela utama.

## Keputusan teknis penting

- **TcpListener, bukan HttpListener.** HttpListener dengan prefix
  `http://+:8765/` butuh reservasi URL (netsh urlacl) yang hanya bisa
  dibuat admin, sedangkan installer ini per-user tanpa admin.
  TcpListener cukup meminta izin Windows Firewall satu kali.
- **Aksi layar (screenshot/mouse/keyboard) dijalankan di thread UI**
  lewat `Control.Invoke` — SendKeys butuh message loop, dan ini juga
  membuat urutan aksi selalu rapi.
- **Endpoint streaming (`/file`, `/upload`, `/stream`) ditangani langsung
  di atas `NetworkStream`**, bukan lewat pola Request/Response JSON:
  unduh/unggah disalin per blok 64 KB (berkas besar tidak membebani
  memori), dan stream MJPEG berjalan sampai klien memutus atau listener
  dimatikan (PUTUSKAN) — terdeteksi lewat gagal tulis / flag `IsRunning`.
- **Keamanan bertumpu pada Tailscale** (sesuai desain yang disetujui):
  tidak ada password/token di aplikasi. Hanya perangkat yang disetujui
  user di admin Tailscale-nya yang bisa mencapai port 8765.
  Jangan pernah mengekspos port ini ke internet publik.

## Build

- Lokal (butuh .NET 8 SDK di Windows): `dotnet publish Jembatan.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish`, lalu jalankan ISCC atas `installer/jembatan.iss`.
- CI: jalankan workflow **Build Windows** (workflow_dispatch) → artifact `jembatan-setup`.
