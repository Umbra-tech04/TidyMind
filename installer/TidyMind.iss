; TidyMind installer (Inno Setup 6). Build it with build-installer.ps1, which publishes the app and passes AppVersion
; and PublishDir. Per-user, no admin rights. The data folder (%LOCALAPPDATA%\TidyMind) is never touched, except when
; the user asks the uninstaller to delete it.

#define MyAppName "TidyMind"
#define MyAppExeName "TidyMind.exe"
#define MyAppPublisher "Umbra-tech04"
#define MyAppURL "https://github.com/Umbra-tech04/TidyMind"
; Windows shows reminder notifications only from a sender a Start Menu shortcut names: this ID must be the app's
; App.UserModelId (TidyMind/App.xaml.cs). The CLSID keeps them in the Action Center once the banner has gone; TidyMind
; registers nothing for it, so a click isn't handled by TidyMind. Never change either: they are how Windows knows the
; installed app.
#define MyAppUserModelId "TidyMind.App"
#define MyToastActivatorCLSID "2A3B2680-8A8E-477E-B156-973089BA7F0A"
; The running app holds this mutex (App.ClaimSingleInstance; its OnExit releases it).
#define MyAppMutex "Local\TidyMind_SingleInstance"

#ifndef AppVersion
  #error AppVersion is not set: build with installer\build-installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir is not set: build with installer\build-installer.ps1
#endif

