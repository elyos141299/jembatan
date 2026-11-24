# MuseLink v2.4.0 — catatan pengembang

Aplikasi Windows (.NET 8, WinForms) yang memberi Muse akses kerja ke PC
user lewat jaringan privat Tailscale user. Satu jendela = satu-satunya
saklar: jendela terbuka + status tersambung → Muse bisa bekerja;
jendela ditutup / tombol PUTUSKAN → akses berhenti total.

Sejak v2.3.3 nama tampil aplikasi adalah **MuseLink** (v2.3.2 sempat
"Muse Windows"); nama internalnya tetap **jembatan** — berkas
`Jembatan.exe`, folder pasang `{autopf}\Jembatan`, kunci registry
`HKCU\Software\Jembatan`, nama repo, dan field `app` di `/ping`
tidak berubah.

Panduan untuk user ada di `../README-INSTAL.md`.

## Struktur

| Berkas | Isi |
|---|---|
| `Program.cs` | Titik masuk aplikasi (STAThread): penjaga satu instans (mutex) + kenaikan hak sendiri bila Mode Administrator aktif. |
| `Settings.cs` | Pengaturan di `HKCU\Software\Jembatan` (Mode Administrator, bendera UserStopped untuk penjaga) + pembantu hak administrator (cek elevasi, relaunch `runas`). |
| `MainForm.cs` | Jendela utama: status, log aktivitas real-time, tombol PUTUSKAN/Sambung lagi, saklar jalan otomatis & Mode Administrator, penanda mode, panel chat Muse (WebView2, sejak v2.2), system tray (v2.3.1), tugas penjaga anti-crash + log persisten (v2.3.2), tema terang gaya iCloud (v2.3.3). |
| `UiControls.cs` | Kontrol tampilan gaya iCloud (v2.3.3): `RoundedPanel` (kartu sudut bundar), `ToggleSwitch` (saklar ala iOS), `RoundedButton` (tombol pil), `RoundDot` (titik status). Murni tampilan. |
| `AgentServer.cs` | Layanan agen HTTP (port 8765) + semua endpoint. |
| `NativeInput.cs` | Mouse/keyboard: user32 `SetCursorPos`/`mouse_event` + `SendKeys`. |
| `app.manifest` | Manifest aplikasi: DPI-aware PerMonitorV2 (rujukan dari csproj). |
| `installer/jembatan.iss` | Inno Setup 6 → `Jembatan-Setup.exe` (per-user, tanpa admin). |
| `.github/workflows/build-windows.yml` | CI: publish self-contained single-file + bangun installer, artifact `jembatan-setup`. |

## Endpoint (JSON, port 8765)

