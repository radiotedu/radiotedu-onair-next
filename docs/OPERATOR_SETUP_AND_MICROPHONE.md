# Operator setup and live microphone

Developer: **RadioTEDU Technologies / RTAI**. The OnAir Next symbol is original application artwork; the RadioTEDU mark comes from the official technology site.

## Install without development tools

Use the matching installer attached to a release: Windows x64 EXE, Apple Silicon macOS PKG, or Intel macOS PKG. These contain the .NET runtime, desktop console and pinned audio runtime with its source archives and notices. Music and stream passwords are never included. The packages are fresh-install previews until the release notes identify completed qualification. An installer filename is not evidence of signing or notarization.

Windows setup opens its own desktop window. In **System → Get started**, open installation checks. The bundled audio-tools directory is filled in automatically. Review the package, install the service and approve Windows elevation. Existing installations use the separate reviewed application-update workflow; the setup wrapper never silently stops an existing broadcaster.

macOS 14 or newer is required, matching the bundled [.NET 10 platform support](https://learn.microsoft.com/en-us/dotnet/core/install/macos). On macOS, install from the signed-in studio operator account. The PKG places the app in Applications, initializes private operator data and registers a launchd daemon running as that operator. An existing application or service is preserved and causes this fresh-install package to stop. The daemon starts independently of the desktop window, uses automatic recovery and remembers station running intent. Availability before login still depends on storage/FileVault and machine configuration.

Both platforms begin with eight stopped channels and no armed outputs. Opening a window does not start broadcasting.

## Configure music, sweepers and Icecast

1. In **System → Get started**, use each channel's **Music folder** button. Choose its live folder and complete **Check engine access**, or use **Add files** for managed storage.
2. Use **Sweepers** to choose that channel's jingle and sweeper folders and save their cadence. Music, jingles and sweepers keep separate inventories.
3. Enter the Icecast host, port, source user and password. Select the desired channels, primary mounts, low-bitrate and FLAC variants. **Review destinations** shows the exact endpoints without displaying passwords. Review does not contact those sources.
4. All channels must be stopped to replace connection settings. Confirm ownership and release of the former source, then **Save reviewed connections**. Credentials are kept in protected service storage, not browser storage.
5. On Windows use **Start stopped service** after saving. On macOS launchd restarts the stopped configuration owner automatically. Reconnect and read back destinations before starting the selected channels in **On Air**.

AAC-LC is 192 kbit/s; HE-AACv2 is 64 kbit/s; FLAC retains its existing lossless codec path. New wizard outputs have a bounded five-second queue. A buffer cannot prevent every upstream outage. No codec is silently substituted when its encoder is missing. Default fresh-install crossfade is three seconds except Classic, which starts at zero; adjust each channel in On Air.

## Add files to a live folder

**Library → Add files** copies selected audio into the channel's current assigned folder when connected to the commissioned installed service. It does not switch the channel to a different folder. Batches accept 1–64 files up to 240 MiB. Retained transfers support retry/cancel; content-addressed destination names prevent overwriting different songs or duplicating successful retries. The background validator checks audio before admitting it to rotation. Review progress and metadata in Library; a rejected or unreadable file is not a ready song.

The service needs write access and free space on the assigned drive. Errors remain visible in Library. Existing playing files are not replaced. Development engines keep external folders read-only. Folder links and stale folder revisions are rejected by the import worker.

## Microphone

1. Select the channel in **On Air**. Start its music automation first.
2. Select an input device and choose **Test microphone**. The browser asks for microphone permission only after this action. The meter is private; no microphone audio is sent to the engine yet.
3. Adjust input gain. Optional headphone monitoring is off by default. Use headphones before enabling it. **Mute** silences the input; while live, music remains ducked until **End live**.
4. Choose **Go live** and confirm the named channel. Its codec variants receive the same mixed programme. Other channels do not receive the microphone. Music continues at a reduced level with a short ramp; the existing final-mix limiter and programme meter stay after the mix.
5. **End live** restores the normal music level. **Release device** closes capture. Changing channels, closing the browser page, losing the device or losing the capture connection ends microphone delivery. Go live is never retried automatically after a fault.

The desktop window can hide in the tray while capture continues; exiting the application or closing a browser page ends capture. The engine clears abandoned speech and restores automation. The transport uses bounded 20 ms, 48 kHz stereo float PCM blocks, a 750 ms engine idle timeout and acknowledgement/backlog checks in the client. This controls backlog and recovery, not a guarantee of zero network loss. Only one microphone transport can own an engine at a time. Recording and remote guest contribution are not provided by this panel.

## Recovery and verification

The Windows service and macOS daemon supervise the engine independently of the UI. Each output retries its own connection; microphone operations do not restart outputs or reset the playlist. Station running intent persists, whereas microphone activation does not.

Read source connection state separately from listener audio. Unit, loopback and synthetic microphone tests do not certify a physical microphone, a particular sound device, EBU conformance across every source, or uninterrupted operation during power/ISP/server failure. Complete operator listening and a sustained commissioning run on a non-live installation before deploying a new engine to an established broadcaster.

Application-only maintenance requires recovery contract version 2 in both the installed image and its replacement. Both must declare support for held rotation, queued items, scheduled programmes and crossfade journals. Older undeclared packages are rejected instead of risking an incompatible rollback; migration from such installations remains a separate reviewed operation.
