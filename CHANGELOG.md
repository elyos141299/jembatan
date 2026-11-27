# Changelog — MuseLink

Semua perubahan penting per versi. Catatan pengembang yang lebih
rinci (terutama versi ≤ 3.3.2) ada di README.md.

## 5.18.0 — 2026-11-27

Perbaikan dari temuan program eksperimen (Ronde 9):
- /act: tangga pemulihan kini BERHENTI di anak tangga pertama
  yang benar-benar bertindak tetapi gagal verifikasi, dan
  melapor dengan kode baru "acted_unverified" (bidang acted:
  true + anak tangga + attempts). Dulu tangga terus naik dan
  mengulang aksi yang sama lewat jalur lain — terbukti satu
  checkbox ter-toggle 4x dalam satu panggilan /act saat
  expectText tidak pernah cocok. Perilaku saat verifikasi
  lulus atau saat tidak ada anak tangga yang bertindak tidak
  berubah.
- /uia3/find & /uia3/act: parameter "name" kini diterima
  sebagai alias "text" (konvensi /uia/find). Dulu "name"
  diam-diam diabaikan sehingga pencarian berjalan TANPA
  saringan nama sama sekali. Parameter "text" tetap menang
  bila keduanya diberikan.

## 5.17.0 — 2026-11-27

Perbaikan dari temuan program eksperimen (Ronde 8):
- /uia/find & /uia3/find: tiap elemen kini membawa bidang
  "offscreen" (nilai IsOffscreen UIA apa adanya) — pemanggil
  bisa menilai keterlihatan elemen dari data, bukan menebak
  dari koordinat (latar: tombol dialog hantu Notepad Win11
  yang berulang hampir menyesatkan klik, Ronde 3/6/8).
- /frame: parameter preset=status — setara w=640&q=50
  untuk cek status cepat; parameter w/q eksplisit tetap
  menang bila diberikan.

## 5.16.0 — 2026-11-27

Perbaikan dari temuan program eksperimen (Ronde 6):
- Makro: kejadian VK_PACKET hasil ketikan /type kini direkam
  BESERTA karakter Unicodenya (dari scan code hook) dan
  diputar ulang sebagai input Unicode — dulu makro semacam
  itu diputar tanpa mengetik apa pun, gagal diam-diam.
  Makro lama (VK murni / VK_PACKET tanpa karakter) diputar
  persis seperti sebelumnya.
- Refleks pemicu window: perubahan JUDUL pada jendela yang
  sudah dikenal kini dinilai ulang — model tab Windows 11
  (berkas dibuka jadi tab di jendela lama, hwnd tetap) dulu
  tidak pernah memicu aturan. Cooldown per aturan tetap
  menahan banjir; catatan log membedakan "judul berubah"
  dari "jendela baru".
- /health: bidang ai.whisperReady — model Whisper di
  sidecar sudah termuat atau belum (dilaporkan sidecar di
  /health-nya), agar agent tidak menebak dari 503 pada
  transkrip pertama.
- Scheduler: jeda tilik 20 → 5 detik — jadwal "once" yang
  terukur telat 21 detik di Ronde 6 kini terpicu dalam
  hitungan detik.

## 5.15.0 — 2026-11-27

Perbaikan dari temuan program eksperimen (Ronde 2 & 4):
- /key: kamus tombol mode VK kini mengenal numpad ADD
  (0x6B), SUBTRACT (0x6D), MULTIPLY (0x6A), DIVIDE (0x6F),
  DECIMAL (0x6E) — `^{ADD}` dulu ditolak 400 padahal mode
  scan bisa. Pesan error nama tak dikenal kini menyebut
  alternatif mode scan ({"keys":"ctrl+add","scan":true}).
- /locate: anak tangga baru "uia3" di antara UIA jendela
  depan dan OCR — memindai SEMUA jendela (mesin /uia3/find)
  sehingga ikon taskbar (Start, File Explorer, Chrome) dan
  elemen jendela latar kini ketemu dari server langsung;
  dulu hanya bisa lewat fallback di sisi agent.
- /pw/open tanpa url: ditolak 400 di server dengan pesan
  "url wajib diisi" — dulu pesan baru muncul dari sidecar
  sesudah Chrome diluncurkan.
- /pw/screenshot: param path yang diabaikan kini dicatat
  di respons ("note") — hasil tetap base64 inline.