| Metode & path | Body | Hasil |
|---|---|---|
| `GET /ping` | — | `{ok:true, app:"jembatan", version:"2.3.5", admin}` — `admin` = hak proses saat ini |
| `GET /screenshot` | — | PNG layar utama (binary `image/png`); wilayah potong opsional `?x=&y=&w=&h=` (v2.4.0) |
| `GET /sysinfo` | — | `{ok, version:"2.3.5", admin, pc, windows, screen:"1920x1080", uptimeSec, jembatanPort:8765}` |
| `GET /clipboard` | — | `{ok:true, text}` — teks clipboard saat ini ("" bila kosong/bukan teks) |
| `GET /windows` | — | `{ok:true, windows:[{title, process}]}` — jendela tingkat atas yang terlihat & berjudul |
| `GET /file?path=<urlencoded>` | — | Binary mentah berkas (`application/octet-stream`, `Content-Disposition: attachment`); 404 JSON bila berkas tidak ada |
| `GET /stream?w=&q=&fps=` | — | MJPEG `multipart/x-mixed-replace; boundary=frame` — lebar maks `w` (bawaan 1280, 320–1920), kualitas JPEG `q` (bawaan 60, 30–90), target `fps` (bawaan 10, 1–15); frame yang tidak berubah tidak dikirim (keepalive ±1 detik) |
| `POST /move` | `{x, y}` | `{ok:true}` |
| `POST /click` | `{x, y, button, count?, modifiers?}` (`left`/`right`/`middle`; `count` 1–3, 2 = klik ganda; `modifiers` mis. `"ctrl,shift"` ditahan selama klik) | `{ok:true}` |
| `POST /scroll` | `{x, y, delta}` | `{ok:true}` — delta positif = scroll ke atas (120 per "klik" roda) |
| `POST /drag` | `{x1, y1, x2, y2, button?, modifiers?}` | `{ok:true}` — seret bertahap dari titik awal ke titik akhir; `modifiers` ditahan selama seret |
| `POST /type` | `{text}` | `{ok:true}` — teks diketik lewat SendInput Unicode (v2.4.0): karakter apa pun terkirim apa adanya, baris baru = Enter |
| `POST /key` | `{keys}` | `{ok:true}` — nama tombol (`ENTER`, `F5`, …) atau sintaks SendKeys (`^c`, `%{F4}`) |
| `POST /clipboard` | `{text}` | `{ok:true}` — isi clipboard diganti teks ini (log hanya mencatat panjangnya) |
| `POST /paste` | `{text}` | `{ok:true}` — clipboard diisi teks lalu Ctrl+V ditekan: cara tercepat memasukkan teks panjang |
| `POST /focus` | `{title}` | `{ok:true, focused}` — bawa jendela yang judulnya mengandung `title` (tidak peka huruf besar/kecil) ke depan; 404 bila tidak ada |
| `POST /shell` | `{command, timeoutMs?}` atau `{scriptB64, timeoutMs?}` (script PowerShell utuh ter-Base64 UTF-8, v2.4.0) | `{ok, exitCode, stdout}` — terminal PERSISTEN (satu `powershell.exe` berumur panjang): folder kerja & variabel bertahan antar panggilan; timeout (bawaan 60 dtk, maks 300 dtk) → `{ok:false, timeout:true, restarted:true, stdout}` dan shell dibuat ulang |
| `POST /shell/restart` | — | `{ok:true}` — bunuh + buat ulang shell persisten |
| `POST /exec` | `{command, args, timeoutMs?}` atau `{scriptB64, timeoutMs?}` | `{ok, exitCode, timeout, stdout, stderr}` — timeout default 60 dtk (maks 300 dtk via `timeoutMs`), keluaran dipotong 100.000 karakter; `scriptB64` = script PowerShell utuh ter-Base64 (UTF-8) dijalankan lewat `-EncodedCommand`, kebal kerusakan tanda kutip (v2.4.0) |
| `GET /uia?max=` | — | `{ok, window, count, elements:[{i, name, type, x, y, w, h, enabled}]}` — peta elemen interaktif jendela aktif lewat UI Automation (v2.4.0); `max` bawaan 120 (10–400) |
| `POST /uia/click` | `{index}` atau `{name}` (+ `button?`) | `{ok, clicked, type, x, y}` — klik titik tengah elemen hasil pindai `/uia` terakhir; 404 bila tidak ketemu |
| `POST /window` | `{title, action}` (`close`/`minimize`/`maximize`/`restore`) | `{ok, window, action}` — kendali jendela yang judulnya mengandung `title`; 404 bila tidak ada |
| `GET /lastops` | — | `{ok, ops:[{seq, time, method, path, status, ms}]}` — 50 operasi Route terakhir, terbaru dulu (gelombang 2): cek anti-ambiguitas setelah koneksi putus |
| `GET /frame?w=&q=` | — | JPEG bingkai layar terkompresi (binary `image/jpeg`) — lebar maks `w` (bawaan 1280, 320–1920), kualitas `q` (bawaan 70, 30–95); jalur lihat instan, lebih ringan dari `/screenshot` |
| `POST /keydown` / `POST /keyup` | `{key}` | `{ok:true}` — tahan/lepas satu tombol (`CTRL`, `SHIFT`, `ALT`, `WIN`, huruf/angka, `F1`–`F12`, arah, dst.) untuk kombinasi lintas perintah |
| `POST /wait` | `{windowTitle?, filePath?, timeoutMs?}` | `{ok, found, via, waitedMs}` — tunggu di sisi server (cek tiap 500ms) sampai jendela berjudul itu muncul atau berkas itu ada; timeout bawaan 30 dtk, maks 300 dtk |
| `POST /open` | `{path}` | `{ok:true}` — `Process.Start` dengan shell |
| `POST /upload?path=<urlencoded>` | badan mentah (bukan JSON) | `{ok:true, bytes}` — badan ditulis jadi berkas di `path`; folder induk dibuat bila belum ada |

Semua permintaan dicatat ke log jendela (jam + ringkasan). Khusus
`/stream`: hanya dua baris log — "stream dimulai" dan "stream berhenti",
bukan per frame.

## Perubahan v2.4.0 (dari v2.3.5) — "Operasi PC Maksimal"

Murni memperkuat jalur kendali Muse; tampilan aplikasi tidak
berubah sama sekali.