[Setup]
; Never change AppId: upgrades and the uninstaller find the existing install by it.
AppId={{F2611D97-83A8-4144-B007-40714887191F}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
SetupIconFile=..\TidyMind\tidymind-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
LicenseFile=TERMS.txt
InfoBeforeFile=PRIVACY.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Setup and uninstall ask to close TidyMind first (an in-app update waits for it to close instead: InitializeSetup).
AppMutex={#MyAppMutex}
CloseApplications=yes
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename=TidyMind-Setup-{#AppVersion}
; The wizard follows the Windows display language: Hungarian gets Hungarian, everything else English. Inno Setup
; ships no Serbian (Latin) messages file, so Serbian is not offered. TERMS.txt and PRIVACY.txt are English only (the
; Hungarian labels say so).
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "hungarian"; MessagesFile: "compiler:Languages\Hungarian.isl"

[Messages]
english.WizardLicense=Terms
english.LicenseLabel=Please read the terms before installing.
english.LicenseLabel3=TidyMind is provided as is. You need to accept these terms to install it.
english.LicenseAccepted=I &accept the terms
english.LicenseNotAccepted=I &do not accept the terms
english.WizardInfoBefore=Privacy
english.InfoBeforeLabel=How TidyMind handles your data.
english.InfoBeforeClickLabel=When you are ready to continue, click Next.
hungarian.WizardLicense=Feltételek
hungarian.LicenseLabel=A telepítés feltételei.
hungarian.LicenseLabel3=A TidyMind „ahogy van” alapon érhető el. A telepítéshez el kell fogadni ezeket a feltételeket (a szöveg angol nyelvű).
hungarian.LicenseAccepted=&Elfogadom a feltételeket
hungarian.LicenseNotAccepted=&Nem fogadom el a feltételeket
hungarian.WizardInfoBefore=Adatvédelem
hungarian.InfoBeforeLabel=Hogyan kezeli a TidyMind az adatokat (a szöveg angol nyelvű).

[CustomMessages]
english.DataFolderTaken=This folder holds your TidyMind data. Please choose another folder for the program.
english.DeleteDataPrompt=Also delete your TidyMind data?%n%nThis deletes all your memories, projects, collections, notes, reminders and settings in:%n%1%n%nBackups you saved with TidyMind in another folder are not affected.%n%nChoose No to keep your data, for example to install TidyMind again later.
english.DeleteDataFailed=Some of your TidyMind data couldn't be deleted. You can delete this folder yourself:%n%1
hungarian.DataFolderTaken=Ez a mappa a TidyMind adatait tartalmazza. A programot egy másik mappába kell telepíteni.
hungarian.DeleteDataPrompt=A TidyMind adatai is törlődjenek?%n%nEz törli az összes memóriát, projektet, gyűjteményt, jegyzetet, emlékeztetőt és beállítást ebben a mappában:%n%1%n%nA TidyMinddal más mappába mentett biztonsági mentéseket ez nem érinti.%n%nA Nem gombbal az adatok megmaradnak, például egy későbbi újratelepítéshez.
hungarian.DeleteDataFailed=A TidyMind adatainak egy része nem törölhető. Ez a mappa kézzel is törölhető:%n%1

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The whole publish folder (exe, runtime, dlls, resources, THIRD-PARTY-NOTICES.txt).
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Written again on every install, so installing over an older version also gives its shortcut the IDs. The
; uninstaller deletes both shortcuts.
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelId}"; AppUserModelToastActivatorCLSID: "{#MyToastActivatorCLSID}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelId}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  // /RELAUNCH=1: started by TidyMind's in-app update (Updates.InstallerArguments, with /SILENT): wait for TidyMind to
  // close, and start it again at the end. A normal install keeps the "Launch TidyMind" checkbox instead.
  Relaunch: Boolean;
  Installed: Boolean;

function DataFolder: String;
begin
  Result := ExpandConstant('{localappdata}\{#MyAppName}');
end;

// A reminder process (TidyMind.exe --remind ...) shows its notification and exits within seconds, but one still
// running would hold the program files. The main app is covered by AppMutex.
procedure StopReminderProcesses;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-CimInstance Win32_Process | Where-Object { $_.Name -eq ''{#MyAppExeName}'' '
    + '-and $_.CommandLine -like ''*--remind *'' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// TidyMind starts the installer and then closes the normal way (everything saved, the mutex released). Setup checks
// AppMutex right after this; with /SUPPRESSMSGBOXES it would give up at once, so it waits here first (up to a minute).
function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Relaunch := ExpandConstant('{param:RELAUNCH|0}') = '1';
  if Relaunch then
  begin
    Waited := 0;
    while CheckForMutexes('{#MyAppMutex}') and (Waited < 600) do
    begin
      Sleep(100);
      Waited := Waited + 1;
    end;
    Log(Format('In-app update: waited %d ms for TidyMind to close.', [Waited * 100]));
  end;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssDone then
    Installed := True;
end;

// After an in-app update TidyMind starts again: the new version, or the old one if setup failed (setup puts back what
// it replaced). If setup stopped before it knew the program folder (TidyMind never closed), there's nothing to start.
procedure DeinitializeSetup;
var
  Exe: String;
  ResultCode: Integer;
begin
  if not Relaunch then
    exit;
  try
    Exe := ExpandConstant('{app}\{#MyAppExeName}');
  except
    Log('In-app update: the program folder is unknown; TidyMind is not started again.');
    exit;
  end;
  if not Installed then
    Log('In-app update: setup did not finish; starting the version that is still installed.');
  if not FileExists(Exe) or not ExecAsOriginalUser(Exe, '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResultCode) then
    Log('In-app update: TidyMind could not be started again (' + Exe + ').');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopReminderProcesses;
  Result := '';
end;

// The program must not go into the data folder: the two are kept apart, so upgrades and uninstalls never touch data.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir)
    and (Pos(AddBackslash(Uppercase(DataFolder)), AddBackslash(Uppercase(WizardDirValue))) = 1) then
  begin
    MsgBox(CustomMessage('DataFolderTaken'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Data: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopReminderProcesses;
    // The app deletes its own TidyMind_Reminder_* tasks, before its files go. If that fails, uninstalling carries on.
    if not Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--remove-reminder-tasks', '', SW_HIDE,
                ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      Log('Removing the reminder tasks failed (' + IntToStr(ResultCode) + ').');
  end;

  // Windows keeps TidyMind's notification preferences (Settings > Notifications) under its ID once one was shown;
  // they go with the app.
  if CurUninstallStep = usPostUninstall then
    RegDeleteKeyIncludingSubkeys(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{#MyAppUserModelId}');

  // Only when asked, and only that exact folder. A silent uninstall always keeps the data.
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    Data := DataFolder;
    if (ExpandConstant('{localappdata}') <> '') and DirExists(Data)
      and (MsgBox(FmtMessage(CustomMessage('DeleteDataPrompt'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      if not DelTree(Data, True, True, True) then
        MsgBox(FmtMessage(CustomMessage('DeleteDataFailed'), [Data]), mbError, MB_OK);
    end;
  end;
end;
