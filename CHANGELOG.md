# Changelog — MuseLink

Semua perubahan penting per versi. Catatan pengembang yang lebih
rinci (terutama versi ≤ 3.3.2) ada di README.md.

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