- **Peta elemen UI Automation**: `GET /uia` memindai jendela aktif
  dan mengembalikan elemen interaktifnya (nama, tipe, kotak batas);
  `POST /uia/click` mengklik elemen berdasarkan indeks/nama —
  menggantikan tebak koordinat dari screenshot.
- **Klik ganda/tiga**: `POST /click` menerima `count` 1–3.
- **Ketik Unicode**: `POST /type` kini memakai SendInput
  KEYEVENTF_UNICODE — stabil di semua aplikasi & karakter.
- **Script terenkode**: `POST /exec` & `POST /shell` menerima
  `scriptB64` (script PowerShell utuh ter-Base64 UTF-8) — perintah
  kompleks tidak lagi rusak di rantai tanda kutip.
- **Screenshot wilayah**: `GET /screenshot?x=&y=&w=&h=` memotong
  wilayah langsung dari sumbernya.
- **Kendali jendela**: `POST /window` — close/minimize/maximize/
  restore jendela berdasarkan judul.
- **Gelombang 2 — catatan anti-ambiguitas**: `GET /lastops`
  mencatat 50 operasi terakhir (dicatat sebelum respons ditulis,
  jadi tetap ada walau koneksi putus saat pengiriman).
- **Gelombang 2 — klik penahan & tahan tombol**: `modifiers` di
  `/click` & `/drag`, plus `POST /keydown` / `POST /keyup`.
- **Gelombang 2 — info sistem diperluas**: `/sysinfo` kini memuat
  cpuPercent, ramPercent, ramTotalMb, diskFreeGb, diskTotalGb,
  idleSec (detik sejak input terakhir user).
- **Gelombang 2 — bingkai instan**: `GET /frame` = JPEG
  terkompresi untuk lingkaran lihat-klik yang rapat.
- **Gelombang 2 — tunggu kondisi**: `POST /wait` menunggu jendela/
  berkas di sisi server.

## Perubahan v2.3.5 (dari v2.3.4)

- **Sudut mulus anti-alias penuh (perhalusan tampilan)**: kartu
  (`RoundedPanel`) dan tombol (`RoundedButton`) tidak lagi dipotong
  `Region` — Region Windows tidak mengenal anti-alias sehingga
  sudutnya bergerigi tangga piksel. Area di luar lengkung kini dicat
  warna latar induk, lalu bentuk bundar digambar anti-alias dengan
  `PixelOffsetMode.HighQuality`.
- **Panel chat dirapikan**: badan chat diberi bantalan 10px sehingga
  sudut persegi WebView2 menempel di bidang putih kartu (tidak
  menabrak lengkung sudut); fon kepala panel diseragamkan Segoe UI
  10pt; judul jendela memakai Segoe UI Semibold.
- **Saklar**: bayangan lembut di bawah kenop + garis tepi lintasan
  semi-transparan sangat tipis. Murni tampilan — perilaku, endpoint,
  dan logika penjaga tidak berubah sama sekali.

## Perubahan v2.3.4 (dari v2.3.3)

- **Perbaikan tugas penjaga (watchdog)**: pendaftaran tugas
  "MuseLink Watchdog" di v2.3.3 selalu GAGAL — nilai `/TR` schtasks
  mentok 261 karakter dan perintah PowerShell sebarisnya lebih
  panjang dari itu (kegagalan tercatat jujur di log). Kini naskah
  penjaga ditulis ke `%LOCALAPPDATA%\Jembatan\watchdog.ps1` dan
  tugas menjalankannya dengan `-File` (jauh di bawah batas).
- **Label saklar dipendekkan**: "Jalan otomatis" dan "Mode
  Administrator" — teks lama kepotong di kartu kepala sempit.

## Perubahan v2.3.3 (dari v2.3.2)

- **Nama tampil final: "MuseLink"** — judul jendela, judul besar di
  kartu kepala, tooltip & menu ikon tray ("Buka MuseLink"), balon
  tray, baris log pembuka (`=== MuseLink v2.3.3 dimulai ===`),
  `AppName` installer, dan nama pintasan Start Menu/Desktop kini
  "MuseLink". Nama internal tetap **jembatan** (exe, folder pasang,
  kunci registry, berkas log, field `app` di `/ping`, mutex — semua
  tidak berubah; AppId installer sama, `DefaultDirName` tetap
  disematkan ke `{autopf}\Jembatan`).
