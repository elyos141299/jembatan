# Jembatan v2.2 — catatan pengembang

Aplikasi Windows (.NET 8, WinForms) yang memberi Muse akses kerja ke PC
user lewat jaringan privat Tailscale user. Satu jendela = satu-satunya
saklar: jendela terbuka + status tersambung → Muse bisa bekerja;
jendela ditutup / tombol PUTUSKAN → akses berhenti total.

Panduan untuk user ada di `../README-INSTAL.md`.

## Struktur

| Berkas | Isi |
|---|---|
| `Program.cs` | Titik masuk aplikasi (STAThread). |
| `MainForm.cs` | Jendela utama: status, log aktivitas real-time, tombol PUTUSKAN/Sambung lagi, checkbox jalan otomatis, dan panel chat Muse (WebView2, sejak v2.2). |
| `AgentServer.cs` | Layanan agen HTTP (port 8765) + semua endpoint. |
| `NativeInput.cs` | Mouse/keyboard: user32 `SetCursorPos`/`mouse_event` + `SendKeys`. |
| `app.manifest` | Manifest aplikasi: DPI-aware PerMonitorV2 (rujukan dari csproj). |
| `installer/jembatan.iss` | Inno Setup 6 → `Jembatan-Setup.exe` (per-user, tanpa admin). |
| `.github/workflows/build-windows.yml` | CI: publish self-contained single-file + bangun installer, artifact `jembatan-setup`. |

## Endpoint (JSON, port 8765)

| Metode & path | Body | Hasil |
|---|---|---|
| `GET /ping` | — | `{ok:true, app:"jembatan", version:"2.2"}` |
| `GET /screenshot` | — | PNG layar utama (binary `image/png`) |
| `GET /sysinfo` | — | `{ok, version:"2.2", pc, windows, screen:"1920x1080", uptimeSec, jembatanPort:8765}` |
| `GET /clipboard` | — | `{ok:true, text}` — teks clipboard saat ini ("" bila kosong/bukan teks) |
| `GET /windows` | — | `{ok:true, windows:[{title, process}]}` — jendela tingkat atas yang terlihat & berjudul |
| `GET /file?path=<urlencoded>` | — | Binary mentah berkas (`application/octet-stream`, `Content-Disposition: attachment`); 404 JSON bila berkas tidak ada |
| `GET /stream?w=&q=&fps=` | — | MJPEG `multipart/x-mixed-replace; boundary=frame` — lebar maks `w` (bawaan 1280, 320–1920), kualitas JPEG `q` (bawaan 60, 30–90), target `fps` (bawaan 10, 1–15); frame yang tidak berubah tidak dikirim (keepalive ±1 detik) |
| `POST /move` | `{x, y}` | `{ok:true}` |
| `POST /click` | `{x, y, button}` (`left`/`right`/`middle`) | `{ok:true}` |
| `POST /scroll` | `{x, y, delta}` | `{ok:true}` — delta positif = scroll ke atas (120 per "klik" roda) |
| `POST /drag` | `{x1, y1, x2, y2, button?}` | `{ok:true}` — seret bertahap dari titik awal ke titik akhir |
| `POST /type` | `{text}` | `{ok:true}` — teks diketik; karakter khusus SendKeys di-escape |
| `POST /key` | `{keys}` | `{ok:true}` — nama tombol (`ENTER`, `F5`, …) atau sintaks SendKeys (`^c`, `%{F4}`) |
| `POST /clipboard` | `{text}` | `{ok:true}` — isi clipboard diganti teks ini (log hanya mencatat panjangnya) |
| `POST /paste` | `{text}` | `{ok:true}` — clipboard diisi teks lalu Ctrl+V ditekan: cara tercepat memasukkan teks panjang |
| `POST /focus` | `{title}` | `{ok:true, focused}` — bawa jendela yang judulnya mengandung `title` (tidak peka huruf besar/kecil) ke depan; 404 bila tidak ada |
| `POST /shell` | `{command, timeoutMs?}` | `{ok, exitCode, stdout}` — terminal PERSISTEN (satu `powershell.exe` berumur panjang): folder kerja & variabel bertahan antar panggilan; timeout (bawaan 60 dtk, maks 300 dtk) → `{ok:false, timeout:true, restarted:true, stdout}` dan shell dibuat ulang |
| `POST /shell/restart` | — | `{ok:true}` — bunuh + buat ulang shell persisten |
| `POST /exec` | `{command, args, timeoutMs?}` | `{ok, exitCode, timeout, stdout, stderr}` — timeout default 60 dtk (maks 300 dtk via `timeoutMs`), keluaran dipotong 100.000 karakter |
| `POST /open` | `{path}` | `{ok:true}` — `Process.Start` dengan shell |
| `POST /upload?path=<urlencoded>` | badan mentah (bukan JSON) | `{ok:true, bytes}` — badan ditulis jadi berkas di `path`; folder induk dibuat bila belum ada |

Semua permintaan dicatat ke log jendela (jam + ringkasan). Khusus
`/stream`: hanya dua baris log — "stream dimulai" dan "stream berhenti",
bukan per frame.

## Perubahan v2.2 (dari v2.1)

