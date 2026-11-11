# PhoneCam

**Your Android phone as a live camera for a projector or a big screen: on Windows, over a USB cable, with nothing to install on the phone.**

PhoneCam is a small portable Windows app in the spirit of Camo Studio, cut down to what a classroom needs: plug the phone in, point it at a notebook, a calculator or a lab bench, and project the picture full screen with low latency.

> The interface starts in English and can be switched to Italian or Persian using the language selector. The choice is saved in PhoneCam's settings; Persian uses a right-to-left layout.

## Features

- **Plug and play**: detects the phone as soon as it is connected with USB debugging on, and starts the camera by itself.
- **Every lens the phone exposes**: main, ultra-wide, macro and front cameras, each with the resolutions it declares (16:9 and 4:3, up to 4K).
- **Frame rate and quality presets** (4 to 32 Mbit/s).
- **Continuous autofocus**, handled by the phone.
- **Zoom** and **torch** control; drag a zoomed image to pan, or reset it to 1×.
- **Rotate, mirror, fit or fill** the picture.
- **Full screen** for projectors, with a resizable side panel that can be hidden or floated over the image.
- **Keeps streaming with the phone screen off or locked.**
- **Remembers your settings**, and sets aside lenses the phone refuses to stream (one click brings them back).
- **Download and run**: a single `PhoneCam.exe`, with nothing to install or extract and no .NET installation needed.

## Requirements

| | |
|---|---|
| **PC** | Windows 10 or 11, 64-bit |
| | Microsoft Edge WebView2 Runtime, already present on Windows 11 and on up-to-date Windows 10 |
| | Windows "N" editions also need the Media Feature Pack, for H.264 decoding |
| **Phone** | Android 12 or newer |
| | Developer options → **USB debugging** turned on |
| | A USB cable that carries data |

## Getting started