- **Migrasi nama tugas & nilai Run (semua generasi)**: tugas login
  kini **"MuseLink"**, tugas penjaga **"MuseLink Watchdog"**, nilai
  Run **"MuseLink"**. `SyncAutoStart()`/`SetAutoStart()` menghapus
  seluruh nama lama — tugas "Jembatan", "Muse Windows",
  "Muse Windows Watchdog" dan nilai Run "Jembatan", "Muse Windows" —
  agar tidak ada duplikat dari generasi mana pun.
- **Tema terang gaya iCloud.com** (murni tampilan, perilaku tidak
  berubah sama sekali): latar form `#F5F5F7`; blok kepala, area log,
  dan badan panel chat menjadi **kartu putih bersudut bundar 16px**
  (kontrol baru `RoundedPanel` di `UiControls.cs`, garis tepi
  `#E3E3E8`, margin luar 12px — garis pemisah 1px v2.3.2 hilang,
  digantikan jarak antar kartu). Tipografi Segoe UI: judul 15pt
  tebal, teks utama `#1D1D1F`, teks sekunder `#6E6E73`; log tetap
  Consolas di atas putih.
- **Saklar ala iOS** (`ToggleSwitch`) menggantikan kedua checkbox:
  lintasan 44×26 hijau `#34C759` saat menyala / `#E8E8ED` saat mati,
  kenop putih 22px; labelnya berdiri sendiri di kanan saklar dan
  ikut bisa diklik. Semantik `Checked`/`CheckedChanged` sama persis,
  jadi alur pengaturan (persist + `SyncAutoStart`) tidak berubah.
- **Tombol pil** (`RoundedButton`, digambar sendiri): "💬 Chat Muse"
  biru `#0071E3`; tombol bilah alat chat abu muda `#E8E8ED`;
  PUTUSKAN merah `#FF3B30` sudut 12px tinggi 44px, dan keadaan
  "Sambung lagi"-nya kini biru aksen (bukan hijau). Titik status
  menjadi lingkaran 10px (`RoundDot`): oranye menunggu, hijau
  `#34C759` terhubung, abu mati. Label mode: abu sekunder, atau
  biru aksen saat Administrator. Label versi 8.5pt abu di pojok
  kanan bawah kartu kepala. Label galat panel chat: teks kuning
  tua `#8A6D00` di atas `#FFF4CC`. Bilah judul Windows tetap bawaan
  (tidak borderless); ikon & tooltip tray tidak berubah.

## Perubahan v2.3.2 (dari v2.3.1)

- **Nama tampil baru: "Muse Windows"** — judul jendela, judul besar
  di kepala jendela, tooltip & menu ikon tray, `AppName` installer,
  dan nama pintasan Start Menu/Desktop kini "Muse Windows". Nama
  internal TIDAK berubah: berkas tetap `Jembatan.exe`, folder pasang
  tetap `{autopf}\Jembatan` (`DefaultDirName` disematkan eksplisit,
  AppId installer sama), kunci registry tetap
  `HKCU\Software\Jembatan`, mutex tetap `JembatanSingleInstance`,
  field `app` di `/ping` tetap `"jembatan"`.
- **Penjaga anti-crash (watchdog)**: Tugas Terjadwal **"Muse Windows
  Watchdog"** berjalan tiap 5 menit (`schtasks /SC MINUTE /MO 5`,
  hak tertinggi) dengan aksi PowerShell tersembunyi: bila proses
  `Jembatan` tidak ada, ia menyalakannya lagi — kecuali bendera
  berhenti-sengaja terpasang. Tugas ini didaftarkan/dihapus di dalam
  `SyncAutoStart()` dengan syarat yang sama seperti tugas login:
  jalan otomatis aktif + Mode Administrator diminta + proses sedang
  admin.
- **Bendera berhenti-sengaja `UserStopped`** (DWORD di
  `HKCU\Software\Jembatan`, kelas `Settings`): dipasang (1) HANYA
  oleh jalan Keluar dari menu tray, dan dibersihkan (0) setiap
  aplikasi berhasil dimulai. Penutupan oleh Windows shutdown / Task
  Manager tidak memasangnya — penjaga akan menyalakan ulang
  aplikasi, sesuai fungsinya.
- **Migrasi nama tugas & nilai Run**: tugas login kini bernama
  **"Muse Windows"** (dulu "Jembatan") dan nilai Run kini
  "Muse Windows"; `SyncAutoStart()` juga menghapus tugas lama
  bernama "Jembatan" dan nilai Run lama "Jembatan" agar tidak ada
  duplikat.
