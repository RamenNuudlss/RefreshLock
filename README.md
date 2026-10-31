<p align="center">
  <img src="assets/icon.png" width="96" alt="RefreshLock icon">
</p>

<h1 align="center">RefreshLock</h1>

<p align="center">A tiny Windows tray app that locks your monitor at the refresh rate you choose, and snaps it back the instant anything changes it.</p>

<p align="center">
  <img src="assets/screenshot.png" width="420" alt="RefreshLock window">
</p>

## Why I built this

I have a 240 Hz monitor that kept flipping itself to **49 Hz**. Not 60, not 144. 49, a rate I never picked.

I tried everything I could find:

- Setting 240 Hz in **Windows Settings → Display → Advanced display**
- Setting it in **NVIDIA Control Panel → Change resolution**
- Digging through every related Windows and NVIDIA setting

None of it stuck. I'd set 240 Hz, and some time later it would quietly fall back to 49 Hz again. There was no warning, no obvious trigger, and no setting that made it stay.

I wanted something lightweight that would **aggressively force** the refresh rate to whatever I told it, and keep forcing it. Nothing I found did exactly that, so I made RefreshLock.

## What it does

RefreshLock watches your monitor. The moment its refresh rate is anything other than your target, RefreshLock switches it back. It doesn't wait for you to notice or require you to open a settings menu.

### Features

**Enforcement**
- **Instant correction:** hooks Windows' display-change event, so a drop gets reversed the moment it happens
- **Backup polling:** also checks on a timer (0.25 s, 0.5 s, 1 s, 2 s, or 5 s) in case an event is missed
- **Keeps your resolution:** only the refresh rate is changed
- **Saves the change to Windows**, so the corrected rate is what Windows treats as your setting
- **Smart about unavailable modes:** if your target rate isn't offered at that moment, RefreshLock tells you instead of failing silently, and keeps retrying

**Interface**
- **Big live readout** of the current refresh rate and resolution
- **Status pill:** Locked (green), Not offered (amber), Paused (grey), or Change failed (red)
- **Monitor picker** for multi-monitor setups
- **Rate list from your hardware:** only shows the rates Windows actually offers at your current resolution
- **Correction counter** showing how many times it has fixed a drop, when the last one happened, and what rate it dropped to. This is useful for spotting a pattern in what's causing it.
- **Dark theme**, including a dark title bar on Windows 10 (2004+) and Windows 11

**Tray and startup**
- **Close to tray:** closing or minimizing hides it to the system tray
- **Tray menu:** left-click to show/hide; right-click for Open, Enforcing on/off, Force now, and Exit
- **Tray tooltip** shows the current rate and status at a glance
- **Start minimized** and **Launch at login** toggles
- **Optional notifications** each time it catches and fixes a drop
- **Single instance:** launching it again just brings up the existing window

**Lightweight**
- One ~58 KB `.exe`, no installer
- No dependencies beyond the .NET Framework that already ships with Windows 10/11
- Settings stored in a plain text file at `%APPDATA%\RefreshLock\settings.ini`

## Download

Grab `RefreshLock.exe` from the [latest release](../../releases/latest) and run it.

> The exe is unsigned, so SmartScreen may warn on first launch: click **More info → Run anyway**. Or build it yourself (see below).

## Usage

1. Pick your monitor and the rate to lock at (e.g. 240 Hz).
2. Leave it enforcing. Close the window and it keeps running in the tray.
3. Turn on **Launch at login** so it's always guarding your refresh rate.

## Build from source

No Visual Studio needed. The C# compiler ships with Windows:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:app.ico /optimize+ /r:System.Windows.Forms.dll /r:System.Drawing.dll RefreshLock.cs
```

## Troubleshooting

**Status says "240 Hz not offered".** Your GPU has temporarily lost that mode entirely, so no app can switch to it until it comes back. Common causes:

- **A bad mode in the monitor's EDID** that the driver falls back to. An odd number like 49 Hz is a classic sign. [CRU (Custom Resolution Utility)](https://www.monitortests.com/forum/Thread-Custom-Resolution-Utility-CRU) can delete the unwanted modes; run its `restart64.exe` afterward. Then Windows can't fall back to them at all.
- **Cable or port bandwidth.** Use DisplayPort or a certified high-speed HDMI cable that supports your resolution at 240 Hz.
- **Windows 11 Dynamic refresh rate.** Make sure Settings → Display → Advanced display is set to a fixed rate, not a "Dynamic" option.
- **A game or app requesting its own rate.** If drops line up with launching something, check that program's display settings. The correction counter's timestamps help here.

## License

MIT
