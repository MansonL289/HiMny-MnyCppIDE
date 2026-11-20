; MnyC++ IDE 安装包脚本（Inno Setup 6）
; 用法：先运行 dotnet publish 生成发布目录，再用 Inno Setup 编译本文件
;   dotnet publish WinUI3Shell\WinUI3Shell.csproj -c Release -p:Platform=x64 -p:SelfContained=true -p:WindowsAppSDKSelfContained=true
;   然后把 publish 目录同步到下面的 PublishDir：
;   robocopy "WinUI3Shell\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish" "d:\0Mny\Tools\pub-cpp" /MIR

#define AppName "MnyC++ IDE"
#define AppVersion "内测-20261118"
#define AppPublisher "MNY"
#define AppURL "https://github.com/MansonL289/HiMny-MnyCppIDE"
#define AppExeName "MnyMinecraftLauncher.exe"

; 微软官方 WebView2 Evergreen 引导安装器（约 1.9MB，联网后拉取最新运行时）
; 需与本 iss 同目录；丢失时用 https://go.microsoft.com/fwlink/p/?LinkId=2124703 重新下载
#define WebView2Setup "MicrosoftEdgeWebView2Setup.exe"

; 注意：Inno Setup 不认路径里的 [MNY] 方括号，所以发布文件和图标都从无括号的中转目录取
; Tools\pub-cpp 是 publish 目录的副本（编译前用上面的 robocopy 刷新）
#define PublishDir "d:\0Mny\Tools\pub-cpp"
#define IconFile PublishDir + "\Assets\AppIcon.ico"

; 应用唯一标识，生成后不要再改，否则卸载/升级会错乱
#define AppId "{{B7E4C1D2-9A3F-4C58-8E21-6F0D3A5B7C94}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
; 开始菜单统一放在 MnyApp 文件夹下，与 MnyMinecraftLauncher 共用一个程序组
DefaultGroupName=MnyApp
DisableProgramGroupPage=yes
OutputDir=d:\0Mny
OutputBaseFilename=MnyCppIDESetup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; 装/卸前自动关闭正在运行的程序（Restart Manager），避免文件被占用装出半新半旧
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no
; 同一 AppId 升级时沿用旧安装目录（默认即 yes，这里显式写明）
UsePreviousAppDir=yes

[InstallDelete]
; 安装前清空程序目录里的旧文件，避免旧 dll 残留
; 用户设置保存在 %LOCALAPPDATA%\MnyC++，不在这里，不受影响
Type: filesandordirs; Name: "{app}\*"
; 清理旧版本遗留在独立程序组里的快捷方式（升级到 MnyApp 分组后不再需要）
Type: files; Name: "{commonprograms}\{#AppName}\{#AppName}.lnk"
Type: files; Name: "{commonprograms}\{#AppName}\卸载 {#AppName}.lnk"
Type: dirifempty; Name: "{commonprograms}\{#AppName}"

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; WebView2 引导安装器：仅在系统未安装运行时时才复制，装完自动删除
Source: "{#WebView2Setup}"; DestDir: "{app}"; Flags: ignoreversion deleteafterinstall; Check: not WebView2Installed

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; 先补装 WebView2 运行时（静默、等待完成），再启动程序
Filename: "{app}\{#WebView2Setup}"; Parameters: "/silent /install"; StatusMsg: "正在安装 Microsoft Edge WebView2 运行时…"; Flags: waituntilterminated runhidden; Check: not WebView2Installed
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DeleteUserData: Boolean;

{ ---- WebView2 运行时检测 ---- }
const
  WebView2ClientKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function ReadWebView2Version(RootKey: Integer; const SubKey: string): string;
var
  Value: string;
begin
  Result := '';
  if RegQueryStringValue(RootKey, SubKey, 'pv', Value) then
    Result := Trim(Value);
end;

{ 系统是否已安装 WebView2 运行时：机器级 / 32 位视图 / 当前用户，任一命中即视为已装 }
function WebView2Installed(): Boolean;
begin
  Result :=
    (ReadWebView2Version(HKEY_LOCAL_MACHINE, WebView2ClientKey) <> '') or
    (ReadWebView2Version(HKEY_LOCAL_MACHINE, 'Software\WOW6432Node\' + WebView2ClientKey) <> '') or
    (ReadWebView2Version(HKEY_CURRENT_USER, WebView2ClientKey) <> '');
end;

{ 卸载前询问：是否一并删除用户设置（默认不勾选） }
function InitializeUninstall(): Boolean;
var
  Form: TSetupForm;
  Hint: TNewStaticText;
  Check: TNewCheckBox;
  OkButton, CancelButton: TNewButton;
begin
  DeleteUserData := False;
  Result := True;

  Form := CreateCustomForm(ScaleX(440), ScaleY(190), False, True);
  try
    Form.Caption := '卸载 {#AppName}';

    Hint := TNewStaticText.Create(Form);
    Hint.Parent := Form;
    Hint.Left := ScaleX(20);
    Hint.Top := ScaleY(20);
    Hint.Width := ScaleX(400);
    Hint.Height := ScaleY(52);
    Hint.AutoSize := False;
    Hint.WordWrap := True;
    Hint.Caption := '卸载只会删除程序本身，编辑器设置、字体、编译选项等用户设置会保留。' +
      '如需一并清除，请勾选下面的选项（不可恢复）。';

    Check := TNewCheckBox.Create(Form);
    Check.Parent := Form;
    Check.Left := ScaleX(20);
    Check.Top := ScaleY(86);
    Check.Width := ScaleX(400);
    Check.Caption := '清除所有设置（删除编辑器配置、界面缩放等用户设置）';
    Check.Checked := False;

    OkButton := TNewButton.Create(Form);
    OkButton.Parent := Form;
    OkButton.Caption := '卸载';
    OkButton.ModalResult := mrOk;
    OkButton.Default := True;
    OkButton.Top := ScaleY(140);
    OkButton.Height := ScaleY(32);

    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form;
    CancelButton.Caption := '取消';
    CancelButton.ModalResult := mrCancel;
    CancelButton.Cancel := True;
    CancelButton.Top := ScaleY(140);
    CancelButton.Height := ScaleY(32);

    OkButton.Width := Form.CalculateButtonWidth([OkButton.Caption, CancelButton.Caption]);
    CancelButton.Width := OkButton.Width;
    OkButton.Left := Form.ClientWidth - ScaleX(20) - OkButton.Width * 2 - ScaleX(8);
    CancelButton.Left := Form.ClientWidth - ScaleX(20) - CancelButton.Width;

    Form.ActiveControl := Check;
    Form.FlipAndCenterIfNeeded(True, nil, False);

    Result := Form.ShowModal() = mrOk;
    if Result then
      DeleteUserData := Check.Checked;
  finally
    Form.Free();
  end;
end;

{ 卸载完成后按用户选择清理设置目录 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if not DeleteUserData then
    Exit;

  DataDir := ExpandConstant('{localappdata}\MnyC++');
  if DirExists(DataDir) then
    DelTree(DataDir, True, True, True);
end;

{ 装完仍检测不到 WebView2 运行时：给出明确提示（静默安装不打扰）
  ssDone 在 [Run] 段执行完之后才触发，避免在引导安装器运行前误报 }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssDone) and (not WizardSilent) and (not WebView2Installed()) then
    MsgBox('未检测到 Microsoft Edge WebView2 运行时，编辑器与刷题功能需要它才能使用。' + #13#10 +
      '请联网后重新运行安装包，或到 https://developer.microsoft.com/microsoft-edge/webview2/ 手动安装后重启程序。',
      mbInformation, MB_OK);
end;