- **Log persisten**: setiap baris log jendela juga ditulis ke
  `%LOCALAPPDATA%\Jembatan\jembatan.log` (satu baris per entri,
  format `[HH:mm:ss] pesan` yang sama, dikunci + try/catch menelan
  galat). Rotasi saat aplikasi dimulai: berkas yang melewati 2 MB
  diganti nama menjadi `jembatan.log.old`. Setiap jalan dibuka
  dengan baris pemisah `=== Muse Windows v2.3.2 dimulai ===`.
- **Perapihan tampilan jendela** (tanpa mengubah perilaku): padding
  luar kepala diseragamkan 12px dan semua elemen kiri rata pada satu
  garis vertikal dengan jarak antarbaris yang rata; kedua checkbox
  kini berlebar tetap 252px sehingga teksnya tidak lagi terpotong
  saat panel chat terbuka (panel kiri ±380px); garis pemisah 1px di
  antara kepala–log dan log–baris PUTUSKAN; area log dibungkus panel
  ber-padding agar teks punya bantalan dalam; tombol PUTUSKAN pas
  44px; ritme padding panel chat disamakan dengan panel kiri.

## Perubahan v2.3 (dari v2.2)

- **Mode Administrator (akses penuh)** — checkbox kedua di jendela
  utama, "Mode Administrator (akses penuh)", disimpan sebagai DWORD
  `AdminMode` di `HKCU\Software\Jembatan` (kelas `Settings`). Nilai
  belum ada = **AKTIF secara bawaan**, sesuai perintah eksplisit user.
  Label kecil di kepala jendela menunjukkan level proses apa adanya:
  "Mode: Administrator" (kuning) atau "Mode: Pengguna biasa" (redup) —
  dari hak proses sebenarnya, bukan dari pengaturan.
- **Naik hak sendiri saat dibuka**: manifest tetap `asInvoker`.
  Di `Program.Main`, bila Mode Administrator aktif tapi proses belum
  admin, aplikasi menjalankan ulang dirinya dengan Verb `runas` —
  muncul **satu prompt UAC** — lalu proses lama keluar. Bila user
  menekan "No", tidak ada pengulangan: aplikasi lanjut sebagai
  pengguna biasa dan mencatat "Mode Administrator ditolak" di log.
  Mencentang checkbox-nya dari jendela yang belum admin memicu
  kenaikan hak yang sama saat itu juga.
- **Jalan otomatis tanpa prompt**: satu fungsi `SyncAutoStart()`
  (dipanggil saat jendela dimuat dan tiap checkbox berubah) memilih
  mekanismenya. Mode Administrator aktif + proses admin + jalan
  otomatis aktif → dibuat **Tugas Terjadwal bernama "Jembatan"**
  (`schtasks /Create /SC ONLOGON /RL HIGHEST`) yang menjalankan
  Jembatan sebagai administrator tiap login Windows **tanpa prompt
  UAC sama sekali**, dan nilai Run dicabut agar tidak dobel. Di luar
  keadaan itu, jalan otomatis kembali ke nilai Run `HKCU` seperti
  v2.1 (tugas terjadwal yang tertinggal dihapus bila proses mampu —
  butuh admin — dan cukup dicatat di log bila tidak).
- **Penjaga satu instans**: mutex bernama `JembatanSingleInstance`
  di `Program.Main` — instans kedua (benturan tugas terjadwal vs
  buka manual, atau serah-terima naik hak) keluar diam-diam;
  pengecekan dicoba berulang ±5 detik untuk memberi waktu instans
  lama mati.
- **Cara mematikan**: hilangkan centang "Mode Administrator
  (akses penuh)". Tugas terjadwal dihapus dan jalan otomatis kembali
  ke nilai Run pada kesempatan proses admin berikutnya; jendela yang
  sedang terbuka tetap admin sampai Jembatan dibuka ulang — log
  mencatat persis itu.
- **Catatan jujur soal risiko**: jembatan yang berjalan sebagai
  administrator berarti **semua perintah yang lewat jembatan —
  `/exec`, `/shell`, klik, ketik — berkekuatan administrator penuh**.
  Tombol PUTUSKAN dan jendela log tetap menjadi kendali user, dan
  perlindungan jaringan tetap Tailscale, tapi kesalahan langkah
  berdampak sebesar hak admin itu sendiri. `/ping` dan `/sysinfo`
  kini menyertakan field `admin` (bool) agar level proses selalu
  bisa diverifikasi dari luar.

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
