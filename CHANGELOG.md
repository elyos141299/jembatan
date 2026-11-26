# Changelog — MuseLink

Semua perubahan penting per versi. Catatan pengembang yang lebih
rinci (terutama versi ≤ 3.3.2) ada di README.md.

## 5.3.0 — 2026-11-26

Tata letak dibangun ulang PERSIS desain Figma "MuseLink —
Futuristic AI Control Center" (v5.2 baru mengganti warna/font;
user meminta komposisi halamannya juga sama). Sesuai perintah
user, tombol/elemen Figma yang fiturnya belum ada TETAP tampil —
fiturnya menyusul. Server & endpoint tidak berubah.

### Kerangka
- Sidebar 240: label MENU, 9 menu bernama Figma (Overview, Muse
  Console, Desktop Control, Live Screen, Activity Center,
  Automation Center, System Monitor, Integrations, Settings),
  kartu status dua baris (Bridge Connected • Tailscale /
  Muse Online • versi).
- Bar atas: judul + breadcrumb, badge Muse Connected (ungu) &
  Bridge Online (hijau), lonceng, chip pengguna Elyos —
  Administrator. Tombol Emergency Stop besar di bar atas hilang;
  fungsinya hidup di Quick Actions Overview, Console (Stop), dan
  Settings → Desktop Bridge.
- Panel konteks kanan 300px di semua layar (Current Activity,
  Active Application, Current Task, Recent Events, System
  Information) — diisi data asli bridge & sensor.
- Jendela bawaan 1440×900 (kanvas Figma).

### Halaman
- Overview: 4 kartu metrik Figma, Current AI Activity (progress
  + langkah + Lihat Detail/Hentikan), Quick Actions berfungsi
  (Screenshot menyimpan PNG, navigasi, Emergency Stop), Recent
  Activity linimasa, System Overview 5 bilah.
- Muse Console (dulu Command Center): bilah percakapan + chat
  ala Figma; kolom input tetap menjalankan PowerShell asli dan
  hasilnya tampil sebagai kartu Command Execution. Daftar
  percakapan masih contoh desain.
- Desktop Control: kartu Mouse/Keyboard/Application/Desktop
  Information + Active Window ala Figma; Fokus/Minimize/Tutup
  berfungsi. Tiga tombol klik mouse masih contoh desain.
- Live Screen (BARU): pratinjau desktop asli per 2 dtk, chip
  resolusi/refresh rate (DEVMODE)/capture/latency/aplikasi
  aktif, Ambil Screenshot menyimpan PNG.
- Activity Center: gabungan Activity + System Logs — cari +
  chip filter, linimasa gabungan operasi bridge & EventLog,
  detail kejadian, Salin Detail & Ekspor Log berfungsi.
- Automation Center: kartu workflow dari rutinitas tersimpan
  asli (editor menampilkan langkah dari JSON; Hapus berfungsi);
  contoh Figma berbadge "Contoh" bila kosong. Tombol putar/
  jeda/stop, + Workflow, Simpan, Uji Jalan masih contoh desain.
- System Monitor (BARU): 5 kartu metrik, grafik CPU/RAM 60
  cuplikan, daftar proses teratas dengan %CPU per proses.
- Integrations (dulu Connections): 6 kartu aplikasi; status
  Terhubung diuji dari proses berjalan/gh di PATH; Test
  Connection & Buka berfungsi; Configure contoh desain.
- Settings: daftar 9 kategori Figma; General berisi 3 saklar
  asli (Jalan otomatis, Cegah tidur, Mode Administrator) +
  saklar Notifikasi, bahasa, dan tema contoh desain; isi
  Permissions & Connections dilipat ke Desktop Bridge
  (Putuskan/Sambungkan + Emergency Stop berfungsi); About asli;
  kategori lain panel judul (isi menyusul).

### Dihapus
- Halaman CommandPage, ConnectionsPage, LogsPage, SecurityPage
  (isinya terserap ke Console, Integrations, Activity Center,
  dan Settings → Desktop Bridge).

## 5.2.0 — 2026-11-26

Tema "Futuristic AI Control Center" — penerapan desain Figma
yang disetujui user (jalur palet penuh). Murni tampilan: server,
endpoint, dan perilaku aplikasi tidak berubah.

