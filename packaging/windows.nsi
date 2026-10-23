Unicode True
!include "MUI2.nsh"
!include "x64.nsh"
Name "DAYDRIVE"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\DAYDRIVE"
InstallDirRegKey HKCU "Software\DAYDRIVE" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "DAYDRIVE"
VIAddVersionKey "FileDescription" "DAYDRIVE Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "This build of DAYDRIVE requires 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Section "DAYDRIVE"
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}/*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\DAYDRIVE"
  CreateShortcut "$SMPROGRAMS\DAYDRIVE\DAYDRIVE.lnk" "$INSTDIR\DAYDRIVE.exe"
  CreateShortcut "$SMPROGRAMS\DAYDRIVE\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\DAYDRIVE.lnk" "$INSTDIR\DAYDRIVE.exe"
  WriteRegStr HKCU "Software\DAYDRIVE" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "DisplayName" "DAYDRIVE"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "Publisher" "funeralclef"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "DisplayIcon" "$INSTDIR\DAYDRIVE.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "URLInfoAbout" "https://github.com/funeralclef/DAYDRIVE-Public"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\DAYDRIVE.lnk"
  Delete "$SMPROGRAMS\DAYDRIVE\DAYDRIVE.lnk"
  Delete "$SMPROGRAMS\DAYDRIVE\Uninstall.lnk"
  RMDir "$SMPROGRAMS\DAYDRIVE"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DAYDRIVE"
  DeleteRegKey HKCU "Software\DAYDRIVE"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DAYDRIVE"
  ; Only files owned by this installer are removed. User data is kept.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
