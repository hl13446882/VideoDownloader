#define AppName "Video Downloader"
#define AppVersion "0.2.21-beta"
#define Publisher "VideoDownloader"
#define PublishDir "..\publish"

[Setup]
AppId={{5B9E9ED5-6D2C-4C57-8C0E-61D1C02BA9A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
DefaultDirName={autopf}\VideoDownloader
DefaultGroupName=Video Downloader
DisableProgramGroupPage=yes
OutputDir=..\publish\installer
OutputBaseFilename=VideoDownloader-0.2.21-beta-win-x64-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\VideoDownloader.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Video Downloader"; Filename: "{app}\VideoDownloader.exe"
Name: "{autodesktop}\Video Downloader"; Filename: "{app}\VideoDownloader.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\VideoDownloader.exe"; Description: "Launch Video Downloader"; Flags: nowait postinstall skipifsilent
