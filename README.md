# EasyBlackout

A Windows tray utility. One global hotkey (default **Ctrl+Alt+B**) instantly blacks out every monitor and every RGB peripheral. Press it again and everything comes back exactly as it was.

**[Download the latest release](https://github.com/venstreni/EasyBlackout/releases/latest)** (Windows 10/11 x64) · [spei.dev/easyblackout](https://spei.dev/easyblackout/)

## What it controls

| Integration | Covers | How it stays out of the way |
|---|---|---|
| **Displays** | Every monitor | Topmost black overlay per monitor (physical pixels, per-monitor DPI). Optional per-monitor **DDC/CI hardware power-off** for true zero light (opt-in, with a Test button). |
| **Corsair iCUE** (SDK v4) | Keyboards, mice, headsets, fans, RAM, coolers… | Exclusive lighting control only during a blackout. On restore the LEDs are made transparent and control is released, so iCUE's profile shows again. |
| **Razer Chroma** (REST SDK) | Synapse devices | A Chroma session exists only during the blackout. Deleting it hands control back to Synapse. |
| **Logitech G HUB** (LED SDK) | G-series devices | The SDK is initialised only during the blackout. `LogiLedShutdown` returns control to G HUB. |
| **SteelSeries GG** (GameSense) | GG devices | Black "game events" while blacked out. `stop_game` returns control to GG. |
| **HyperX** (native HID) | Pulsefire Haste (Kingston 0951:1727 and HP 03F0:0F8F). Other HyperX gear is listed, and is covered via OpenRGB or Dynamic Lighting. | NGENUITY has no third-party SDK, so black "direct mode" frames are streamed only during the blackout. The firmware reverts to its onboard (NGENUITY-saved) lighting within about 50 ms of the stream stopping, even if the app crashes. |
| **Windows Dynamic Lighting** | HID LampArray devices (no vendor app needed) | LampArray objects are opened only during the blackout, while the overlay is in the foreground. |
| **OpenRGB** (optional SDK server) | Motherboard, RAM, GPU, strips… | Saves each controller's mode and colors, then writes them back on restore. |

Devices that are detected but that no API can reach are listed as **Not controllable**. EasyBlackout never claims a device is dark when it isn't. If the app exits or crashes mid-blackout, it runs an emergency restore, and every vendor app reclaims its lighting once the process is gone.

## Build

Requirements: the .NET 8 SDK, plus [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer (`winget install JRSoftware.InnoSetup`).

```powershell
.\build.ps1            # test + publish + installer
.\build.ps1 -SkipTests
```

The build produces:
- `artifacts\publish\`: a portable, self-contained `EasyBlackout.exe` and `iCUESDK.x64_2019.dll`
- `artifacts\EasyBlackout-Setup-<version>.exe`: a per-user installer (no admin rights needed), with an optional "start with Windows"

## Layout

- `src/EasyBlackout.Core`: providers, orchestrator, monitors/DDC, settings, hotkey model. Has no UI dependency.
- `src/EasyBlackout`: the WPF app: tray, global hotkey, overlays, dashboard.
- `tests/EasyBlackout.Tests`: xUnit tests (orchestrator with fake providers, hotkeys, settings, status).
- `installer/EasyBlackout.iss`: the Inno Setup script.
- `tools/make-icons.ps1`: regenerates the icons.

## Files

- Settings: `%APPDATA%\EasyBlackout\settings.json`
- Logs: `%LOCALAPPDATA%\EasyBlackout\logs` (kept for 7 days)

## License

MIT, see [LICENSE](LICENSE). The Corsair iCUE SDK DLL is redistributed under Corsair's SDK license.
