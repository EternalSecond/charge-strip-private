# Charge Strip

A portable Windows 11 taskbar strip for device batteries, media controls, and PC resources. The screenshots below show the earlier tray layout.

[**Windows 11 x64 releases**](https://github.com/EternalSecond/charge-strip-private/releases) · [Changelog](CHANGELOG.md)

![Charge Strip open on Windows 11, showing real mouse and headset battery levels](docs/tray-popup-windows.png)

[See the Settings window](docs/settings-preview.png)

## Install and run

1. Download **Charge-Strip-Windows-x64.zip** and extract it to a folder you plan to keep.
2. Open **ChargeStrip.exe**. The modules share one strip next to the notification area. It scales down as the taskbar fills up and hides if the remaining gap cannot fit the strip.
3. Right-click the strip and select **Settings**, or launch **Open Settings.cmd**. The launcher reuses the running instance and works while the strip is hidden.

The portable executable is unsigned. Windows may display a publisher warning. To verify your download, run `Get-FileHash .\Charge-Strip-Windows-x64.zip -Algorithm SHA256` in PowerShell and compare it with **SHA256SUMS.txt** on the release page.

## Use the strip

| Action | Result |
| --- | --- |
| Hover over the battery icons | Read device names and charge percentages |
| Hover over Batteries | See the displayed device names and charge levels |
| Right-click the strip | Open Settings, refresh, or exit |
| Choose **Position → Left of the system tray** | Keep the right edge 6 pixels from the tray and grow left as modules are added |
| Choose **Position → Manual X/Y** | Select a monitor and enter offsets in pixels, or drag the strip |
| Check modules and use the arrows | Toggle Batteries, Media, and PC Resources and order them from left to right. Changes appear immediately in the preview and on the taskbar. |
| Check devices, use the arrows and set a limit | Choose the readings in the battery list. The strip shows an icon and percentage for up to two selected devices. |
| Change display mode | Show icons and percentages, icons only, or percentages only |
| Change scale, spacing, colors or background tint | Adjust the strip and its panels |
| Enable **Run with Windows** | Show the strip when your Windows account signs in |
| Save / Cancel | Keep / revert changes |

Choose **УКР**, **РУС**, or **ENG** at the top of Settings to switch between Ukrainian, Russian, and English. The interface updates immediately. Select **Save** to keep the language, or **Cancel** to revert. Readings refresh in the background about every 12 seconds.

## Media

Click the track title or artwork to open the media card above the strip. The card shows the last available track or video immediately while Windows metadata and artwork refresh in the background. Pick a source in the card to switch between available Windows media sessions, including Chrome with YouTube and Spotify. Playing and paused sessions can appear. The card enables playback, skipping, and seeking according to the selected session's capabilities. Escape or a click outside dismisses the card.

## PC Resources

Click the chip with a pulse line to open **PC Resources**. In **Settings → PC Resources**, pick and reorder the metrics independently for the strip and the panel. Clear all strip metrics to keep the chip by itself. Uncheck **PC Resources** in the module list to hide the module.

Choose **Numbers** or **Numbers + bars**, and set the bar color with the palette or a `#RRGGBB` value. Bars show the share of capacity in use. Network bars use the combined link speed of active interfaces. GPU summarizes the busiest available hardware engine and shows a dash when Windows supplies no GPU data. The sampler collects only the metrics selected for either view. The panel opens by click and dismisses on Escape or an outside click.

## Existing Windhawk island

**Embed in existing Island Media Controls** uses Island Media Controls 1.0.0 by usho to place battery and PC readings between the media island and the native tray icons on horizontal taskbars. The readings have their own element beside the player in the mod's placement host. Charge Strip supplies battery levels and PC readings from a local file. The media island retains its playback controls and artwork. Charge Strip's own strip and media card are hidden in this mode.

Click the battery button to see device names, charge and connection status. Click the resource button to see the selected panel metrics in a two-column dashboard with live readings. Both panels open above the taskbar.

Click a resource card to see up to five processes. CPU uses the share of total CPU time, RAM shows the resident working set, and GPU shows each process's busiest engine. The disk and network cards show an unavailable message in the process view because the overview counters cannot attribute those readings to individual processes or to a specific drive. Use Back or Escape to return to the overview.

Run `windhawk\Build-IslandMedia.ps1` from PowerShell to build the integration against the installed mod source. Run `windhawk\Set-IslandMediaLibrary.ps1` once as administrator to apply it. Use its `-Restore` switch to restore the original Windhawk library. The integration preserves the original MIT attribution and requires Windhawk for this mode.

## Device compatibility

| Device or source | Status |
| --- | --- |
| SteelSeries Arctis Nova Pro Wireless, USB base `1038:12E0` | Verified on hardware. Separate readings for the headset and the spare battery in the base, compared with SteelSeries Engine. |
| HyperX Pulsefire Haste 2 Wireless, USB receiver `03F0:0D97` | Verified on hardware. Reports percentage and charging status. |
| HyperX Pulsefire Haste 2 Wireless, USB receiver `03F0:0F98` | Uses the documented battery query. This receiver revision was not tested for this release. |
| HyperX Cloud Alpha Wireless, USB receiver `03F0:098D` | Receiver detected on the test PC. It returned no battery reading during testing. |
| PC battery and Bluetooth accessories | Displays readings exposed by Windows. Availability depends on the model and connection mode. |

A dash means a detected device has no available reading. Check that the device is powered on, awake and connected through a supported receiver or Bluetooth mode.

Charge Strip sends one Windows notification when the Arctis headset reaches 25% or below while discharging. The alert rearms at 35%, and an unknown reading or an app restart preserves the current alert cycle.

## Settings, updates and removal

Preferences are stored per user in `%LOCALAPPDATA%\Charge Strip\settings.xml`. Charge Strip reads battery data locally and has no telemetry.

To update, quit Charge Strip and extract the new files into the same folder. To exit, right-click the strip and select **Exit**. To remove it, turn off **Run with Windows**, save, exit the app, then delete the extracted folder. Remove the separate preferences file if you also want to clear your layout and colors.

## Build from source

Use Windows 11 x64 and the included .NET Framework compiler. Run these commands from Command Prompt in the repository root.

| Command | Result |
| --- | --- |
| `"Start Charge Strip.cmd"` | Build if needed and start the app |
| `"Start Charge Strip.cmd" /settings` | Build if needed and open Settings |
| `"Start Charge Strip.cmd" /build` | Rebuild and launch. Exit any running copy first. |
| `"Start Charge Strip.cmd" /package` | Build the portable ZIP and SHA-256 checksum in `build\` |

Charge Strip is written in C# with Windows Forms. `ModuleStrip.cs` measures the available taskbar space with Windows UI Automation and draws the shared strip. `Settings.cs` handles the editor and persistence. `MediaSessions.cs` reads Windows media sessions and artwork on a worker thread. `PcResources.cs` samples Windows performance data. `Batteries.cs` reads Windows properties and queries supported USB HID devices. `Localize.cs` contains the three interface translations.

### Protocol references

- [SteelSeries Arctis HID protocol](https://github.com/TimP4w/HaloDaemon-plugins/blob/main/steelseries_arctis/docs/protocol.md)
- [HyperX Pulsefire Haste 2 battery protocol](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/blob/main/docs/hyperx_haste_2.md)
- [HyperX Cloud Alpha HID battery protocol](https://github.com/FedeltaMedia/hyperx-cloud-alpha-battery#technical-details)

## License

Charge Strip is available under the [MIT License](LICENSE).
