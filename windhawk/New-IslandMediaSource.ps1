param(
    [Parameter(Mandatory)] [string]$Source,
    [Parameter(Mandatory)] [string]$Output
)

$ErrorActionPreference = 'Stop'
$sourceText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Source)).Replace("`r`n", "`n")
if ($sourceText -notmatch '(?m)^// @id\s+island-media-controls\s*$' -or
    $sourceText -notmatch '(?m)^// @version\s+1\.0\.0\s*$' -or
    $sourceText.Contains('static std::wstring ChargeStripReadings()')) {
    throw 'Use the original Island Media Controls 1.0.0 source.'
}

function Insert-Once([string]$Text, [string]$Anchor, [string]$Replacement) {
    $index = $Text.IndexOf($Anchor, [StringComparison]::Ordinal)
    if ($index -lt 0 -or $Text.IndexOf($Anchor, $index + $Anchor.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected exactly one source anchor: $Anchor"
    }
    $Text.Substring(0, $index) + $Replacement + $Text.Substring($index + $Anchor.Length)
}

$additions = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'IslandReadings.inc'))
$anchor = 'void UpdateRuntimeLayout(HWND hwnd,'
$sourceText = Insert-Once $sourceText $anchor "$additions`n$anchor"
$anchor = '    g_layout.compactWidth = compactWidth;'
$sourceText = Insert-Once $sourceText $anchor "    if (!IsVerticalTaskbar() && !HideCompactTrackInfo() && !ChargeStripReadings().empty()) compactWidth += 108;`n$anchor"
$anchor = '    mainIsland.Children().Append(content);'
$sourceText = Insert-Once $sourceText $anchor "    if (!IsVerticalTaskbar() && !HideCompactTrackInfo()) AddChargeStripReadings(content);`n$anchor"
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
[IO.File]::WriteAllText($outputPath, $sourceText, [Text.UTF8Encoding]::new($false))
