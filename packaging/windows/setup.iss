#ifndef PackageRoot
 #error PackageRoot is required
#endif
[Setup]
AppId=RadioTEDU.OnAirNext.OperatorSetup
AppName=RadioTEDU OnAir Next
AppVersion=1.1.0
AppPublisher=RadioTEDU Technologies / RTAI
AppPublisherURL=https://radiotedu.com/technology
DefaultDirName={localappdata}\RadioTEDU\OnAirNext-Installer\1.1.0
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=RadioTEDU-OnAir-Next-Windows-x64-Setup
OutputDir=..\..\artifacts\installers
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\desktop-shell\icon.ico
Uninstallable=no
CloseApplications=no
RestartApplications=no
[Files]
Source: "{#PackageRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{userdesktop}\RadioTEDU OnAir Next Setup"; Filename: "{app}\desktop\RadioTEDU OnAir Next.exe"; Parameters: "--setup-package ""{app}"""; IconFilename: "{app}\desktop\resources\app\icon.ico"
[Run]
Filename: "{app}\desktop\RadioTEDU OnAir Next.exe"; Parameters: "--setup-package ""{app}"""; Description: "Open RadioTEDU OnAir Next setup"; Flags: nowait postinstall skipifsilent
