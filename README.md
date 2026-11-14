# RadioTEDU OnAir Next

OnAir Next is a radio automation and playout system with Windows and macOS installer work under qualification. A Windows service runs each station's audio programme and sends it to its configured streaming outputs. Operators manage the service through a local browser console or the optional Windows desktop shell.

RadioTEDU's November 2026 handoff moved eight stations and sixteen configured outputs to OnAir Next. The transfer record describes the installed system at the time of handoff. This repository contains the application source and its test fixtures. The GitHub revision and the build installed on a broadcasting PC are tracked separately. [Transfer record](docs/PRODUCTION_TRANSFER_20261105.md)

## Operating the console

Open the installed OnAir Next shortcut as the Windows operator. The launcher opens the local, authenticated console in the default browser. The optional Electron shell opens the same console in its own window and stays in the system tray when closed. The Windows service owns playback independently of either window. [Desktop console](desktop-shell/README.md)

- **On Air** shows the station programme, queue, audio levels and connection state of each output.
- **Library** manages station-specific live folders, metadata and queue additions. The engine validates new files before including them in rotation.
- **Programmes** prepares and schedules special-day music and timed recordings. A prepared publication includes the validated media selected for that schedule.
- **Reports** shows playback history and calendar exports. The journal records what the engine played and when.
- **System** manages the local engine connection and provides setup and maintenance controls.

[Browser operator guide](docs/BROWSER_OPERATOR_GUIDE.md) · [Live folder workflow](docs/LIVE_FOLDER_OPERATOR_WORKFLOW.md) · [Programme scheduling](docs/PROGRAMME_DRAFT_UI_ACCEPTANCE.md)

## Operator release

The operator release adds a station-bound live microphone panel, uploads into assigned live folders, a guided Icecast setup screen, and native Windows / Apple Silicon / Intel Mac installer builds. [Setup and microphone guide](docs/OPERATOR_SETUP_AND_MICROPHONE.md). Installer artifacts and release notes must state their actual test and code-signing status. The installed broadcasting version remains independent of source changes.

## Windows setup

Extract the complete Windows x64 package and open `setup/OnAirNext.Setup.exe`. Follow the guided checks for the package and audio runtime before installing. A fresh installation starts with eight stopped stations and no armed outputs. Use the separate update procedure for an existing installation.

[Guided setup](docs/WINDOWS_GUIDED_SETUP.md) · [Application updates](docs/WINDOWS_APP_UPDATES.md) · [Audio runtime](docs/CODEC_RUNTIME.md)

## Development and testing

The active solution uses the .NET SDK pinned in `global.json`. On a development machine, `scripts/Setup-Dev.ps1` prepares the private SDK and SSD build root. `scripts/Test-Engine.ps1` is the engine verification entry point. Development outputs use literal loopback and `/next-` mounts. [SDK pin](global.json)

| Component | Role |
| --- | --- |
| `src/OnAirNext.Engine` | PCM playout, mixing and crossfade, output recovery, library and playback journal |
| `src/OnAirNext.Host` | Windows service and local engine control pipe |
| `src/OnAirNext.Web` | Browser console and authenticated bridge to the engine |
| `src/OnAirNext.Setup` | Guided installation and maintenance |
| `desktop-shell` | Optional Windows desktop window and tray icon |
| `tests/OnAirNext.Tests`, `tests/OnAirNext.Lab`, `tests/browser` | Unit, loopback audio and browser checks |

GitHub Actions runs builds and software checks on disposable Windows runners. Test evidence is scoped to the source revision and fixtures named in each run. The 18-output synthetic roster and RadioTEDU's 16-output installation have separate qualification records.

[GitHub Actions](https://github.com/radiotedu/radiotedu-onair-next/actions) · [Delivery ledger](docs/BROADCAST_PC_DELIVERY_LEDGER.md) · [Development plan](DEVELOPMENT_PLAN.md)

Keep broadcast credentials, production databases, private configuration, music and recordings out of this public repository.
