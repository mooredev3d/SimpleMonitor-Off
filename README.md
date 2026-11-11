# Simple Monitor Off

Simple Monitor Off is a small tray app for Windows to turn off your monitors. Left-click its icon next to the clock and every display goes dark. Nothing is closed, so music, downloads and renders keep running. You choose what wakes the screens, and any other input is blocked while they're off, so a bumped desk or a cat on the keyboard won't wake them.

## Features

- **One-click off** from the system tray
- **Choose what wakes the screens:** key press, mouse movement, mouse click, or any combination. Set how far the mouse must move so a small wiggle doesn't count.
- **Phone remote:** a home-screen page on your phone to turn the screens off or wake them over your home Wi-Fi. It's PIN-protected, with optional HTTPS.
- **Two turn-off methods:**
  - Standard Windows display sleep
  - **Direct to monitors (DDC/CI)**, for setups where DisplayPort monitors keep waking themselves up. Monitors without DDC/CI, such as many pen displays, get a black cover instead.
- **Turns screens off again** if they wake by themselves, as many times as you choose
- **Lock the PC** when turning off (optional)
- **Mute while off:** lowers speaker volume and restores it on wake, without muting your headset mic
- **Monitors window:** lists each screen with its connection (HDMI, DisplayPort, USB-C), resolution, and DDC/CI support
- **Wake log:** shows what woke your screens: which input device, input injected by software, a monitor reconnecting, or a program keeping the display awake. You can ignore those programs from right inside the log.
- **Light, dark, or follow-Windows** appearance
- **Start with Windows**, with all settings remembered

## Requirements

- Windows 10 or 11 (64-bit)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows will offer to install it the first time if it's missing)
- For the phone remote: the phone and PC on the same Wi-Fi network

## Install

1. Download the latest `SimpleMonitorOff` zip from [Releases](../../releases).
2. Unzip it anywhere and run `SimpleMonitorOff.exe`.
3. The monitor icon appears in the tray. Windows 11 may hide it under the **^** at first; drag it onto the taskbar to keep it next to the clock.

## Using it

- **Left-click** the tray icon to turn the monitors off.
- **Right-click** for all settings.
- **Ctrl+Alt+Del** always wakes the screens. Windows doesn't let any app block it.

### Phone remote

1. Right-click the tray icon, choose **Phone remote → Connect a phone**, and scan the QR code with your phone.
2. Add the page to your home screen (Chrome: **⋮ → Add to Home screen**).
3. The first time, Windows asks to allow the app through the firewall. Choose **Allow** for private networks.

To use **HTTPS**, turn on **Phone remote → Use HTTPS**. The connect window then shows a second QR code that walks you through trusting this PC's certificate on your phone. The certificate is generated locally and can only vouch for home-network addresses.

## Troubleshooting

| Problem | Try |
| --- | --- |
| Screens turn back on by themselves | Open **Wake log**, turn the screens off, and see what's listed just before they come back on. |
| A program keeps the display awake (e.g. Parsec) | In **Wake log**, click **Check programs**, select it, and click **Ignore selected**. |
| DisplayPort monitors wake right back up | Switch **Turn-off method** to **Direct to monitors (DDC/CI)**, and turn on DDC/CI in each monitor's on-screen menu. |
| A monitor won't turn off with the Direct method | Check its DDC/CI status in **Monitors**. Turn it on in the monitor's menu, or avoid adapters, docks and switches that block it. |
| Phone can't reach the remote | Make sure the phone is on the same Wi-Fi (not a guest network) and the PC's network is set to **Private**. Run `allow-phone-remote.bat` to open the firewall port. |

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
build.bat
```

or

```
dotnet publish MonitorOff.csproj -c Release
```

The single-file `SimpleMonitorOff.exe` is written to the `build` folder.

## Helper scripts

- `build.bat` builds the app, closing any running copy first.
- `allow-phone-remote.bat` adds a firewall rule so phones on your local network can reach the remote (port 8765).
- `ignore-parsec-display.bat` tells Windows to ignore Parsec's request to keep the display awake (`undo` reverses it).

## License

[MIT](LICENSE) © Patchs
