param(
    [string]$Windhawk = (Join-Path $env:ProgramFiles 'Windhawk'),
    [string]$Source = (Join-Path $env:ProgramData 'Windhawk\ModsSource\island-media-controls.wh.cpp')
)

$ErrorActionPreference = 'Stop'
$build = Join-Path (Split-Path $PSScriptRoot) 'build'
New-Item -ItemType Directory -Path $build -Force | Out-Null
$sourceFile = Join-Path $build 'island-media-integrated.wh.cpp'
$binary = Join-Path $build 'island-media-controls_1.0.0_charge-strip-tray-v5.dll'
& (Join-Path $PSScriptRoot 'New-IslandMediaSource.ps1') -Source $Source -Output $sourceFile

$engine = Get-ChildItem -LiteralPath (Join-Path $Windhawk 'Engine') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName '64\windhawk.lib')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$engine) { throw 'The x64 Windhawk engine library was not found.' }
$lib = Join-Path $engine.FullName '64\windhawk.lib'
$response = Join-Path $build 'island-compile.rsp'
$options = @(
    '-std=c++23 -O1 -shared -DUNICODE -D_UNICODE -DWINVER=0x0A00 -D_WIN32_WINNT=0x0A00 -D_WIN32_IE=0x0A00 -DNTDDI_VERSION=0x0A000008 -D__USE_MINGW_ANSI_STDIO=0 -DWH_MOD'
    '-DWH_MOD_ID=L\"island-media-controls\" -DWH_MOD_VERSION=L\"1.0.0\"'
    ('-include windhawk_api.h -target x86_64-w64-mingw32 -Wl,--export-all-symbols -o "' + $binary.Replace('\', '/') + '"')
    ('"' + $lib.Replace('\', '/') + '" "' + $sourceFile.Replace('\', '/') + '"')
    '-lole32 -loleaut32 -lruntimeobject -lwindowsapp -luuid -luser32 -lgdi32 -lmsimg32 -lshlwapi -lwindowscodecs -ldwmapi -ld3d11 -ld2d1 -ldcomp -lcomctl32'
)
[IO.File]::WriteAllLines($response, $options, [Text.UTF8Encoding]::new($false))
& (Join-Path $Windhawk 'Compiler\bin\clang++.exe') "@$response"
if ($LASTEXITCODE -ne 0) { throw "Windhawk compiler exited with code $LASTEXITCODE." }
Write-Output $binary