## 5.14.1 — 2026-11-26

Perbaikan dari baterai gerbang 5.14.0 (ronde yang sama):
- /uia3 find/act: saringan automationId & nama kini berlaku
  BERSAMA bila keduanya diisi. Di baterai gerbang, aid
  "TextBox" saja menabrak address bar Explorer (sama-sama
  "TextBox") dan nama "Search" saja menabrak item daftar
  "Searches" — gabungan keduanya unik.
- Catatan baterai: setvalue pada elemen Edit NAMA ITEM di
  Explorer = mengganti nama berkas/folder (rename). Jangan
  pernah dipakai sebagai sasaran uji — satu folder user
  sempat terganti nama dan langsung dipulihkan utuh.

## 5.14.0 — 2026-11-26

"Upgrade Semua" (pilihan user; UI nomor 4 di-skip user):

- **Mesin pemulihan /act:** endpoint tangga lintas-jalur —
  (1) UIA jendela depan, (2) UIA3 lintas jendela,
  (3) visual /locate, (4) OCR murni; tiap anak tangga
  diverifikasi expectText di sekitar titik aksi bila
  diminta; semua percobaan dilaporkan apa adanya.
  (Retry+verify+fallback per aksi /batch sudah ada sejak
  v5.6.0 — /act menutup separuh yang belum ada: gugur
  LINTAS JALUR otomatis.) /act bisa dipakai di /batch
  sebagai {"do":"act"}.
- **/uia3 (permukaan FlaUI lintas jendela):** FlaUI.UIA3
  sudah menjadi mesin /uia sejak v2.4.0; yang baru:
  /uia3/find & /uia3/act memindai SEMUA jendela tingkat
  atas (filter window/process, tunggu-coba-ulang) —
  elemen di jendela latar kini bisa ditemukan & diaksi
  tanpa mengangkat jendelanya.