### Diubah
- **Palet warna diganti total** dari color styles Figma: latar
  #0B0E13, surface #121821, elevated #1A2330, border #1E2A3D,
  aksen cyan #36D9F5 + violet #9274FF, sukses #38D996,
  peringatan #F5B94C, bahaya #F06475, teks #F0F5FA / #8998A9.
  Nama token lama dipertahankan (CrSage = violet, CrGold =
  amber) agar tidak ada halaman yang perlu ditulis ulang.
- **Font aplikasi kini Inter** (tertanam, lisensi OFL) dengan
  cadangan Segoe UI.
- Tombol kini berisian elevated dengan garis aksen cyan saat
  hover; item navigasi aktif berlatar elevated; kotak centang
  terisi cyan dengan centang gelap saat aktif; sorotan item
  dropdown memakai elevated. Perbaikan v5.1 (dropdown gelap,
  kotak centang gelap) dipertahankan.
- Kartu: radius 12, padding 16.

### Ditambahkan
- Gaya `CrPrimaryButton` (isian cyan, teks gelap) + pabrik
  `UiKit.PrimaryBtn`; dipakai tombol "Jalankan" di Command
  Center.

## 3.11.0 — 2026-11-26

"Satpam Input" — Desktop State Awareness tahap 4, terakhir
(audit DSA-15/16/17/18).

### Ditambahkan
- **InputArbiter**: /batch kini memegang lease input eksklusif
  selama rangkaiannya berjalan (pemilik = taskId, atau id
  tergenerasi). Batch lain dan endpoint input langsung dari
  tugas lain ditolak **429 `input_leased`** (retryable) —
  sub-aksi pemilik lease lolos lewat taskId warisan. Lease
  ber-TTL 180 detik yang diperbarui tiap aksi: pemegang yang
  mati/hang melepasnya otomatis. /health memuat `inputLease`.

### Diubah
- **/type mengetik per potongan ±20 karakter** (satu operasi UI
  per potongan) — operasi lain bisa menyela di antara potongan
  teks panjang, tidak lagi menunggu seluruh teks selesai.
- **/stream dibatasi 2 klien serentak**; klien berikutnya
  menerima 429 `stream_busy`.
- **UiaScan._last kini di bawah satu kunci statis** dan `Last`
  mengembalikan salinan — tidak ada lagi bacaan daftar hidup
  dari thread lain di tengah pindai.

## 3.10.0 — 2026-11-26

"Dokter & Rem" — Desktop State Awareness tahap 3 (audit
DSA-11/12/13, sebagian DSA-14).

### Ditambahkan
- **Taksonomi error berkode**: semua respons gagal (status ≥400)
  kini dilengkapi `code` (bad_request/not_found/conflict/busy/
  internal/ui_timeout) dan `retryable` — string `error` lama
  tidak berubah, pemanggil memutuskan ulang-tidak dari data.
- **GET /health**: diagnosis satu panggilan — versi, uptime,
  probe responsivitas thread UI (tidak lewat OnUi, justru untuk
  mendeteksi UI macet), operasi UI berjalan, timeout UI
  terakhir, shell hidup, seq event terakhir, jendela depan.
- **POST /op/cancel {id}**: membatalkan operasi UI yang masih
  mengantre (termasuk "hantu" timeout yang belum giliran);
  pemanggilnya menerima 409 `op_cancelled` (retryable — aksi
  tidak pernah jalan). Yang sudah berjalan dijawab jujur 409
  `op_running`; yang sudah selesai 409 `op_finished`.
- **Watchdog sadar responsivitas**: naskah penjaga kini
  memprobe /ping — proses yang ada tapi tidak menjawab 2×
  berurutan dibunuh dan dinyalakan ulang (dulu proses
  menggantung lolos karena yang dicek hanya keberadaannya).

### Diubah (perilaku, disengaja)
- **Timeout /exec kini ok:false** dengan `code:"exec_timeout"`,
  `retryable:false`, keluaran parsial tetap dikembalikan —
  semantik disamakan dengan /shell (`shell_timeout`, kini juga
  membawa exitCode:-1). Dulu /exec menjawab ok:true saat
  timeout sedangkan /shell ok:false.

## 3.9.0 — 2026-11-25

"Telinga & Ingatan" — Desktop State Awareness tahap 2 (audit
DSA-02/06/07/09/10).

