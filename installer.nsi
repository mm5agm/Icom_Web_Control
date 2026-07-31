!define APPNAME "Icom Web Control"
!define COMPANY "MM5AGM"
!define VERSION "0.1.0-alpha"
!define INSTALLDIR "$PROGRAMFILES64\${COMPANY}\${APPNAME}"
Name "${APPNAME} ${VERSION}"
OutFile "Icom_Web_Control_Setup.exe"
InstallDir "${INSTALLDIR}"

RequestExecutionLevel admin

Page directory
Page instfiles

Section "Install"
    ; Stop any running instance of IWC before copying files. Without this,
    ; an upgrade install on top of a running IWC fails with NSIS's "Error
    ; opening file for writing" on every locked DLL. /F is the force flag;
    ; if the process isn't running, taskkill exits non-zero but ExecWait
    ; doesn't check the return code, so missing-process is harmless. The
    ; Sleep gives Windows a moment to release the file handles before the
    ; File copy begins.
    ExecWait 'taskkill /F /IM Icom_Web_Control.exe'
    Sleep 1500

    SetOutPath "$INSTDIR"

    ; Exclude files that must not be shipped or must not overwrite user data.
    ; The build-installer.ps1 script removes these before NSIS runs;
    ; the /x flags here are a belt-and-braces safety net.
    File /r \
        /x "*.pdb" \
        /x "libman.json" \
        /x "web.config" \
        /x "radio_state.json" \
        /x "appsettings.user.json" \
        "publish\*"

    CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\Icom_Web_Control.exe"
    CreateDirectory "$SMPROGRAMS\${COMPANY}"
    CreateShortCut "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk" "$INSTDIR\Icom_Web_Control.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} ${VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayIcon" "$INSTDIR\Icom_Web_Control.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${COMPANY}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${VERSION}"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" 65000
SectionEnd

Section "Uninstall"
    ; Stop the app if it is running before deleting files
    ExecWait 'taskkill /F /IM Icom_Web_Control.exe'
    Sleep 1500

    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"
    RMDir /r "$INSTDIR"
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
SectionEnd