- **Playwright (/pw/*):** sidecar Python pw_sidecar.py
  (venv AI-Tools, pola AiBridge; HTTP 127.0.0.1:8771)
  mengendarai Chrome TERPASANG dengan PROFIL OTOMASI
  SENDIRI (%LOCALAPPDATA%\Jembatan\pw-profile — profil
  harian tidak disentuh). Endpoint: /pw/open, /pw/goto,
  /pw/click (teks/selector), /pw/fill, /pw/text, /pw/wait,
  /pw/screenshot, /pw/close, GET /pw/state. Butuh paket
  pip "playwright" di venv AI-Tools (tanpa unduh browser —
  memakai Chrome terpasang). Installer kini memaketkan
  pw_sidecar.py di samping exe.

## 5.13.0 — 2026-11-26

"Refleks & Arbitrase" (perintah user "push sampai mentok"
26 Nov 2026):

ARBITRASE — siapa berhak menggerakkan PC:
- Sensor sentuhan FISIK (PhysicalInput): hook low-level
  mouse+keyboard membedakan kejadian fisik dari suntikan
  (flag INJECTED) — terbaca di /health (physicalInput:
  agoSec/count/lastKind).
- YIELD: putar makro, /type berpotongan, dan /drag kini
  berhenti seketika saat user menyentuh perangkat fisik di
  tengah operasi — respons jujur code "user_preempted",
  alasan selesai makro tercatat ("preempted"), kejadian
  masuk /events sebagai "yield". Suntikan sendiri tidak
  memicu yield.
- Lease API: POST /input/lease {owner, ttlSec 10-300} &
  /input/lease/release & GET /input/lease — di atas
  mekanisme lease taskId v3.11.0 (agen lain tertolak 429
  input_leased selama lease aktif).
- Penjaga idle rutinitas: /routines/save menerima
  minIdleSec; /routines/run menunda (409 user_active)
  selama sentuhan fisik user lebih baru dari ambang —
  berlaku juga untuk scheduler & refleks.

REFLEKS — bereaksi tanpa diperintah:
- Mesin aturan tersimpan (reflex-rules.json): pemicu
  window (jendela baru cocok judul/proses), process
  (mulai/berhenti), file (berkas baru/berubah di folder
  terpantau, FileSystemWatcher).
- Aksi aman: log, event (masuk /events), tasknote
  (catatan Task Memory), routine:<nama>,
  dismiss:<teks tombol persis> via UIA pada jendela baru.
- Cooldown per aturan (bawaan 30 dtk) anti-banjir;
  GET /reflex/rules, POST /reflex/rules(+delete),
  GET /reflex/log (100 pemicuan terakhir).

## 5.12.1 — 2026-11-26

- /health: pid & processUptimeSec kini BENAR-BENAR di
  /health (BuildHealth). Di 5.12.0 keduanya tak sengaja
  mendarat di endpoint diagnostik endpoint — ketahuan
  dari baterai tes gerbang 5.12.0 dan diperbaiki di
  ronde yang sama.

## 5.12.0 — 2026-11-26

"Tangan Stabil & Ingat" (approv user "Gas semua 1-5"
26 Nov 2026), dari temuan full test v5.11.1:
- Penyelidikan restart: TERBUKTI bukan crash dan bukan
  makro — log aplikasi mencatat "Listener DIMATIKAN oleh
  kamu" (AppHost.ToggleServer, jalur tombol UI) pada
  20:16 & 20:18; uptimeSec adalah umur LISTENER sehingga
  mati/nyala listener terbaca sebagai restart. Uji tekanan
  putar makro 10x: PID tetap, nol gagal.
- /health: blok "process" kini memuat pid &
  processUptimeSec — umur proses dan umur listener terbaca
  terpisah, tidak tertukar lagi.
- Pencatat crash: UnhandledException &
  UnobservedTaskException tertulis ke crash.log
  (%LOCALAPPDATA%\Jembatan) — kematian di masa depan
  meninggalkan jejak, bukan teka-teki.
- /type PINTAR: mode "auto" (bawaan) mengirim teks lewat
  jalur tempel (clipboard dipulihkan) bila jendela depan
  aplikasi rewel TSF (Notepad Win11); mode "paste"/"keys"
  memaksa jalur. Respons memuat "via" & "chars".
- Task Memory & Context: POST /task/start|note|done +
  GET /task; konteks tugas aktif tersimpan JSON
  (task-context.json, tahan restart) dan ikut terbaca di
  GET /context — ingatan kerja bersama agent & aplikasi.
- VONIS PENA (gerbang ditutup): /penstroke di Paint
  (Windows Ink) menghasilkan NOL tinta (2x, termasuk
  variasi kursor diparkir) dan di Krita hanya garis tipis
  bertekanan terkunci + palet terpicu. Pena sintetis tidak
  diterima sebagai pena bertekanan oleh tumpukan Ink asli
  di mesin ini; endpoint tetap ada (tidak berbahaya,
  melaporkan apa adanya) tapi BUKAN alat gambar. Jalur
  gambar resmi tetap mouse SendInput (terbukti setia).

## 5.11.1 — 2026-11-26

Dua obat dari baterai tes v5.11.0 (approv user "Gas
v5.11.1" 26 Nov 2026):
- /paste: pemulihan clipboard kini POTRET PER FORMAT
  (FileDrop > gambar > teks, data konkret disimpan lalu
  dipasang ulang sebagai DataObject baru). Cara lama
  (memasang ulang objek IDataObject mentah) terbukti
  membuat clipboard KOSONG sesudah tempel.
- /penstroke: semua bingkai pena kini membawa cap waktu
  (DwTime) & nomor bingkai (FrameId) yang hidup, plus
  bingkai hover pembuka/penutup. Di 5.11.0 keduanya nol
  sehingga Krita menggabung satu goresan jadi satu
  kejadian (palet pop-up + satu titik liar, tanpa garis).
- Catatan vonis baterai tes: kerusakan huruf acak /type
  pada teks panjang terbukti berasal dari tumpukan teks
  Notepad Win11 (teks sama PERSIS ke omnibox Chrome &
  via /paste) — bukan cacat pengiriman; untuk teks
  persis ke editor TSF gunakan /paste.

## 5.11.0 — 2026-11-26

"Upgrade Tangan: Tiga Jalur Kendali + Tekanan Kuas"
(PLAN-v5.11.0 — menggantikan PLAN-v5.10.1; approv user
"gas" 26 Nov 2026). Tiga jalur kendali kini resmi:
desktop terstruktur = UIA/FlaUI, canvas & gambar = mouse
SendInput + pena, web = CDP.

Bagian A — perbaikan cacat audit (delapan butir):
- A.1 Semua gerakan mouse kini SendInput ABSOLUT
  (MoveTo; MOUSEEVENTF_ABSOLUTE|VIRTUALDESK) — SetCursorPos
  hanya jalan kembali. Mesin tinta & perekam makro kini
  menerima gerakan yang sama seperti pointer fisik.
  Pelepasan tombol DIVERIFIKASI (ReleaseButton:
  GetAsyncKeyState, kirim ulang maks 3×) — obat garis liar
  antar goresan. Perekam makro menambah PEMUNGUT POSISI
  berkala (50 ms) yang menangkap gerakan teleport yang
  luput dari hook.
- A.2 Ritme ketik dibedah: potongan 20 -> 12 karakter +
  jeda antar-potongan 40 ms (tumpukan TSF sempat terkuras;
  anomali kerusakan indeks tetap pada teks panjang).
- A.3 /type memeriksa Emergency Stop di TIAP potongan
  (berhenti di tengah + typedChars) dan menolak teks
  > 20.000 karakter (400).
- A.4 ScrollSmooth dijepit total ±4800; DragPath ditolak
  bila > 200 titik atau total diam > 30 detik (400).
- A.5 /key menerima holdMs (0–5000) — jeda tahan ketukan
  untuk game polling; berlaku juga mode scan.
- A.6 VkFromName lengkap: F13–F24, numpad penuh, sisip/
  printscreen/scrolllock/pause/capslock/numlock/menu,
  media (next/prev/stop/play-pause) & volume (up/down/mute).
- A.7 /paste menyimpan & MEMULIHKAN clipboard user
  sesudah tempel (jeda 250 ms); keepClipboard:true =
  perilaku lama.
- A.8 IdleMilliseconds/IdleSeconds aman dari luapan tick
  32-bit (selisih dihitung di ruang uint; dulu rusak
  sesudah uptime 49,7 hari).

Bagian B — kemampuan gambar:
- B.9 /penstroke (PROTOTIPE): goresan pena bertekanan
  Windows Ink (CreateSyntheticPointerDevice +
  InjectSyntheticPointerInput, tekanan 0..1024 per titik).
  Aplikasi tujuan harus memakai API "Windows 8+ Pointer
  Input". Bergerbang tes goresan meruncing di Krita.
- B.10 /penstroke verify:true — potret kotak goresan
  sebelum/sesudah, laporkan changedRatio & inkDetected.
- B.11 DragPath berkecepatan ADAPTIF: langkah 16px di
  lintasan lurus, 9px belokan sedang, 6px belokan tajam
  (sudut di titik tujuan segmen).

Bagian C — jalur UIA (FlaUI sudah terpasang sejak v2.4.0
& /uia/act sejak v3.5.0): /uia/act kini menerima waitMs
(0–60000) — pencarian elemen dicoba ulang tiap 250 ms
sampai batas waktu, bukan sekali tembak.

Bagian D — mata: sidecar AI + endpoint /parse OmniParser
(icon-detect microsoft/OmniParser-v2.0 di GPU, dimuat
malas; label kotak = teks EasyOCR yang pusatnya di dalam
kotak). /locate menambah tahap "omniparser" sesudah
UIA/OCR/YOLO; mode "omni" mengembalikan semua kotak
elemen (maks 40). Keterangan Florence-2 DITUNDA ronde
ini dengan sadar (label dari OCR sudah menutup kebutuhan
grounding; caption model menyusul bila terbukti perlu).

Bagian E — jalur web: CdpBrowser.cs — CDP MENTAH lewat
WebSocket (keputusan teknis: TANPA pustaka Playwright
agar exe tetap berkas tunggal; fungsi sesuai rencana).
Chrome TERPASANG dikendalikan dengan PROFIL KHUSUS
otomasi (LocalAppData\Jembatan\browser-profile; profil
utama user tidak disentuh). Endpoint: /browser/start|
stop|goto|eval|waitfor|click|setvalue|text. Klik & isi
nilai di tingkat DOM; waitfor = polling Smart Waiting.
Aksi peramban masuk InputPaths (tunduk lease & halt).

Bagian F — AutoHotkey v2 terpasang di PC sebagai perkakas
samping (di luar aplikasi; hanya skrip tulisan agen).
Power Automate Desktop DITOLAK user (dicatat di rencana).

## 5.10.0 — 2026-11-26

Upgrade Tangan jilid 2 (user memilih "Semua" dari menu 8
butir; PLAN-v5.10.0). Semua tambahan opt-in — perilaku lama
tidak berubah tanpa bidang baru:

- /scroll: axis "x" (HWHEEL) + smooth (langkah ±40/15ms).
- GET /cursor (posisi kursor) + /move relatif {dx, dy} dan
  glideMs (gerak meluncur ease-in-out, bukan teleport).
- /type rateMs (0–50 ms/huruf; bawaan 15 = lama).
- /drag "via": seret jalur waypoints dengan dwellMs per
  titik (NativeInput.DragPath).
- Mode game: /key, /keydown, /keyup menerima "scan":true —
  kejadian KEYEVENTF_SCANCODE dari MapVirtualKey(VSC_EX),
  sintaks gabungan "ctrl+shift+esc" untuk /key.
- Clipboard gambar & file: POST/GET /clipboard/image
  (PNG base64), GET /clipboard/files. Log hanya ukuran.
- Makro (MacroRecorder.cs baru): POST /record/start|stop,
  GET /record/status, GET /macros, POST /macros/run|stop.
  Hook LL mouse+keyboard di utas berpump pesan; sampel
  gerak 30ms/4px; batas 10 mnt/20.000 kejadian; kejadian
  suntikan ditandai Inj; putar ulang berutas sendiri
  dengan jaring pengaman lepas-tombol dan gerbang
  Emergency Stop (/macros/run masuk InputPaths;
  /record/stop sengaja di luar agar selalu bisa berhenti).

## 5.9.0 — 2026-11-26

Upgrade Tangan (perintah user: "dari kemaren kita belom
upgrade tangan kamu"). Audit kode sebelum bangun menemukan
sebagian rencana ternyata SUDAH ada sejak v2.4–v3.11 dan
tidak perlu dibangun ulang: urutan atomik = /batch (v2.4.1,
dengan InputArbiter v3.11.0), klik count 1–3, ketik Unicode
SendInput, clipboard GET/POST + /paste, /setvalue UIA.
Yang benar-benar baru di ronde ini:

- **Arbitrase manusia di /batch**: bidang opsional
  `yieldToUserMs` — bila masukan terakhir user <1 detik,
  rangkaian menunda sampai layar tenang (atau batas
  tercapai) alih-alih rebutan kursor; dilaporkan sebagai
  userYielded/userYieldWaitMs/userStillActive di respons.
  Sensor baru NativeInput.IdleMilliseconds().
- **/mousedown + /mouseup**: tahan/lepas tombol mouse
  (pasangan /keydown-/keyup) — tekan-tahan & seret manual
  dua tahap. Tombol tertahan dicatat dan dilepas paksa oleh
  jaring pengaman yang sama (Stop & /keys/release).
- **/click holdMs**: long-press dalam satu aksi lokal.
- **/drag berparameter**: grabMs/steps/stepMs/settleMs —
  bawaan persis perilaku lama (60/16/15/60).
- Endpoint lama & protokol tidak berubah; /mousedown dan
  /mouseup masuk daftar jalur masukan (terblokir saat
  Emergency Stop, seperti jalur masukan lain).

## 5.8.0 — 2026-11-26

Kembali ke tampilan Jembatan v1 (perintah user: "persis
seperti pertama kali namun tanpa WebView2", judul jendela
"MuseLink"). Shell Control Room/Tray-First v5.0–5.7
dipensiunkan:

- Jendela SATU, gelap, title bar standar Windows: judul
  "MuseLink", baris status (kotak warna + "Muse terhubung •
  aktivitas terakhir HH:MM:SS"), baris port persis v1, log
  aktivitas monospace real-time ([HH:MM:SS] pesan), tombol
  PUTUSKAN/SAMBUNGKAN selebar jendela. Warna disampel dari
  screenshot asli v1 (#0D1117 / #161B22 / #2EA043 / #B9372D).
- 5 halaman WPF (Home, Activity, AI, Settings, About) +
  UiKit + tema Control Room dihapus. Konsol dalam aplikasi
  ikut pensiun; jalur bridge untuk agent tidak berubah.
- Tiga saklar pengaturan (jalan otomatis, Mode Administrator,
  cegah PC tidur) PINDAH ke menu tray sebagai item bercentang
  — jendela v1 memang tanpa saklar, pengaturan tetap terjangkau.
- Paket Microsoft.Web.WebView2 DICABUT dari proyek (kode
  sudah tidak memakainya; sisa referensi warisan) + pengaturan
  zoom panel chat dibersihkan. Aplikasi kini bebas WebView2.
- Backend nol sentuh: server, semua engine, endpoint,
  tray-first (start --auto tanpa jendela, X sembunyi ke tray),
  dan Exit final v5.7.1 dipertahankan persis.

## 5.7.1 — 2026-11-26

Perbaikan Exit (temuan tes Tray-First di hari yang sama):
"Keluar" dari menu tray mematikan aplikasi, tetapi penjaga
anti-crash menyalakannya lagi <1 menit kemudian — bendera
berhenti-sengaja `UserStopped` (rancangan v2.3.2, dibaca
skrip penjaga tiap menit) ternyata tidak pernah dipasang
oleh UI WPF sejak v5.0.0, dan tidak pernah dibersihkan di
mana pun. Kini: ExitApp memasang bendera sebelum Shutdown,
OnStartup membersihkannya setiap aplikasi dimulai. Exit
kini benar-benar berhenti; perlindungan anti-crash untuk
crash sungguhan tidak berubah.

## 5.7.0 — 2026-11-26

Penyederhanaan UI **Tray-First** (prompt user di hari yang sama:
"Simplify, don't rebuild" — MuseLink pada intinya aplikasi
jembatan yang bersemayam di tray, bukan dashboard). Backend,
endpoint, protokol & mesin AI TIDAK berubah sama sekali.

### Berubah
- **Navigasi 9 → 5 halaman:** Home, Activity, AI, Settings,
  About. Halaman Desktop Control, Live Screen, System Monitor,
  Automation & Integrations dihapus sebagai halaman (semua
  fungsinya tetap tersedia lewat bridge; yang hilang hanya
  etalase tampilannya).
- **Panel konteks kanan dibuang** dari semua halaman (isinya
  duplikat Overview/Activity). Bar atas dirampingkan: lonceng
  notifikasi & chip pengguna display-only dihapus; tagline
  sidebar menjadi "DESKTOP BRIDGE". Ukuran awal jendela
  1200×780 (dari 1440×900).
- **Home** = Overview dirampingkan: 5 kartu metrik (Muse,
  Bridge, CPU, GPU, Disk), aktivitas AI saat ini, aktivitas
  terakhir. Quick Actions & System Overview dibuang.
- **AI** = Muse Console tanpa bilah percakapan contoh:
  konsol perintah PowerShell utuh seperti sebelumnya, ditambah
  panel status MESIN AI (sidecar diberi label jujur "Siaga —
  menyala saat dipakai") & INTEGRASI (status aplikasi
  terdeteksi, logika halaman Integrations lama, tanpa tombol
  Configure palsu).
- **Settings** hanya menyisakan kategori berisi nyata:
  General (3 saklar asli, berlaku seketika — kolom nama/
  bahasa/saklar Notifikasi display-only dan tombol Simpan
  palsu dibuang), Desktop Bridge, Appearance. About menjadi
  halaman utama tersendiri.
- **Startup otomatis tidak lagi membuka jendela sama sekali**
  (sebelumnya tampil ±6 detik lalu sembunyi): mode --auto
  langsung bersemayam di tray.
- **Menu tray** mendapat baris status non-aktif di kepala:
  versi + "Bridge Online/Offline" (+ "Stop aktif").

### Tetap (terverifikasi ulang)
- Tombol X menyembunyikan ke tray; Keluar hanya lewat tray.
- Start with Windows, Mode Administrator, Emergency Stop,
  Verification/Recovery/Scheduler, media FFmpeg, OCR/deteksi
  GPU, transkripsi Whisper — semua seperti 5.6.0.

## 5.6.0 — 2026-11-26

Paket "upgrade yang bisa di-upgrade" (perintah user) + koreksi
audit tampilan di hari yang sama: tiga engine baru di server
(Verification, Recovery, Scheduler), perkakas media FFmpeg,
penyimpanan SQLite, dan ikon aplikasi BARU menggantikan logo
lama di semua permukaan Windows.

### Baru / berubah
- **Verification Engine** — field opsional per aksi di /batch:
  `"verify":{kind, ...}` membuktikan hasil aksi sebelum dianggap
  berhasil (kind: text=OCR wilayah, uia=nama elemen, value=nilai
  elemen bernama, window=judul jendela, process=proses
  jalan/hilang, stable=layar diam selama stableMs, file=berkas
  ada/minSize). Hasil per langkah kini membawa `verified` +
  `verifyDetail`. Endpoint mandiri **POST /verify** memakai
  mesin yang sama. Rutinitas otomatis ikut terverifikasi karena
  /routines/run berjalan di atas /batch.
- **Recovery Engine** — field `"retry":{times, delayMs}` per
  aksi (aksi+verifikasi diulang, jeda membesar ×2) dan
  `"fallback":{aksi}` (aksi cadangan dicoba sekali bila tetap
  gagal; hasilnya ditandai `recovered:true`). Jujur: ini jalan
  pintas pemulihan, BUKAN undo.
- **Scheduler Engine** — jadwal tersimpan menjalankan rutinitas
  bernama: pola daily (HH:mm), weekly (hari + HH:mm), interval
  (tiap N menit), once (tanggal-waktu). Endpoint GET /schedules,
  POST /schedules/save|delete|toggle. Aturan aman: slot lewat
  >15 menit (PC mati) dicatat "terlewat" dan dilompati, tidak
  dikejar; input ter-halt/ tersewa → ditunda. /health melaporkan
  `scheduler` (jumlah, aktif, slot terdekat).
- **Penyimpanan SQLite** — jadwal disimpan di
  %LOCALAPPDATA%\Jembatan\schedules.db lewat
  Microsoft.Data.Sqlite (keputusan tool ronde ini: SQLite =
  jalur upgrade penyimpanan terstruktur; JSON Routines tetap).
- **Perkakas media FFmpeg** — POST /media/probe (metadata
  ffprobe), /media/thumb (satu bingkai → gambar), /media/trim
  (potong; copy tanpa encode atau H.264/AAC), /media/convert
  (menurut ekstensi tujuan; .wav = PCM 16 kHz mono siap
  Whisper). Proses berargumen rapi tanpa shell, berbatas waktu.
  /health melaporkan `media` (ffmpeg/ffprobe/versi).
- **ripgrep di PC** — dipasang via WinGet bersamaan ronde ini
  (pencarian teks dalam berkas secepat Everything untuk nama).

### Koreksi tampilan (audit 26 Nov)
- **Ikon aplikasi BARU** — icon.ico digambar ulang persis spec
  logo Figma (rangka cyan tegak + rangka violet 45° + titik
  emas di atas ubin gelap #1A2330), 8 ukuran digambar native
  (16–256). Karena tray mengambil ikon dari exe dan pintasan
  desktop + ikon setup mengikuti berkas yang sama, SATU
  penggantian ini memperbarui tray, taskbar, desktop, dan
  setup sekaligus — sebelumnya semuanya masih logo rantai
  ungu-lavender lama (temuan user sendiri).
- **Maximized tidak lagi tertutup taskbar** — WM_GETMINMAXINFO
  dibelokkan ke area kerja monitor; isi bawah Console/Desktop/
  Activity yang dulu kepotong kini tampil penuh.
- **Grafik System Monitor mengalir terus** — riwayat CPU/RAM
  kini diisi sampler latar bersama (tiap 2 dtk) sejak halaman
  pertama dibuka, bukan hanya selama halaman tampil; grafik
  tidak lagi tampak kosong.
- Nama perangkat "余呉" di Settings BUKAN bug: nama Windows PC
  ini memang dua karakter itu (terverifikasi dari
  MachineName/hostname) — aplikasi menampilkannya dengan benar.

## 5.5.0 — 2026-11-26

Paket "semua" (perintah user): empat upgrade visual/suara GPU
sekaligus di atas fondasi sidecar 5.4.

### Baru / berubah
- **POST /transcribe** — {audio: base64 WAV} -> teks via
  faster-whisper (small, float16) di GPU sidecar. Model Whisper
  dimuat malas pada panggilan pertama; /health sidecar kini
  melaporkan `model` + status `whisper`.
- **/listen {transcribe:true}** — rekaman loopback/mic yang
  sama kini bisa langsung dikembalikan sebagai TEKS (JSON:
  text/language/ms/source/seconds), bukan WAV mentah. Ini
  jembatan perintah suara bahasa Indonesia: Whisper paham id,
  pengenal suara Windows tidak. Sidecar gagal -> 503 jujur;
  tanpa transcribe perilaku lama utuh.
- **/ocr bawaan = GPU dulu** — tanpa ?engine= (dan tanpa
  enhance), EasyOCR GPU dicoba pertama; tangga mesin lama
  (WinRT/Tesseract/auto) menjadi cadangan otomatis. Mode yang
  dipakai tetap dilaporkan apa adanya.
- **/locate cadangan YOLO (Visual Grounding)** — mode auto/yolo:
  bila UIA + OCR gagal, query dipetakan ke label COCO (alias
  Indonesia: orang->person, hp->cell phone, kucing->cat, dst)
  dan YOLO GPU mencari objeknya di layar; kandidat bervia
  "yolo-gpu". YOLO hanya mengenal label tetapnya — tombol UI
  tetap wilayah UIA/OCR.
- **Model deteksi medium** — sidecar memuat yolo11m.pt lebih
  dulu (jauh lebih akurat dari nano; inferensi tetap puluhan
  ms di GPU), yolo11n.pt jadi cadangan. /detect & /health kini
  melaporkan `model` yang benar-benar dimuat.

### Catatan
- Tiga model (YOLO medium + EasyOCR + Whisper) berbagi VRAM
  6 GB (±3-4 GB); sidecar tetap mulai malas dan mati bersama
  MuseLink bila ia yang memulainya.

## 5.4.1 — 2026-11-26

Perbaikan kemasan 5.4.0: `ai_sidecar.py` kini ikut di dalam
installer (baris Source baru di jembatan.iss) dan terpasang di
samping exe. Di 5.4.0 berkas itu tidak terbawa installer —
fitur GPU tetap jalan hanya karena salinannya sudah ada di
AI-Tools; pemasangan bersih akan kehilangan skrip sidecar.
Tanpa perubahan fitur/perilaku lain.

## 5.4.0 — 2026-11-26

Visual GPU (perintah user: "visual harus dari GPU"). Mesin AI
lokal yang terpasang di PC (Python venv `AI-Tools`: YOLO +
faster-whisper + EasyOCR di atas torch CUDA / GTX 1660 Super)
kini tersambung ke MuseLink lewat sidecar HTTP lokal.

### Baru
- **POST /detect** — deteksi objek YOLO11n di GPU atas wilayah
  layar `{x,y,w,h}` (bawaan: seluruh layar). Balikan: daftar
  `{label, conf, x, y, w, h}` dalam koordinat LAYAR + `ms`
  inferensi. Lapisan tambahan untuk Visual Grounding; tombol UI
  tetap urusan UIA/OCR (YOLO bawaan mengenali objek dunia nyata).
- **GET /ocr?engine=gpu** — OCR EasyOCR (id+en) di GPU lewat
  sidecar; format respons sama persis dengan mesin lain
  (`mode: "gpu"`). Sidecar gagal -> jatuh ke mesin bawaan dan
  mode dilaporkan apa adanya.
- **AiBridge + ai_sidecar.py** — sidecar Python (127.0.0.1:8770)
  memuat model SEKALI ke VRAM (±15-30 dtk pada panggilan AI
  pertama), dimulai malas oleh MuseLink, dimatikan bersama
  server bila MuseLink yang memulainya. Skrip sidecar terbit
  di samping exe dan disalin ke `AI-Tools\sidecar\` saat dipakai.
- **/health** kini memuat kunci `ai`: `{installed, sidecar,
  device: "cuda"}` (status sidecar di-cache 10 detik).

### Tetap
- Tangkapan layar sudah DXGI-first (GPU) dengan cadangan GDI —
  tidak berubah. Endpoint lama & tampilan aplikasi: nol
  perubahan.

## 5.3.1 — 2026-11-26

Perbaikan crash: membuka Muse Console (dan halaman lain yang
memuat tombol contoh desain tanpa handler) membunuh aplikasi —
UiKit.Btn/PrimaryBtn memasang handler null ke routed event WPF
(Click), dan AddHandler(null) melempar ArgumentNullException.
Kini handler hanya dipasang bila tidak null. Tanpa perubahan
tampilan/fitur dari 5.3.0.

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
