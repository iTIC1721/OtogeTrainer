; OtogeTrainer 설치 프로그램 (Inno Setup 6)
;
; 직접 컴파일하지 말고 tools/build-installer.ps1 로 만든다. 그 스크립트가 아래 값을 /D 로 넘긴다.
;   AppVersion        표시 버전 (예: 0.2.0, 0.2.0-beta.1)
;   AppVersionNumeric 파일 속성용 숫자 버전 (예: 0.2.0)
;   SourceDir         tools/publish.ps1 가 만든 배포 폴더
;   OutputDir         설치 파일을 둘 폴더
;
; 기본은 사용자별 설치(%LOCALAPPDATA%\Programs) — 관리자 권한이 필요 없다(NFR-10).
; 라이브러리 · 설정(%APPDATA%\OtogeTrainer)은 앱 폴더 밖에 있어서 업데이트 · 제거해도 그대로 남는다.

#ifndef AppVersion
  #error AppVersion 이 정의되지 않았습니다. tools/build-installer.ps1 로 빌드하세요.
#endif

#define AppName "OtogeTrainer"
#define AppExe  "OtogeTrainer.exe"
#define AppUrl  "https://github.com/iTIC1721/OtogeTrainer"

[Setup]
; AppId 는 바꾸지 않는다. 바뀌면 업데이트가 아니라 별개의 프로그램으로 설치된다.
AppId={{2789E4A7-B2A2-4C16-9FFA-0F6B93A71DBD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=iTIC1721
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersionNumeric}

DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no

OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