### Ditambahkan
- **Event fokus & judul sungguhan (WinEventHook)**: EventLog kini
  memasang SetWinEventHook di thread UI — perubahan jendela depan
  tercatat sebagai kind `focus` (dengan hwnd & pid, debounce
  400ms) dan perubahan judul jendela tingkat atas sebagai
  `window-title` ("lama → baru"). Polling 2 detik tetap jalan
  untuk proses/jendela/idle seperti sebelumnya.
- **Long-poll /events**: `?wait=<ms>` (maks 30000) menahan respons
  sampai ada kejadian baru — Muse tidak perlu lagi polling.
- **Bendera `truncated`** di /events: menyala bila `since`
  tertinggal lebih jauh dari isi cincin 200 — kehilangan
  kejadian tidak lagi senyap.
- **TaskJournal**: POST apa pun boleh membawa `"taskId"` —
  operasi tercatat dengan korelasi tugas, hwnd jendela depan
  saat diterima, dan ringkasan hasil (pesan error). Sub-aksi
  /batch mewarisi taskId induknya. `GET /task?id=` membaca satu
  rangkaian tugas; operasi bertugas juga ditulis ke jurnal
  persisten `task-journal.jsonl` (rotasi 2MB) sehingga tetap
  bisa dibaca sesudah aplikasi restart. /lastops, /op, /ops
  kini menampilkan taskId.

## 3.8.0 — 2026-11-25

"Mata Jendela" — Desktop State Awareness tahap 1 (audit DSA
DSA-01/03/04/05/08).

