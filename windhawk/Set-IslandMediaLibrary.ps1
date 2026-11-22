param([switch]$Restore)

$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an administrator PowerShell.'
}

$key = 'HKLM:\SOFTWARE\Windhawk\Engine\Mods\island-media-controls'
$config = Get-ItemProperty -LiteralPath $key
if ($config.Version -ne '1.0.0') { throw 'Expected installed Island Media Controls 1.0.0.' }

$root = Join-Path (Split-Path $PSScriptRoot) 'build'
$backup = Join-Path $root 'island-media-original-library.txt'
$name = 'island-media-controls_1.0.0_charge-strip.dll'
$libraryDir = Join-Path $env:ProgramData 'Windhawk\Engine\Mods\64'
if ($Restore) {
    $original = [IO.File]::ReadAllText($backup).Trim()
    if ($original -notmatch '^island-media-controls_1\.0\.0_\d+\.dll$' -or
        !(Test-Path -LiteralPath (Join-Path $libraryDir $original))) {
        throw 'Original library was not found.'
    }
    if ($config.LibraryFileName -ne $name) { throw 'The installed mod has changed since integration.' }
    Set-ItemProperty -LiteralPath $key -Name LibraryFileName -Value $original
} else {
    if ($config.LibraryFileName -eq $name) { return }
    if ($config.LibraryFileName -notmatch '^island-media-controls_1\.0\.0_\d+\.dll$') {
        throw 'Unexpected installed library. Existing configuration was left intact.'
    }
    $binary = Join-Path $root $name
    Get-Item -LiteralPath $binary | Out-Null
    [IO.File]::WriteAllText($backup, $config.LibraryFileName)
    Copy-Item -LiteralPath $binary -Destination (Join-Path $libraryDir $name) -Force
    Set-ItemProperty -LiteralPath $key -Name LibraryFileName -Value $name
}
