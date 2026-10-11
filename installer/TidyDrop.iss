#ifndef AppVersion
  #define AppVersion "0.6.1"
#endif
#ifndef AppSource
  #define AppSource "..\artifacts\installer-app"
#endif
#ifndef Bootstrapper
  #define Bootstrapper "..\artifacts\tools\MicrosoftEdgeWebview2Setup.exe"
#endif

[Setup]
AppId={{328142DD-27C0-46AA-8265-68070462868A}
AppName=归序
AppVersion={#AppVersion}
AppVerName=归序 {#AppVersion}
AppPublisher=TidyDrop
AppPublisherURL=https://github.com/ly20030823/tidydrop
AppSupportURL=https://github.com/ly20030823/tidydrop/issues
AppUpdatesURL=https://github.com/ly20030823/tidydrop/releases
DefaultDirName={localappdata}\Programs\TidyDrop
DefaultGroupName=归序
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=TidyDrop-Setup-{#AppVersion}-win-x64
SetupIconFile=..\src\TidyDrop.App\Assets\TidyDrop.ico
UninstallDisplayIcon={app}\TidyDrop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
AppMutex=Local\TidyDrop.App.Active
CloseApplications=no
RestartApplications=no
SetupLogging=yes
DisableWelcomePage=no
UninstallDisplayName=归序 · TidyDrop

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面入口（拖入文件或文件夹即可收纳）"; GroupDescription: "快捷方式："
Name: "copypath"; Description: "添加一级右键菜单「复制文件路径」（需要管理员确认本机签名信任）"; GroupDescription: "可选功能："; Flags: unchecked

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Excludes: "data\*,smoke*,*.pdb,*.pfx,*.p12"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Bootstrapper}"; Flags: dontcopy
Source: "ChineseSimplified-LICENSE.txt"; DestDir: "{app}\LICENSES"; DestName: "Inno-ChineseSimplified.txt"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\归序"; Filename: "{app}\TidyDrop.exe"; WorkingDir: "{app}"; Comment: "拖入文件或文件夹，后台收纳；双击查看归档空间"
Name: "{userdesktop}\归序"; Filename: "{app}\TidyDrop.exe"; WorkingDir: "{app}"; Comment: "拖入文件或文件夹，后台收纳；双击查看归档空间"; Tasks: desktopicon

[Run]
Filename: "{app}\TidyDrop.exe"; Description: "打开归序，选择归档总文件夹"; Flags: postinstall nowait skipifsilent

[Code]
const
  WebViewKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
var
  SignaturePage: TOutputMsgWizardPage;

function HasWebView: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebViewKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure InitializeWizard;
begin
  SignaturePage := CreateOutputMsgPage(wpSelectTasks, '右键菜单与本地签名', '请了解可选功能对系统的影响',
    '归序目前使用个人开发签名。若勾选「复制文件路径」菜单，安装时会请求管理员确认，将随附的公开代码签名证书加入本机「受信任人」证书库。' + #13#10#13#10 +
    '这份签名信任对电脑上的所有账户生效，也适用于同一私钥签名的其他安装包；不会加入根证书库。菜单仅添加到当前账户。' + #13#10#13#10 +
    '移除菜单或卸载时会请求清理归序新增的证书信任。其他账户仍在使用菜单时保留。取消管理员确认不会影响归序的文件收纳功能。' + #13#10#13#10 +
    '程序已自带 .NET 运行环境。若系统缺少 WebView2，安装时需要联网从微软下载。卸载会保留设置、整理记录和所有归档材料。');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SignaturePage.ID) and not WizardIsTaskSelected('copypath');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
  if not HasWebView then begin
    ExtractTemporaryFile('MicrosoftEdgeWebview2Setup.exe');
    if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := '无法启动微软 WebView2 安装器，请确认联网后重试。'
    else if not HasWebView then
      Result := 'WebView2 尚未安装完成（返回码 ' + IntToStr(ExitCode) + '）。请确认联网后重试。';
  end;
end;

function RunMenu(Action: String; Consent: Boolean): Boolean;
var
  Arguments: String;
  ExitCode: Integer;
  Output: TExecOutput;
  Line: Integer;
  PreviousRedirection: Boolean;
begin
  Arguments := '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Shell\context-menu.ps1') + '" -Action ' + Action;
  if Consent then Arguments := Arguments + ' -TrustConsent';
  if Action = 'remove' then Arguments := Arguments + ' -IfBoundHere';
  // Launch the native System32 image without a Sysnative alias that would break PSHOME in the child.
  PreviousRedirection := EnableFsRedirection(False);
  try
    Result := ExecAndCaptureOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode, Output);
  finally
    EnableFsRedirection(PreviousRedirection);
  end;
  for Line := 0 to GetArrayLength(Output.StdOut) - 1 do Log(Output.StdOut[Line]);
  for Line := 0 to GetArrayLength(Output.StdErr) - 1 do Log(Output.StdErr[Line]);
  Log('Context menu ' + Action + ': launched=' + IntToStr(Ord(Result)) + ', result=' + IntToStr(ExitCode));
  if Result then Result := ExitCode = 0;
end;

function MenuBoundHere: Boolean;
var
  Location: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\TidyDrop\CopyPathMenu', 'ExternalLocation', Location) and
    (CompareText(Location, ExpandConstant('{app}')) = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (WizardIsTaskSelected('copypath') or MenuBoundHere) then
    if not RunMenu('install', WizardIsTaskSelected('copypath')) then
      SuppressibleMsgBox('归序已安装，但右键菜单尚未启用。管理员确认可能被取消，也可能是系统阻止了注册。你可以稍后在「归档设置」里重新添加。', mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and FileExists(ExpandConstant('{app}\Shell\context-menu.ps1')) then
    if not RunMenu('remove', False) then
      RaiseException('右键菜单或归序新增的签名信任尚未清理。卸载已暂停，请完成管理员确认后重试；也可以先在归序设置中移除菜单。');
end;
