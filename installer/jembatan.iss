; Installer MuseLink (nama internal: Jembatan) — pola sama
; seperti wPlay: per-user (tanpa admin), Inno Setup 6.
; File Jembatan.exe diambil dari folder publish\ hasil dotnet publish.

[Setup]
AppId={{7D1C9A42-5B3E-4F08-9E6A-2C4D8B0F5A71}
AppName=MuseLink
SetupIconFile=..\icon.ico
AppVersion=2.5.6
AppPublisher=Muse
; DefaultDirName disematkan eksplisit ke folder lama {autopf}\Jembatan:
; nama tampil berubah jadi "MuseLink", folder pasang TIDAK ikut pindah.
DefaultDirName={autopf}\Jembatan
DefaultGroupName=Jembatan
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=Jembatan-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Jembatan.exe

[Tasks]
Name: "desktopicon"; Description: "Buat pintasan di Desktop"; GroupDescription: "Pintasan tambahan:"

[Files]
Source: "..\publish\Jembatan.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MuseLink"; Filename: "{app}\Jembatan.exe"
Name: "{autodesktop}\MuseLink"; Filename: "{app}\Jembatan.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Jembatan.exe"; Description: "Buka MuseLink sekarang"; Flags: nowait postinstall skipifsilent
