; Установщик Beg1nnnerOS (Inno Setup 6): https://jrsoftware.org/isinfo.php
; Сборка: build.bat (сначала dotnet publish в папку publish, затем этот скрипт).

#ifndef AppVersion
  #define AppVersion "2.1.4"
#endif
#define AppName "Beg1nnnerOS"
#define AppExe "Beg1nnnerOS.exe"

[Setup]
AppId={{6F3C2B8E-4A1D-4E7B-9C35-B1A9E0D24F17}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Beg1nnnerOS
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; По умолчанию — установка только для текущего пользователя (без прав администратора);
; в начале установки можно выбрать «для всех пользователей».
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=Beg1nnnerOS-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Программа создаёт мьютекс с этим именем — установщик попросит закрыть её перед обновлением
AppMutex=Beg1nnnerOS-single-instance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Установка Microsoft Edge WebView2 Runtime…"; Flags: waituntilterminated; Check: ShouldInstallWebView2
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
; Тихая установка = обновление из самой программы: после неё программа запускается сама
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

var
  DownloadPage: TDownloadWizardPage;

function HasVersion(RootKey: Integer; const SubKey: String): Boolean;
var
  V: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', V) and (V <> '') and (V <> '0.0.0.0');
end;

{ Окно программы показывается через WebView2 — проверяем, установлен ли он }
function WebView2Installed: Boolean;
begin
  Result := HasVersion(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2Key)
    or HasVersion(HKLM, 'SOFTWARE\' + WebView2Key)
    or HasVersion(HKCU, 'Software\' + WebView2Key);
end;

function ShouldInstallWebView2: Boolean;
begin
  Result := (not WebView2Installed) and FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'));
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (not WebView2Installed) then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if not DownloadPage.AbortedByUser then
          SuppressibleMsgBox('Не удалось скачать Microsoft Edge WebView2 Runtime: ' + GetExceptionMessage + #13#10 +
            'Программа установится, но для её окна WebView2 нужно будет поставить вручную.',
            mbInformation, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ При удалении — по желанию удалить настройки, входы Twitch, алерты и статистику }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Beg1nnnerOS');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox('Удалить также настройки и данные Beg1nnnerOS?' + #13#10 +
                '(команды, таймеры, алерты, входы Twitch, статистика)' + #13#10#13#10 + DataDir,
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
