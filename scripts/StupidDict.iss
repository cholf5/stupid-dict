; Windows 安装包脚本（Inno Setup 6）：把自包含发布目录打成 per-user Setup.exe，
; 装到 %LOCALAPPDATA%\Programs\Stupid Dict，带开始菜单/可选桌面快捷方式和卸载器。
; 安装器 UI 仅英文；应用内语言不受影响。
; 只在 CI 的 windows job 编译（choco 安装的 Inno Setup）；ISCC 仅 Windows，
; 本地 macOS 的 scripts/package.sh 仍只出绿色 zip，打不出安装包。
;
; 编译（在仓库根目录）:
;   ISCC /DAppVersion=1.0.0 scripts\StupidDict.iss
; 源目录默认为仓库根下的 StupidDict-{AppVersion}-win-x64（CI publish 步骤的输出，
; 单文件自包含 exe），本地可用 /DSourceDir=<发布目录> 覆盖。
; 产出: artifacts/windows/StupidDict-{AppVersion}-win-x64-setup.exe

#define MyAppName "Stupid Dict"
#define MyAppExe "StupidDict.exe"
#define MyAppPublisher "cholf5"
#define MyAppURL "https://github.com/cholf5/stupid-dict"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\StupidDict-" + AppVersion + "-win-x64"
#endif

[Setup]
; AppId 是安装/升级/卸载识别同一应用的稳定标识，一经发版不可更改
AppId={{AE7B8F67-3A99-4ADD-9265-339C3F384D6C}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
; per-user 安装：无需管理员权限（应用数据本就在 %APPDATA%\StupidDict）
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExe}
; 升级安装时提示关闭正在运行的应用
CloseApplications=yes
SetupIconFile=..\src\StupidDict.App\Assets\app-icon.ico
OutputDir=..\artifacts\windows
OutputBaseFilename=StupidDict-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
