; Установщик Dota 2 Discord Rich Presence (Inno Setup 6)
; Сборка: build.ps1 в корне проекта (или ISCC.exe installer\Dota2RPC.iss после dotnet publish)

#define AppName "Dota 2 Discord Rich Presence"
#define AppShortName "Dota 2 RPC"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppExe "Dota2RPC.exe"
#ifndef DiscordClientId
  ; Application ID, вшитый в программу (build.ps1 берёт его из discord-app-id.txt).
  ; Если задан — страница ввода ID не показывается.
  #define DiscordClientId ""
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6D3B7F2A-91C4-4E58-A0D2-5F7C1B9E4A63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Dota2RPC
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} — установщик
DefaultDirName={localappdata}\Programs\Dota2RPC
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
; Установка для текущего пользователя: права администратора не нужны (автозапуск в HKCU, настройки в профиле)
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Setup
SetupIconFile=..\src\Dota2RPC\Resources\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard.bmp,assets\wizard-200.bmp
WizardSmallImageFile=assets\wizard-small.bmp,assets\wizard-small-200.bmp
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
russian.WelcomeLabel2=Программа покажет в Discord ваш текущий матч Dota 2: героя и его иконку, уровень, K/D/A и игровое время.%n%nДанные берутся из официального Game State Integration от Valve — без чтения памяти игры и без риска блокировки.%n%nРекомендуется закрыть Dota 2 перед установкой.

[Tasks]
Name: "auto"; Description: "Запускать вместе с Windows (рекомендуется)"; GroupDescription: "Автозапуск:"; Flags: exclusive
Name: "withdota"; Description: "Запускать вместе с Dota 2 (программа незаметно ждёт запуска игры)"; GroupDescription: "Автозапуск:"; Flags: exclusive unchecked
Name: "noauto"; Description: "Не запускать автоматически"; GroupDescription: "Автозапуск:"; Flags: exclusive unchecked
Name: "launchoption"; Description: "Добавить параметр запуска -gamestateintegration в Steam (Steam может потребоваться закрыть)"; GroupDescription: "Dota 2:"
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Статус Dota 2 в Discord"
Name: "{autoprograms}\{#AppShortName} — повторная настройка"; Filename: "{app}\{#AppExe}"; Parameters: "--setup --gsi=1 --launch-option=1"; Comment: "Переустановить конфигурацию GSI и параметр запуска"
Name: "{autoprograms}\{#AppShortName} — инструкция"; Filename: "{app}\README.md"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--setup --client-id={code:GetClientId} --autostart={code:AutostartFlag} --with-dota={code:WithDotaFlag} --gsi=1 --launch-option={code:LaunchOptionFlag}{code:QuietFlag}"; StatusMsg: "Настройка интеграции с Dota 2 и Discord..."; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppShortName}"; Flags: postinstall nowait skipifsilent
; Обновление из самой программы (/UPDATE=1): установщик тихий, поэтому новую версию запускаем сами
Filename: "{app}\{#AppExe}"; Parameters: "--background --updated"; Flags: nowait; Check: IsSelfUpdate

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; RunOnceId: "Dota2RPCCleanup"; Flags: waituntilterminated runhidden

[Code]
var
  AppIdPage: TInputQueryWizardPage;

procedure OpenPortal(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', 'https://discord.com/developers/applications', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

function IsDigits(const S: String): Boolean;
var
  I: Integer;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
    if (S[I] < '0') or (S[I] > '9') then
    begin
      Result := False;
      Exit;
    end;
end;

{ При переустановке подставляем уже сохранённый Application ID }
function ExistingClientId(): String;
var
  Text: AnsiString;
  Key: String;
  P, I: Integer;
begin
  Result := '';
  if not LoadStringFromFile(ExpandConstant('{userappdata}\Dota2RPC\settings.json'), Text) then
    Exit;
  Key := '"DiscordClientId": "';
  P := Pos(Key, Text);
  if P = 0 then
    Exit;
  I := P + Length(Key);
  while (I <= Length(Text)) and (Text[I] >= '0') and (Text[I] <= '9') do
  begin
    Result := Result + Text[I];
    I := I + 1;
  end;
end;

procedure InitializeWizard;
var
  Button: TNewButton;
begin
  AppIdPage := CreateInputQueryPage(wpSelectTasks,
    'Приложение Discord',
    'Укажите Application ID для Rich Presence',
    'Discord показывает статус от имени зарегистрированного приложения:' + #13#10 +
    '1. Нажмите «Открыть портал Discord» и войдите в свой аккаунт.' + #13#10 +
    '2. New Application → введите имя «Dota 2» → Create.' + #13#10 +
    '3. На странице General Information скопируйте Application ID и вставьте ниже.' + #13#10 + #13#10 +
    'Поле можно оставить пустым и указать ID позже в окне программы.');
  AppIdPage.Add('Application ID:', False);
  AppIdPage.Values[0] := ExistingClientId();

  Button := TNewButton.Create(AppIdPage);
  Button.Parent := AppIdPage.Surface;
  Button.Caption := 'Открыть портал Discord';
  Button.Left := AppIdPage.Edits[0].Left;
  Button.Top := AppIdPage.Edits[0].Top + AppIdPage.Edits[0].Height + ScaleY(12);
  Button.Width := ScaleX(190);
  Button.Height := ScaleY(26);
  Button.OnClick := @OpenPortal;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { ID встроен в программу — пользователю ничего вводить не нужно }
  Result := ('{#DiscordClientId}' <> '') and (PageID = AppIdPage.ID);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Id: String;
begin
  Result := True;
  if CurPageID = AppIdPage.ID then
  begin
    Id := Trim(AppIdPage.Values[0]);
    AppIdPage.Values[0] := Id;
    if Id = '' then
      Result := MsgBox('Application ID не указан — статус в Discord не будет отображаться, пока вы не укажете его в программе.' + #13#10 + #13#10 +
        'Продолжить без него?', mbConfirmation, MB_YESNO) = IDYES
    else if (not IsDigits(Id)) or (Length(Id) < 17) or (Length(Id) > 21) then
    begin
      MsgBox('Application ID состоит только из цифр (обычно 18–19). Скопируйте его на странице General Information в Discord Developer Portal.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

function GetClientId(Param: String): String;
begin
  Result := Trim(AppIdPage.Values[0]);
end;

function AutostartFlag(Param: String): String;
begin
  if WizardIsTaskSelected('auto') then Result := '1' else Result := '0';
end;

function WithDotaFlag(Param: String): String;
begin
  if WizardIsTaskSelected('withdota') then Result := '1' else Result := '0';
end;

function LaunchOptionFlag(Param: String): String;
begin
  if WizardIsTaskSelected('launchoption') then Result := '1' else Result := '0';
end;

function IsSelfUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function QuietFlag(Param: String): String;
begin
  if WizardSilent then Result := ' --quiet' else Result := '';
end;

{ Обновление поверх старой версии: просим работающую программу корректно завершиться (она очистит статус в Discord) }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

{ Перед удалением корректно завершаем программу, иначе её файл будет занят }
function InitializeUninstall(): Boolean;
var
  Exe: String;
  ResultCode: Integer;
begin
  Result := True;
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    if MsgBox('Удалить также настройки и журналы программы?' + #13#10 +
              '(Application ID Discord и параметры отображения)', mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Dota2RPC'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Dota2RPC'), True, True, True);
    end;
  end;
end;
