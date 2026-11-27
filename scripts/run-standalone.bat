@echo off
rem Folderss 없이 Podman 모니터만 단독으로 띄운다. 빌드 후 실행.
rem 사용: run-standalone.bat [--theme Black^|Light^|Catppuccin^|Dracula^|GitHub^|Nord^|Solarized]
chcp 65001 >nul
setlocal
set "PROJ=%~dp0..\tools\PodmanMonitor.Standalone\PodmanMonitor.Standalone.csproj"
set "EXE=%~dp0..\tools\PodmanMonitor.Standalone\bin\Release\net8.0-windows\PodmanMonitorStandalone.exe"

dotnet build "%PROJ%" -c Release -v q -nologo
if errorlevel 1 (
    echo.
    echo 빌드에 실패했습니다.
    pause
    exit /b 1
)

start "" "%EXE%" %*
endlocal
