# RadioTEDU OnAir Next desktop console

The Windows desktop shell opens the same authenticated local operator console used by a browser. The broadcast engine remains an independent Windows service. Closing the window hides it in the system tray; double-click the tray icon to reopen it. The tray menu also opens the console in the default browser or exits only the desktop shell.

The packaged app starts in the tray when the operator signs into Windows. The engine service has its own automatic startup and does not depend on the shell or a signed-in user. At first run, the app creates the desktop shortcut. A matching older browser-console shortcut is copied to a separate name before replacement; unrelated shortcuts are preserved.

The shell connects only to the literal loopback console origin. Its renderer has Node integration disabled, context isolation and sandboxing enabled. The shell verifies the console product, version and process identity against the operator-private descriptor before passing the existing bootstrap capability to the page. It does not store Icecast credentials.

## Developer qualification

Use the committed dependency lock with npm ci --ignore-scripts, then run node node_modules/electron/install.js to obtain the pinned Electron binary. Run npm test and electron test/smoke-main.cjs in a disposable Windows environment. CI also checks a packaged background launch and an actual Windows shortcut. Its fixtures use loopback only.

The desktop shell is an operator interface. It is not an independent audio monitor, a backup stream, or evidence that a remote listener received uninterrupted audio.
