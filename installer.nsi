!define APPNAME "Icom Web Control"
!define COMPANY "MM5AGM"
!define VERSION "1.0.6"
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

    ; Start menu entry goes straight into Programs, so that typing "Icom" into
    ; Start finds it. Up to v1.0.4 the only entry lived in a folder named after
    ; the publisher, which sorts the app under M for MM5AGM and hides it from
    ; anyone who searches for it by name -- reported after a v1.0.4 install as
    ; "no entry in my start menu" when the shortcut was in fact there.
    CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\Icom_Web_Control.exe"

    ; Remove the pre-1.0.5 entry so an upgrade leaves exactly one, not two.
    ; RMDir without /r deletes the folder only if it is now empty, which is
    ; what we want -- Yaesu Web Control keeps its own shortcut in there.
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    ; NSIS itself is a 32-bit process, so an unqualified HKLM write lands in
    ; Wow6432Node -- the wrong view for a 64-bit-only app. Windows shows both in
    ; Apps & features, so this was never visible, but the entry belongs in the
    ; 64-bit view. Clear the old 32-bit key first or an upgrade leaves two.
    SetRegView 32
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
    SetRegView 64

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
    Delete "$SMPROGRAMS\${APPNAME}.lnk"
    ; Pre-1.0.5 location. Still removed here so uninstalling an old install
    ; leaves nothing behind; the folder itself only goes if it is empty.
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"
    RMDir /r "$INSTDIR"
    ; Both views: 1.0.5 and later write the 64-bit one, earlier versions the
    ; 32-bit one, and an uninstaller built by either may run against either.
    SetRegView 32
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
    SetRegView 64
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
SectionEnd
