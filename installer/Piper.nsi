Unicode true
SetCompressor /FINAL /SOLID lzma

!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION is required. Pass /DPRODUCT_VERSION=MAJOR.MINOR.PATCH (see README)."
!endif
!ifndef PUBLISH_DIR
  !error "PUBLISH_DIR is required. Pass /DPUBLISH_DIR=path-to-dotnet-publish-output (see README)."
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required. Pass /DOUTPUT_FILE=path-to-installer.exe (see README)."
!endif

!define PRODUCT_NAME "Piper"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Piper"
!define MULTIUSER_MUI
!define MULTIUSER_EXECUTIONLEVEL Highest
!define MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER
!define MULTIUSER_INSTALLMODE_INSTDIR "Piper"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_KEY "${UNINSTALL_KEY}"
!define MULTIUSER_INSTALLMODE_INSTDIR_REGISTRY_VALUENAME "InstallLocation"

!include "MultiUser.nsh"

!define MUI_ICON "..\src\Piper.App\Assets\piper.ico"
!define MUI_UNICON "..\src\Piper.App\Assets\piper.ico"
!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Piper.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch Piper"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Piper"
Icon "..\src\Piper.App\Assets\piper.ico"
UninstallIcon "..\src\Piper.App\Assets\piper.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MULTIUSER_PAGE_INSTALLMODE
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  !insertmacro MULTIUSER_INIT
FunctionEnd

Function un.onInit
  !insertmacro MULTIUSER_UNINIT
FunctionEnd

Section "Piper" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}\*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Piper"
  CreateShortcut "$SMPROGRAMS\Piper\Piper.lnk" "$INSTDIR\Piper.exe"
  CreateShortcut "$SMPROGRAMS\Piper\Uninstall Piper.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayName" "Piper"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Piper.exe"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr SHCTX "${UNINSTALL_KEY}" "UninstallString" "$\"$INSTDIR\Uninstall.exe$\""
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD SHCTX "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "Desktop shortcut" SecDesktopShortcut
  CreateShortcut "$DESKTOP\Piper.lnk" "$INSTDIR\Piper.exe"
SectionEnd

Section "Uninstall"
  Delete "$DESKTOP\Piper.lnk"
  Delete "$SMPROGRAMS\Piper\Piper.lnk"
  Delete "$SMPROGRAMS\Piper\Uninstall Piper.lnk"
  RMDir "$SMPROGRAMS\Piper"
  DeleteRegKey SHCTX "${UNINSTALL_KEY}"
  RMDir /r "$INSTDIR"
SectionEnd
