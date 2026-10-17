#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\stage\setup"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define RuntimePage "https://dotnet.microsoft.com/download/dotnet/10.0"

[Setup]
AppId={{08F1A697-16E6-4301-9340-4CCEBE34D4D8}
AppName=KebuzReplay
AppVersion={#AppVersion}
AppVerName=KebuzReplay {#AppVersion}
AppPublisher=Kebuzya
AppPublisherURL=https://github.com/Kebuzya/KebuzReplay
AppSupportURL=https://github.com/Kebuzya/KebuzReplay/issues
AppUpdatesURL=https://github.com/Kebuzya/KebuzReplay/releases
DefaultDirName={localappdata}\Programs\KebuzReplay
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=KebuzReplay-Setup-win-x64
SetupIconFile=..\Kebuz.App\KebuzRecord.ico
UninstallDisplayIcon={app}\KebuzReplay.exe
UninstallDisplayName=KebuzReplay
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.DesktopIcon=Значок на рабочем столе
english.DesktopIcon=Desktop shortcut
russian.RuntimeTitle=Нужен .NET 10 Desktop Runtime
english.RuntimeTitle=.NET 10 Desktop Runtime is required
russian.RuntimeDownloading=Скачивается .NET 10 Desktop Runtime от Microsoft
english.RuntimeDownloading=Downloading .NET 10 Desktop Runtime from Microsoft
russian.RuntimeFailed=.NET 10 Desktop Runtime не установился. Программа без него не запустится. Его можно поставить вручную: {#RuntimePage}%n%nПродолжить установку?
english.RuntimeFailed=.NET 10 Desktop Runtime was not installed, and the program will not start without it. It can be installed by hand: {#RuntimePage}%n%nContinue the installation?
russian.Launch=Запустить KebuzReplay
english.Launch=Start KebuzReplay

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\KebuzReplay"; Filename: "{app}\KebuzReplay.exe"
Name: "{autodesktop}\KebuzReplay"; Filename: "{app}\KebuzReplay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\KebuzReplay.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[Code]
var
  RuntimePage: TDownloadWizardPage;

function HasDesktopRuntime: Boolean;
var
  Found: TFindRec;
begin
  Result := False;

  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), Found) then
  begin
    try
      repeat
        if Found.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          Result := True;
      until Result or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

procedure StopRunningCopy(Path: String);
var
  Code: Integer;
begin
  if FileExists(Path) then
    Exec(Path, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure InitializeWizard;
begin
  RuntimePage := CreateDownloadPage(CustomMessage('RuntimeTitle'), CustomMessage('RuntimeDownloading'), nil);
end;

function InstallRuntime: Boolean;
var
  Code: Integer;
begin
  Result := False;
  RuntimePage.Clear;
  RuntimePage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-10-win-x64.exe', '');
  RuntimePage.Show;

  try
    try
      RuntimePage.Download;
      Result := ShellExec('', ExpandConstant('{tmp}\windowsdesktop-runtime-10-win-x64.exe'), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code)
        and ((Code = 0) or (Code = 3010) or (Code = 1638));
    except
      Log(GetExceptionMessage);
    end;
  finally
    RuntimePage.Hide;
  end;

  Result := Result and HasDesktopRuntime;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (CurPageID = wpReady) and not HasDesktopRuntime then
  begin
    if not InstallRuntime then
      Result := MsgBox(CustomMessage('RuntimeFailed'), mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningCopy(ExpandConstant('{app}\KebuzReplay.exe'));
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopRunningCopy(ExpandConstant('{app}\KebuzReplay.exe'));
  Result := True;
end;