1. Download **[PhoneCam.exe](https://github.com/vittop89/PhoneCam/releases/latest/download/PhoneCam.exe)** from the latest release. It can live anywhere, a USB stick included.
2. On the phone, turn on **USB debugging**: Settings → About phone → tap *Build number* seven times, then Developer options → USB debugging.
3. Connect the phone to the PC and run `PhoneCam.exe`.
4. The first time on each PC the phone asks **"Allow USB debugging?"**: tap *Allow*, ticking *Always allow from this computer*.
5. The picture appears within a few seconds. Double-click it or press **F11** for full screen.

The executable is not code-signed, so the browser may ask you to confirm the download and Windows SmartScreen may say *"Windows protected your PC"*: choose *More info* → *Run anyway*.

On first start PhoneCam unpacks adb, the WebView2 loader and its page, about 10 MB, into `%LOCALAPPDATA%\PhoneCam\app`; later starts reuse them, and nothing else is written outside that folder, the settings and the log.

To update, download the new `PhoneCam.exe` and replace the old one. Each version unpacks into its own folder and removes the ones left by earlier versions. Versions 0.1.0 and 0.1.1 also left about 8 MB per version in `%TEMP%\.net\PhoneCam`: version 0.1.2 and later delete that folder too.

While PhoneCam is running, keep closed:

- the phone's own Camera app, which has priority over PhoneCam;
- other programs that use adb, such as Camo Studio, Android Studio or a different version of scrcpy.

The PC's own webcams are not used and do not get in the way.

## Using the app

Choose English, Italian, or Persian from the language selector. The interface updates immediately and remembers the selection for the next launch.
Drag the sidebar edge to resize it (minimum 340 px); focus the divider and use the arrow keys to resize with the keyboard.

| Control | Description |
|---|---|
| Lens | Select the phone camera |
| Resolution, FPS, Quality | Adjust video output |
| Zoom and light, Torch | Digitally zoom with the buttons or mouse wheel, drag to pan, reset to 1×, or toggle the phone's torch |
| Rotate, Mirror, Fill / Fit | Adjust the image |
| Full screen | Fill the display |
| Live | Video stream is running |

### Keyboard shortcuts

| Key | Action |
|---|---|
| F11, or double-click on the image | Full screen on / off |
| Esc | Leave full screen |
| P | Show or hide the side panel |
| + / − | Zoom in / out |
| R | Rotate by 90° |
| M | Mirror |
| A | Fit / fill |

Changing lens, resolution, frame rate or quality restarts the stream, which takes a second or two. Zoom, torch and the image controls apply immediately. Zoom is applied to the preview, so detail may decrease at high magnifications.

## Troubleshooting

| What you see | What to do |
|---|---|
| *Connect the phone with a USB cable* | The phone is not detected: use a cable that carries data, try another port, check that USB debugging is on. |
| *Tap “Allow” on the phone's USB debugging prompt* | Accept the USB debugging prompt on the phone. |
| *The phone's camera is in use by another app* | Close the phone's Camera app or any other app using the camera. PhoneCam retries by itself. |
| A lens disappears from the list, with a notice | The phone refused to stream that lens: some manufacturers keep extra lenses for their own camera app. *Show excluded lens again* brings it back. |
| *The phone cannot stream with these settings* | The phone's encoder rejected the resolution or quality: choose a lower one. |
| The browser or Windows SmartScreen warns about `PhoneCam.exe` | The executable is not code-signed: keep the download, then *More info* → *Run anyway*. |
| *PhoneCam could not prepare its files* | The helper files could not be unpacked into `%LOCALAPPDATA%\PhoneCam\app`: check free disk space and that the folder is writable. |
| Black picture and 0 fps | Update or install the WebView2 Runtime; on Windows N install the Media Feature Pack. |

Settings, a diagnostic log and the unpacked helper files are kept in `%LOCALAPPDATA%\PhoneCam` (`settings.json`, `phonecam.log`, `app\`).

## How it works

```
 Android phone                                  Windows PC
┌───────────────────────────────┐   USB, adb   ┌──────────────────────────────────────┐
│ scrcpy-server (app_process)   │ ───────────► │ PhoneCam.exe (WinForms)              │
│ camera → hardware H.264       │ ◄─────────── │   adb tunnel, scrcpy protocol parser │
└───────────────────────────────┘  torch       │ WebView2 page                        │
                                               │   WebCodecs H.264 decoding → canvas  │
                                               └──────────────────────────────────────┘
```

- When the phone connects, PhoneCam copies [scrcpy-server](https://github.com/Genymobile/scrcpy) to it and starts it through adb, with the camera as video source. Nothing is installed on the phone, and the server deletes its own file once started.
- The phone encodes H.264 in hardware; the stream travels through an adb forward tunnel over the USB cable.
- On Windows, PhoneCam parses the scrcpy 4.1 protocol and hands each packet to a WebView2 page, which decodes it with WebCodecs (hardware accelerated) and draws it on a canvas.
- Torch commands go through the scrcpy control channel. Zoom is applied to the preview so it can be panned without restarting the camera stream.

PhoneCam shows the camera on screen; it does not provide a virtual webcam for Teams, Zoom or Meet.

## Tested on

- realme GT Neo 3 150W (RMX3563), Android 14, on two Windows PCs: 1280×720, 1600×1200 and 3840×2160 at 30 fps, also with the phone locked. Full HD arrives as 1920×1072, eight rows short, most likely because this phone's encoder works in 16-pixel blocks. One extra rear lens is reserved by the phone and is set aside automatically.

Other phones should work when their manufacturer lets adb use the camera. Reports are welcome in the [issues](https://github.com/vittop89/PhoneCam/issues).

## Building from source

Requirements: Windows, the .NET 8 SDK or newer, PowerShell.

```powershell
# Download scrcpy 4.1 (adb + scrcpy-server) and check it against the SHA-256 pinned in the script
.\scripts\fetch-scrcpy.ps1

# Build and run
dotnet run --project src\PhoneCam

# Release build: a single self-contained dist\PhoneCam.exe
.\scripts\publish.ps1
```

A development build uses the files copied next to its executable; the release build carries them inside `PhoneCam.exe`, unpacks them on first start and ignores any copies it finds next to itself.

The executable records the commit it was built from in its product version (for example `0.1.2+<commit>`), so `publish.ps1` refuses to run with uncommitted changes: commit, publish, then tag that commit. `-AllowDirty` skips the check for local test builds.

Useful switches:

- `PhoneCam.exe --debug` enables the WebView2 developer tools.
- `PhoneCam.exe --selftest <clip.h264> <result.txt>` plays an H.264 file through the decoder without a phone and writes what the page decoded. FFmpeg can make a test clip:
  `ffmpeg -f lavfi -i testsrc2=size=1920x1080:rate=30 -t 5 -c:v libx264 -bf 0 -g 60 -f h264 clip.h264`

## License

PhoneCam is released under the [MIT License](LICENSE).
The release executable bundles scrcpy-server and adb (Apache License 2.0), the .NET runtime (MIT) and the WebView2 SDK (Microsoft, BSD-style license): see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

PhoneCam is not affiliated with Genymobile, Google, Microsoft, Reincubate or realme.
