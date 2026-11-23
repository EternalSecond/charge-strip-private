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
$anchor = 'void SyncIslandOverlayVisibility() {'
$sourceText = Insert-Once $sourceText $anchor "[[clang::no_destroy]] static StackPanel chargeStripItems{nullptr};`nstatic constexpr double chargeStripWidth = 122.0;`nstatic constexpr double chargeStripReservation = 116.0;`nstatic bool ChargeStripVisible() { return chargeStripItems && chargeStripItems.Visibility() == Visibility::Visible; }`n`n$anchor"
$anchor = '        g_playerGrid.Visibility() == xaml::Visibility::Visible && g_playerGrid.Opacity() > 0.001;'
$sourceText = Insert-Once $sourceText $anchor '        (ChargeStripVisible() || (g_playerGrid.Visibility() == xaml::Visibility::Visible && g_playerGrid.Opacity() > 0.001));'
$anchor = '    if (g_islandPlacementPopup.IsHitTestVisible() != g_playerGrid.IsHitTestVisible())'
$sourceText = Insert-Once $sourceText "$anchor`n        g_islandPlacementPopup.IsHitTestVisible(g_playerGrid.IsHitTestVisible());" "    bool hitTest = ChargeStripVisible() || g_playerGrid.IsHitTestVisible();`n    if (g_islandPlacementPopup.IsHitTestVisible() != hitTest)`n        g_islandPlacementPopup.IsHitTestVisible(hitTest);"
$anchor = 'void UpdateRuntimeLayout(HWND hwnd,'
$sourceText = Insert-Once $sourceText $anchor "$additions`n$anchor"
$anchor = '                     g_settings.marginLeft + g_settings.marginRight);'
$sourceText = Insert-Once $sourceText $anchor '                     g_settings.marginLeft + g_settings.marginRight + (ChargeStripVisible() ? chargeStripReservation : 0));'
$anchor = '        g_settings.marginLeft + g_settings.marginRight);'
$sourceText = Insert-Once $sourceText "`n$anchor" "`n        g_settings.marginLeft + g_settings.marginRight + (ChargeStripVisible() ? chargeStripReservation : 0));"
$anchor = '        g_islandOverlayWidth = overlayWidth; g_islandOverlayHeight = overlayHeight;'
$sourceText = Insert-Once $sourceText $anchor @'
        if (ChargeStripVisible()) {
            controls::Canvas::SetLeft(chargeStripItems, localX + g_playerGrid.Width() + 11);
            controls::Canvas::SetTop(chargeStripItems, localY + (height - 26) / 2);
            overlayWidth += chargeStripWidth;
        }
        g_islandOverlayWidth = overlayWidth; g_islandOverlayHeight = overlayHeight;
'@
$anchor = '        placement.Children().Append(island);'
$sourceText = Insert-Once $sourceText $anchor @'
        placement.Children().Append(island);
        if (!IsVerticalTaskbar()) {
            chargeStripItems = MakeChargeStripReadings();
            chargeStripItems.Height(26);
            chargeStripItems.Width(chargeStripWidth - 11);
            placement.Children().Append(chargeStripItems);
        }
'@
$anchor = '    g_islandPlacementHost = nullptr;'
$sourceText = Insert-Once $sourceText "$anchor`n    g_islandBasePositionValid = false;" "$anchor`n    chargeStripItems = nullptr;`n    g_islandBasePositionValid = false;"
$outputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
[IO.File]::WriteAllText($outputPath, $sourceText, [Text.UTF8Encoding]::new($false))