- **Panel chat Muse di dalam jendela**: tombol "💬 Chat Muse" di
  kepala jendela membuka panel kanan (SplitContainer; panel kiri
  ±380px berisi tampilan lama persis, panel kanan = chat). Jendela
  melebar ke minimal 1280×800 saat chat dibuka dan kembali ke ukuran
  ringkas saat ditutup. Isi panel adalah **situs muse.ai yang ditanam
  lewat WebView2** — website yang sama seperti di browser, bukan
  salinan; **user login dengan akunnya sendiri di panel itu** dan
  aplikasi tidak pernah menyentuh kredensial.
- WebView2 dibuat **malas** (pertama kali chat dibuka), dengan folder
  data persisten `%LOCALAPPDATA%\Jembatan\WebView2` (cookie/login awet
  antar jalan), UA Chrome desktop (agar penyedia login tidak menolak
  browser tertanam), dan `NewWindowRequested` diarahkan ke panel yang
  sama agar popup/redirect login tidak keluar jendela. Bilah alat
  kecil di atas panel: "↻ Muat Ulang" dan "🌐 Buka di Chrome".
- Bila runtime WebView2 tidak ada/gagal, panel hanya menampilkan
  pesan galat (dan mencatatnya di log) — **semua fitur jembatan tetap
  jalan penuh**. Menutup panel tidak membuang WebView (dibuat sekali),
  jadi membuka lagi cepat dan sesi tidak hilang.
- Dependensi baru satu-satunya: `Microsoft.Web.WebView2` 1.0.2903.40,
  plus `IncludeNativeLibrariesForSelfExtract` di csproj agar loader
  native-nya ikut ter-extract dari publish single-file. Runtime
  WebView2 (Evergreen) mengandalkan yang sudah terpasang di Windows
  (bawaan Edge/Windows 10-11).
- Endpoint, port, PUTUSKAN, jalan otomatis, dan shell persisten
  **tidak berubah sama sekali**.

## Perubahan v2.1 (dari v2.0)

- **Stream jauh lebih cepat**: tangkap + encode JPEG pindah dari
  thread UI ke thread pekerja stream sendiri (GDI `CopyFromScreen`
  tidak butuh thread UI) — di v2.0 jalur lama hanya mencapai
  ±1–2 fps di dunia nyata. Frame yang pikselnya identik dengan frame
  sebelumnya tidak dikirim (hash MD5), kecuali keepalive ±1 detik.
  Parameter baru: `?w=` (lebar maks), `?q=` (kualitas JPEG),
  `?fps=` (target frame per detik; jeda tidur memperhitungkan waktu
  tangkap+encode).
- **Clipboard dua arah**: `GET /clipboard` membaca teks clipboard,
  `POST /clipboard` mengisinya (isi tidak pernah masuk log, hanya
  panjangnya), dan `POST /paste` = isi clipboard + Ctrl+V untuk
  memasukkan teks panjang seketika tanpa mengetik per karakter.
- **Daftar & fokus jendela**: `GET /windows` (EnumWindows: judul +
  nama proses) dan `POST /focus` (substring judul, jendela
  diminimalkan dipulihkan dulu) — Muse bisa pindah jendela secara
  eksplisit, tidak lagi menebak dari screenshot.
- **`GET /sysinfo`**: nama PC, versi Windows, resolusi layar,
  uptime, dan port — untuk cek cepat kondisi PC.
- **Terminal persisten**: `POST /shell` menjalankan perintah di satu
  proses `powershell.exe -NoProfile -NoExit` yang hidup terus
  (dibuat malas, dibuat ulang bila mati/timeout), dengan penanda
  sentinel berisi `$LASTEXITCODE` untuk tahu kapan perintah selesai
  dan berapa kode keluarnya. Panggilan diserialkan dengan kunci.
  `/exec` satu-tembak tetap ada, tidak berubah.
- **Jalan otomatis**: checkbox "Jalan otomatis saat Windows menyala"
  di jendela utama, ditopang nilai `Jembatan` di
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Di jalan
  pertama (nilai belum ada) otomatis AKTIF; sesudahnya checkbox
  mengikuti isi registry, dan mengubahnya langsung menulis registry.

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
- Versi tampil di tiga tempat yang selaras: `/ping` (`"2.1"`),
  `installer/jembatan.iss` (`AppVersion=2.1`), dan label kecil `v2.1`
  di jendela utama.

## Keputusan teknis penting

- **TcpListener, bukan HttpListener.** HttpListener dengan prefix
  `http://+:8765/` butuh reservasi URL (netsh urlacl) yang hanya bisa
  dibuat admin, sedangkan installer ini per-user tanpa admin.
  TcpListener cukup meminta izin Windows Firewall satu kali.
- **Aksi layar (screenshot/mouse/keyboard/clipboard) dijalankan di
  thread UI** lewat `Control.Invoke` — SendKeys & Clipboard butuh
  message loop/STA, dan ini juga membuat urutan aksi selalu rapi.
  Pengecualian sejak v2.1: tangkap+encode frame `/stream` berjalan di
  thread pekerja stream sendiri karena GDI tidak membutuhkannya dan
  jalur thread UI terbukti lambat (±1–2 fps).
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
