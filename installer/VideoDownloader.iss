#define AppName "Video Downloader"
#define AppVersion "1.0.12"
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
OutputBaseFilename=VideoDownloader-1.0.12-win-x64-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\VideoBrowser.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\VideoDownload\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Video Downloader"; Filename: "{app}\VideoBrowser.exe"
Name: "{autodesktop}\Video Downloader"; Filename: "{app}\VideoBrowser.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\VideoBrowser.exe"; Description: "Launch Video Downloader"; Flags: nowait postinstall skipifsilent
