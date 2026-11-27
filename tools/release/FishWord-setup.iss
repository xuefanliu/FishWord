; FishWord 安装器脚本（Inno Setup 6/7）
; 编译：ISCC.exe tools/release/FishWord-setup.iss /DSourceDir=<便携版组装目录> /DOutputDir=<产物输出目录>
; 说明：安装内容 = 程序 + 种子资源（db/音频），用户数据在首启时复制到 %LocalAppData%\FishWord\，
;       升级覆盖安装不会触碰用户数据。
; make_release.py 会通过 /D 参数传入路径；手动编译时用默认值（开发仓库 dist/ 下）。

#define MyAppName "FishWord"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "FishWord"
#define MyAppExeName "FishWord.App.exe"

#ifndef SourceDir
#define SourceDir "..\..\dist\FishWord-portable"
#endif
#ifndef OutputDir
#define OutputDir "..\..\dist"
#endif

[Setup]
AppId={{8F4E2C91-6B7D-4A1E-9C3F-2D5A7B1E4F08}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\FishWord
DefaultGroupName=FishWord
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=FishWord-setup
SetupIconFile=..\..\src\FishWord.App\Assets\logo.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "立即运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
// 安装文件前把旧版 db 备份为 .legacy-backup：
// 旧版（路径 bug 时代）用户数据可能直接写在安装目录 db（装到 E:\Software 等非保护目录时无 UAC 虚拟化），
// 覆盖安装会替换它——先备份，新版启动时 RecoverLegacyData 检测该备份并自动合并回用户数据副本。
procedure CurStepChanged(CurStep: TSetupStep);
var
  OldDb, Backup: string;
begin
  if CurStep = ssInstall then
  begin
    OldDb := ExpandConstant('{app}\resources\fishword.db');
    Backup := OldDb + '.legacy-backup';
    if FileExists(OldDb) and (not FileExists(Backup)) then
      FileCopy(OldDb, Backup, False);
  end;
end;