### Ditambahkan
- **Skema jendela tunggal** untuk /state, /windows, /context
  (builder WindowJson bersama): setiap jendela kini membawa
  `hwnd` (identitas stabil), `pid`, `foreground`, `hung`,
  `state` ("normal" | "minimized" | "maximized"), `dialog`
  (kelas #32770 atau gaya WS_EX_DLGMODALFRAME), dan `ownerHwnd`
  (dialog menunjuk jendela induknya). /windows dan /context naik
  kelas — sebelumnya hanya /state yang punya pid & hung.
- **Dialog tanpa judul kini terlihat**: jendela dialog visible
  tanpa judul tetap masuk daftar (bertanda `dialog:true`);
  jendela biasa tanpa judul tetap disaring seperti sebelumnya.
- **Penargetan hwnd**: POST /focus dan POST /window menerima
  field opsional `hwnd` — identitas stabil menggantikan tebak
  substring judul untuk jendela berjudul kembar; judul tetap
  menjadi cadangan dan perilaku lama tidak berubah. Respons
  keduanya kini menyertakan `hwnd` jendela yang kena.

### Diubah
- **/state lebih ringan**: potret desktop kini diambil dalam
  SATU panggilan thread UI gabungan (sebelumnya 1 + 1 + N —
  IsHung dihitung per jendela lewat panggilan terpisah).

## 3.7.1 — 2026-11-25

Perbaikan dari uji penerimaan 3.7.0.

### Diperbaiki
- **/locate/act jalur OCR selalu 409**: validasi ulang membaca
  crop mungil seukuran kata (±8px) dan OCR WinRT mengembalikan
  kosong pada bitmap sekecil itu (terbukti: crop 79×26 → 0 kata;
  wilayah lega terbaca normal), jadi target yang sebenarnya
  masih ada dinyatakan "berubah". Kini wilayah validasi
  diperlebar (±200px horizontal, ±60px vertikal, dijepit ke
  layar), kata dicari di hasil baca untuk koordinat segar,
  dengan jalan teks-utuh + kabur-digit sebagai cadangan.

## 3.7.0 — 2026-11-25

Menutup sisa misi implementasi atas AUDIT-2026-11-25.md.

### Ditambahkan
- **Sesi grounding**: `/locate` menyimpan hasil sebagai sesi
  (ID `loc-N`, TTL 60 detik, maks 8 sesi) — kandidat memiliki
  `cid` stabil dan sesi mengingat jendela asal (HWND/PID/judul);
  respons memuat `session` dan `sessionTtlSec`.
- **POST /locate/act** `{session, cid|index}`: klik kandidat sesi
  sesudah validasi ulang (jendela depan sama, proses hidup,
  target masih di tempatnya — pindai UIA segar / OCR ulang
  wilayah). Kedaluwarsa/berubah → 409; tidak ditemukan → 404.
  Hasil klik diverifikasi (`verified.screenChanged`).
- **GET /op?id=** dan **GET /ops**: status operasi UI —
  `running|done|error|timeout|done-late`.

### Diubah
- **BUG-10 (audit)**: `OnUi` tidak lagi `Control.Invoke` tanpa
  batas. Setiap operasi UI ditunggu maksimal `OnUiTimeoutMs`
  (registry `HKCU\Software\Jembatan`, DWORD ms, bawaan 10.000,
  sah 250–120.000). Timeout → **504** `{error:"ui-timeout",
  opId, timeoutMs}`; operasi tidak dibatalkan dan tidak diulang
  otomatis — hasil akhirnya tercatat dan bisa dicek lewat /op.
- **/waitfor**: parameter opt-in `fuzzy:true` (kind=text, query
  berdigit) mencoba cocok kabur OCR sesudah exact gagal;
  respons memuat `fuzzy` dan `matchedVia`. Exact tetap bawaan.
- **Label versi UI** (tile panel, Pengaturan, log mulai) kini
  dari FileVersion rakitan — versi punya satu sumber (csproj)
  untuk server dan UI.

### Dokumentasi
- README: bagian v3.7.0, baris endpoint v3.5.0–v3.7.0, bagian
  Visual Grounding, katalog kode status & galat.
- CHANGELOG.md baru (berkas ini).

## 3.6.0 — 2026-11-25

"Grounding + Perbaikan Audit" — gelombang perbaikan dari
AUDIT-2026-11-25.md (tanpa hardening; keputusan pemilik).

### Ditambahkan
- **POST /locate**: fondasi Visual Grounding — UIA pindai segar,
  gugur ke OCR layar penuh, kandidat terstruktur, `click:true`,
  `marks:true` (gambar Set-of-Marks), lolos kabur OCR khusus
  query berdigit.
- **POST /keys/release** + pencatatan tombol tertahan di server;
  tombol lepas otomatis saat server berhenti.

### Diperbaiki (nomor audit)
- BUG-01 `/readvalue`: `hasValue` jujur, tidak lagi mengembalikan
  nama elemen sebagai nilai.
- BUG-02 `/uia/click`: menolak pindai basi (jendela depan
  berganti / umur pindai > 30 dtk) dengan 409.
- BUG-03 `/open`: folder kerja bawaan = folder exe + parameter
  `args` dan `cwd` (OBS tidak lagi gagal locale).
- BUG-04: `AbandonedMutexException` tertangkap saat perebutan
  mutex instans tunggal.
- BUG-05 `/key`: nama tombol asing ditolak 400 (tidak lagi
  diketik literal).
- BUG-17: wilayah tangkap sepenuhnya di luar layar → galat
  jujur, tidak lagi senyap diganti layar utama.
- BUG-18: SoftwareBitmap OCR di-Dispose.
- BUG-19: `UiaScan.ScanForeground` mengembalikan salinan.
- BUG-22: satu `UIA3Automation` bersama per thread (ThreadLocal).
- Thread penghangat relay berhenti saat server Stop.
- Versi endpoint (`/ping`, `/sysinfo`, `/context`, `/diag`)
  dibaca dari FileVersion rakitan.

## 3.5.0 — 2026-11-25

"Gelombang 1: Mata & Tangan" (dari peta upgrade prioritas user).

### Ditambahkan
- `GET /frame?fmt=png`: bingkai PNG piksel-sempurna.
- Profil UIA penuh di `/uia` & `/inspect`: value, state,
  patterns, automationId, className.
- **POST /uia/act** (invoke/toggle/select/expand/collapse),
  **POST /setvalue**, **POST /readvalue**.
- **POST /waitfor**: tunggu cerdas sisi server (text/uia/window/
  stable/process).
- **GET /state** (snapshot desktop + deteksi jendela macet) dan
  **GET /diag** (telemetri latensi per endpoint).
- `GET /window` rect: posisi/ukuran jendela + keadaan
  minimized/maximized/restored; `/click` menerima `expectWindow`.
- Panel AKTIVITAS auto-follow entri terbaru.

## 3.4.1 — 2026-11-25

Panel disamakan ke mockup Figma yang disetujui user: kolom kiri
420px, kartu STATUS 7 baris (RAM/CPU/GPU tetap di atas), kartu
AKSES ringkas + tombol "Buka Pengaturan", pil "Terhubung — relay
aman".

## Versi sebelumnya

Lihat README.md (catatan pengembang per versi sampai 2.3.x).
