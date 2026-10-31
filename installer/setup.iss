; QP12 配件管理系统 安装包脚本（Inno Setup 6，CI/本地共用）
; 更新原则：appsettings.json（数据库连接）/ print.ini（打印配置）为用户配置，
;          onlyifdoesntexist = 首次安装复制，之后升级永不覆盖
; 版本注入：CI 按发布 tag 传 /DAppVersion=0.0.9；本地编译缺省 0.0.9
; AppId 固定不变（旧版"长远测试预览版"可覆盖升级为 QP12）
#ifndef AppVersion
#define AppVersion "0.0.9"
#endif

[Setup]
AppId={{6E4F9C2A-51B7-4A6D-9E3C-8A12D5F0B744}}
AppName=QP12
AppVersion={#AppVersion}
AppVerName=QP12 {#AppVersion}
DefaultDirName={autopf}\QP12
UninstallDisplayIcon={app}\QP12.exe
SetupIconFile=..\Ico\QP12.ico
OutputDir=.
OutputBaseFilename=QP12_{#AppVersion}_安装包
Compression=lzma2/max
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加任务:"

[Files]
; 主程序（含 .NET 8 运行时，自包含）——不含用户配置文件
Source: "publish\*"; Excludes: "appsettings.json,print.ini"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; 用户配置：已存在则永不覆盖（数据库连接配置/打印配置升级保留）
Source: "publish\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "publish\print.ini"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{autoprograms}\QP12 配件管理系统"; Filename: "{app}\QP12.exe"
Name: "{autodesktop}\QP12 配件管理系统"; Filename: "{app}\QP12.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\QP12.exe"; Description: "运行 {cm:LaunchProgram,QP12}"; Flags: nowait postinstall skipifsilent
