!define PRODUCT_NAME "Goink"
!define PRODUCT_VERSION "${VERSION}"
!define EXE_NAME "goink.exe"
!define RUNTIME_DIR "runtime"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "..\..\dist\goink-${PRODUCT_VERSION}-windows-amd64.exe"

RequestExecutionLevel user
InstallDir "D:\Goink"

Function .onInit
    ; 优先选 D 盘，不存在则 E 盘，最后 C 盘
    StrCpy $0 "C"
    StrCpy $1 "D"
    IfFileExists "$1:\" 0 +3
        StrCpy $INSTDIR "$1:\${PRODUCT_NAME}"
        Goto done_drive
    StrCpy $1 "E"
    IfFileExists "$1:\" 0 +3
        StrCpy $INSTDIR "$1:\${PRODUCT_NAME}"
        Goto done_drive
    StrCpy $INSTDIR "$0:\${PRODUCT_NAME}"
    done_drive:
FunctionEnd

Section "Install"
    SetOutPath $INSTDIR

    ; 主程序（每次覆盖）
    File "build\bin\goink.exe"
    File /r "build\runtime\*.*"

    ; 创建空数据目录（仅不存在时）
    CreateDirectory "$INSTDIR\models"
    CreateDirectory "$INSTDIR\novels"

    ; 开始菜单
    CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
    CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\${EXE_NAME}"
    CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk" "$INSTDIR\uninstall.exe"

    ; 卸载程序
    WriteUninstaller "$INSTDIR\uninstall.exe"

    ; 卸载信息
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" \
        "DisplayName" "${PRODUCT_NAME}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" \
        "UninstallString" "$INSTDIR\uninstall.exe"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" \
        "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" \
        "DisplayVersion" "${PRODUCT_VERSION}"
    WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}" \
        "Publisher" "Goink"
SectionEnd

Section "Uninstall"
    Delete "$INSTDIR\${EXE_NAME}"
    RMDir /r "$INSTDIR\${RUNTIME_DIR}"
    Delete "$INSTDIR\uninstall.exe"

    ; 仅删除空目录（用户数据目录不为空时会保留）
    RMDir "$INSTDIR\models"
    RMDir "$INSTDIR\novels"
    RMDir "$INSTDIR"

    Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
    Delete "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall.lnk"
    RMDir "$SMPROGRAMS\${PRODUCT_NAME}"

    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
SectionEnd
