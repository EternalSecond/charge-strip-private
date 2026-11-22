@echo off
setlocal
set "compiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "output=%~dp0build"
if /i "%~1"=="/package" (
    set "output=%~dp0build\package"
    goto build
)
if /i "%~1"=="/build" goto build
if exist "%output%\ChargeStrip.exe" goto launch

:build
if not exist "%output%" mkdir "%output%"
"%compiler%" /nologo /target:winexe /platform:x64 /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll" /out:"%output%\ChargeStrip.exe" "%~dp0ChargeStrip.cs" "%~dp0Batteries.cs" "%~dp0Settings.cs" "%~dp0Localize.cs" "%~dp0PcResources.cs" "%~dp0MediaSessions.cs" "%~dp0MediaPanel.cs" "%~dp0ModuleStrip.cs"
if errorlevel 1 (
    if /i not "%~1"=="/package" pause
    exit /b 1
)
if /i "%~1"=="/package" goto package

:launch
if /i "%~1"=="/settings" (
    start "" "%output%\ChargeStrip.exe" --settings
) else (
    start "" "%output%\ChargeStrip.exe"
)
exit /b 0

:package
set "CHARGE_STRIP_PACKAGE_DIR=%output%"
set "CHARGE_STRIP_PROJECT_DIR=%~dp0"
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $dir=$env:CHARGE_STRIP_PACKAGE_DIR; $root=$env:CHARGE_STRIP_PROJECT_DIR; $zip=Join-Path $root 'build\Charge-Strip-Windows-x64.zip'; $files=@((Join-Path $dir 'ChargeStrip.exe'),(Join-Path $root 'Open Settings.cmd'),(Join-Path $root 'README.md'),(Join-Path $root 'CHANGELOG.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'docs')); Compress-Archive -LiteralPath $files -DestinationPath $zip -Force; $sha=(Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant(); Set-Content -LiteralPath (Join-Path $root 'build\SHA256SUMS.txt') -Value ($sha+' *Charge-Strip-Windows-x64.zip') -Encoding ascii; Write-Output $zip"
exit /b %errorlevel%
