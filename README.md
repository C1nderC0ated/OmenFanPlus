# OmenFanPlus

OmenFanPlus is a lightweight tray app for HP OMEN laptops: thermal modes, fan control where the BIOS allows it, temperatures and fan speeds, without OMEN Gaming Hub.

It continues **OMEN Hub Lighter Plus** ([P2GR/OmenHubLighterPlus](https://github.com/P2GR/OmenHubLighterPlus)), which extended **OMEN Hub Light** by determ1ne: an opensource and lightweight substitute for a series programs from HP, based on reverse engineering and keeping a similar architecture to HP's programs.

## What's new in 2.0 (October 2026)

- **New name:** OmenFanPlus (was OmenHubLighterPlus). Settings, a saved fan curve and the "Run on startup" task carry over from the old version.
- **.NET 10.** All projects moved from .NET 5/6 (both out of support) to .NET 10 LTS (supported until November 2028).
- **No more WinRing0.** The bundled 2020 OpenHardwareMonitor copy installed the WinRing0 kernel driver, which has a known vulnerability (CVE-2020-14979) and is on Microsoft's vulnerable-driver blocklist. Temperatures now come from [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) 0.9.6:
  - **CPU:** the processor's own sensor (Intel, and AMD Ryzen) through the signed [PawnIO](https://pawnio.eu) driver, if it is installed. Otherwise the ACPI thermal zone, which needs no driver. Hover the CPU temperature in the popup to see which source is in use.
  - **GPU:** NVIDIA's NVAPI (AMD: ADL), which run in user mode.
- **Only the modes your laptop supports.** At startup the app asks the BIOS what it supports, using read-only commands, and shows only those buttons. Older OMENs without manual fan control get **Default / Performance / Comfort**; laptops with it also get Off / Max / Custom curve. The third mode is **Quiet** on Victus boards (HP's device list decides) and **Comfort** on OMEN boards. *Comfort* is the name HP's OMEN Command Center used for its Cool profile on these laptops: it lowers CPU and GPU temperatures and starts the fans earlier, so it isn't a quiet mode; Default is usually the quietest (hover a button for details).
- **The saved mode is sent again** at startup, after sleep and when the charger is plugged in or out (the firmware refuses Performance on battery). Before, the button lit up but nothing was sent. Max is re-sent every 90 s, because the firmware hands manual fan levels back to automatic after about 2 minutes; Off is deliberately left to lapse, so the BIOS takes the fans back.
- **Fans go back to the BIOS on exit.** Quitting releases Max, Off or the custom curve, then sets the saved thermal mode.
- **Honest status.** If the BIOS rejects a command, the popup and the tray tooltip say so, and everything is logged to `%LOCALAPPDATA%\OmenFanPlus\OmenFanPlus.log` (tray menu > *Open log folder*).
- **Fan speed in RPM** in the popup: from the fan levels, which are in units of 100 RPM (the old "%" label was wrong), or from BIOS query 0x11 on boards without fan levels, such as the OMEN 15-dc1xxx. The curve editor still uses the original level scale.
- **"Run on startup" fixed.** The task (now `OmenFanPlusStartup`) no longer stops the app when the charger is unplugged, starts it on battery too, runs for your account only, waits 10 s after logon for HP's WMI interface, and runs at normal priority. It only ever starts the copy installed in Program Files; a build run from a `bin` folder never takes it over.
- **Update checks** (tray menu > *Check for updates...*, automatically 2 minutes after the app starts and then every 24 hours): the app tells you when the .NET runtime or PawnIO has a newer release and how to install it: the Chocolatey or winget command if you have one of them, and always the download link. It never downloads or installs anything by itself. The automatic check sends only GET requests to `builds.dotnet.microsoft.com`, `api.github.com` and, as a fallback, `github.com`, with an `OmenFanPlus/<version>` User-Agent; turn it off with tray menu > *Check for updates automatically*.

## What this fork changes

- A quick UI for when Omen key is pressed
- Ability to set fans to max or turn them off (on laptops whose BIOS supports manual fan levels)
- **Custom fan curve** — define a temperature→speed curve for both CPU and GPU fans with a live preview editor (same laptops)
- Ability to remap the OMEN key to any other key (or hotkey)
- Ability to remap the OMEN key to execute programs
- Responds to trackpad lock key and Windows key lock
- Remove Four Zone Keyboard light (Maybe for in the future)
- An option to run on startup
- New enum values for fan control (Quiet mode wasn't working for me in the original version)
- Fan speed text

![Fan control menu](<https://raw.githubusercontent.com/P2GR/OmenHubLighterPlus/master/README Images/fan_control.png>)
![Fan curve editor](<https://raw.githubusercontent.com/P2GR/OmenHubLighterPlus/master/README Images/fan_curve.png>)
![Remap menu](<https://raw.githubusercontent.com/P2GR/OmenHubLighterPlus/master/README Images/remap.png>)

## Why I need it?

Tuning your computer fast when others are still launching OMEN Gaming Hub, without annoying HP product advertisements.

The goal is to replace or help you removing the following HP Apps:

- OMEN Gaming Hub (Previously "OMEN Command Center")
- HP System Event Utility
- HP Application Enabling Services (Software component driver)
  - HP Analytics services
  - HP App Helper HSA Service
  - HP Diagnostics HSA Service
  - HP Network HSA Service
  - HP System Info HSA Service
- Omen Software and Services (Software component driver)
  - HP Omen HSA Service

Also, you can avoid the "HP System Event Utility" pouring trashes to your `C:\system.sav`, and other components with various stacks of telemetry components.

## Install and update

Requirements: Windows 10/11 x64, the **.NET 10 Desktop Runtime (x64)** to run it and the **.NET 10 SDK** to build it (the SDK includes the runtime). Install them in whichever way you like:

| | .NET 10 SDK | .NET 10 Desktop Runtime | PawnIO (optional) |
| --- | --- | --- | --- |
| [Chocolatey](https://chocolatey.org) | `choco upgrade dotnet-10.0-sdk -y` | `choco upgrade dotnet-10.0-desktopruntime -y` | `choco upgrade pawnio -y` |
| winget | `winget install --id Microsoft.DotNet.SDK.10 -e` | `winget install --id Microsoft.DotNet.DesktopRuntime.10 -e` | `winget install --id namazso.PawnIO -e` |
| Download | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0): *SDK*, Windows x64 | same page: *Desktop Runtime*, Windows x64 | `PawnIO_setup.exe` from [pawnio.eu](https://pawnio.eu) or [GitHub](https://github.com/namazso/PawnIO.Setup/releases/latest) |

Some Windows editions (e.g. LTSC) come without winget, and the download links work everywhere.

Then, from an elevated PowerShell in this folder:

```
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

`install.ps1` builds the app and asks any running copy to quit (it hands the fans back to the BIOS first). It stops old OmenHubLighterPlus builds and removes the WinRing0 driver service that builds before 2.0 left behind. Then it installs to `C:\Program Files\OmenFanPlus`, replaces the old `OmenHubLighterPlusStartup` task with `OmenFanPlusStartup`, and starts the installed copy. Run it again after pulling changes. Don't keep using an old OmenHubLighterPlus build from a `bin` folder: builds before 2.0 load WinRing0 again.

**Optional: PawnIO** for the CPU's own temperature sensor (otherwise the ACPI thermal zone is used, which follows the CPU closely on the OMEN 15-dc1xxx). PawnIO is a signed driver that works with Memory Integrity and the vulnerable-driver blocklist turned on; install it with one of the options in the table above and restart OmenFanPlus afterwards. To **update** PawnIO or the .NET runtime later, quit OmenFanPlus first: the app keeps the PawnIO driver open (so the driver can't be replaced while it runs), and the runtime installer removes the version the app is running on. Restart Windows if the PawnIO installer asks for it.

## How to use it?

Since OMEN Gaming Hub is a huge project, determ1ne only implemented a small part of it which their notebook (OMEN 15-dc0xxx, i7-8750H with NVIDIA GTX 1060) needed. But I also added features for my notebook (Victus 16-d0xxx, i7-11800H with NVIDIA RTX 3060).

Currently, the application supports the following features:

- ~~Four Zone Keyboard Light~~ (Maybe in the future)
- Thermal modes through the BIOS (command 0x1A): Default / Performance / Comfort (HP's Cool profile) on OMEN boards, Default / Performance / Quiet on Victus boards
- Fan Max / Off and a **Custom Fan Curve**, only where the BIOS supports manual fan levels (commands 0x2D/0x2E)

### What works on which laptop

| BIOS answers | Buttons shown |
| --- | --- |
| 0x2D GetFanLevel works | Comfort (Quiet on Victus), Default, Performance, Off, Max, Custom |
| 0x2D unsupported (e.g. OMEN 15-dc1xxx, board 8574, BIOS F.29) | Default, Performance, Comfort |

On the OMEN 15-dc1xxx the BIOS has no max-fan or manual fan command, so the old "Max" button only ever switched to Performance. HP's own platform list agrees (no max-fan support for board 8574). Real manual fan control there would need direct writes to the embedded controller. [OmenCore](https://github.com/theantipopau/omencore) tries that, but its 8574 profile is unverified and the one report from identical hardware (15-dc1077tx, BIOS F.29) saw no working curve or max mode, so this app deliberately doesn't write to the embedded controller.

But if you want, you can easily port what you need, because the fundamental chores are available in a similar form to what HP programed in their applications. Happy hacking!

## Contributing

I was able to find most of what I need by decompiling `HP.Omen.Background.FanControlBg.dll` using [dotPeek](https://www.jetbrains.com/decompiler/) and then going to `HP.Omen.Core.Common.OmenHsaClient`.

Build with `dotnet build OmenFanPlus.sln -c Release`. `tests\Test-InstallScript.ps1` checks `install.ps1` without running it: its bytes, that it parses, that the install-folder guard refuses the wrong folders, and that the guard runs before anything else.

Projects: `OmenFanPlus` (the app), `OmenFanPlus.Hp` (HP BIOS WMI access, reverse-engineered from OMEN Gaming Hub) and `OmenFanPlus.Lib` (temperatures). The original repository's OmenDriverPatcher (a C++ tool that patched HP's services) and EventWatcher console tool were removed in 2.0; they are still in the git history on `main`.

### Custom curve scale (boards with manual fan control)

The BIOS takes fan levels in units of 100 rpm, not percent, and only a band of them is useful (OmenMon observed levels 20-55, i.e. 2,000-5,500 rpm). The curve editor keeps its 0-100 % scale and maps it onto that band: 0 % stops the fan, 1 % is the slowest it runs, 100 % the fastest. The band isn't read from the BIOS yet; if your board answers the 0x2F fan-table query, its raw bytes are written to the log so the real limits can be decoded from it.

Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
