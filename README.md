# Development Gamepad Keyboard

Type with **two analog sticks** and control the mouse from your couch on Windows.
Background tray app for Xbox 360 / One / Series and DualShock 4 / DualSense controllers.

![status](https://img.shields.io/badge/status-alpha-orange) ![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)

## Features

- **Virtual on-screen keyboard** (full US layout incl. function keys, numpad, navigation cluster) rendered as a transparent, always-on-top overlay
- **Dual-stick typing**: each stick draws a ray from its origin point; the highlighted key is pressed with L1 (left) / R1 (right)
- **Adjustable origin points** — edit mode: sticks move the points like a mouse, confirm with A
- **Profiles** for origin points, curves and ray lengths; switch with L2+R2+D-pad Left/Right (current profile shown under the keyboard)
- **Modifier support**: hold L2 = Shift, R2 = Ctrl, stick-press = Alt; combos toggle instead of requiring a third hand
- **Mouse mode** (Y button): right stick = cursor, left stick + D-pad = scroll, remappable buttons with profiles
- **Button legend overlay** for new users, position/size adjustable
- Tray icon: Settings / About / Run as Administrator or User / Run on startup / Exit
- Starts as a normal user; optional admin mode (Task Scheduler entry = no UAC prompt at logon)
- Optional **HidHide reservation** keeps selected controllers exclusive to this app while input is enabled and releases them on `DisableInput`

## Optional HidHide controller reservation

In Settings, enable **Reserve selected controllers while input is enabled** and choose the controller(s). In HidHide Configuration Client, add this app to the Applications list, enable device hiding, and leave inverse cloak disabled.

The app prefers HidHide's process-lifetime session blacklist IOCTLs (functions 2056/2057). For current drivers without that API, Settings offers an explicit legacy fallback. The fallback temporarily adds only the selected device paths to HidHide's persistent blacklist, removes only entries it added on `DisableInput`/exit, and writes a recovery journal before changing the list so the next app launch can clean up after a crash. It also asks Windows to restart the selected device so the change affects applications that already had it open; this needs Administrator rights, otherwise the toast asks you to reconnect the controller manually. Missing, inactive, inversely configured, or otherwise incompatible drivers fail open and show a toast.

## Optional: physical keyboard keys over Parsec (Interception)

By default keystrokes arrive via `SendInput`, which marks them *injected*
(`LLKHF_INJECTED`). Parsec's host-side keyboard hook swallows exactly that
class of input while a session is connected, so gamepad-driven keys can
silently vanish when this app runs on the Parsec host. Mouse injection is not
filtered, which is why mouse mode kept working.

Fix: install the [Interception](https://github.com/oblitum/Interception)
driver once on the game machine —
`command line installer\install-interception.exe /install`, then reboot —
and put its `library\x64\interception.dll` next to `DevelopmentGamepadKeyboard.exe`
(or leave it in `C:\Interception\library\x64`). Then enable **Send keys
through the Interception driver** in Settings → Keyboard. Keys are then
written as genuine keyboard-device input below the injected-flag layer and
behave like physical keys everywhere. The toggle is optional (default off);
without the library the app keeps the classic SendInput path, and every
failure falls back per key. Note: Windows Memory Integrity (Core isolation)
can block this older driver on some systems.

## Optional: keyboard overlay above the Start menu (UIAccess)

Windows keeps the Start menu in a z-order band that plain always-on-top
windows can never reach, so the virtual keyboard overlay hides under it. The
fix is the same mechanism the built-in touch keyboard uses: a **UIAccess**
build — an Authenticode-signed executable with `uiAccess="true"` whose
windows land in the topmost `ZBID_UIACCESS` band and float above the Start
menu, so gamepad typing works in Start search.

The zip ships both flavors plus a one-command installer:

```
extract the zip
run  install-uiaccess.bat         (asks for administrator rights)
launch  "Development Gamepad Keyboard" from the Start menu
```

The script (installer/Install-UiAccess.ps1) copies the `uiaccess\` build to
`C:\Program Files\DevelopmentGamepadKeyboard`, creates a self-signed
code-signing certificate and trusts it on the machine, signs the executable,
and creates the Start-menu shortcut. Windows requires signature + secure
location for UIAccess, which is why the installed copy is the one to launch —
the plain build in the zip keeps the stock behavior (overlay under Start).
If "Run on startup" was used, toggle it off/on after installing so the
scheduled task points at the new copy.

## Building

```sh
dotnet build src/GamepadKeyboard/GamepadKeyboard.csproj -c Release
```

Or grab a ready build from [Actions](../../actions) (artifact `DevelopmentGamepadKeyboard-win-x64`).

> **Requires the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) installed** — the app is framework-dependent to stay lightweight (~1 MB app + your system runtime, no bundled 100 MB payload).

## Roadmap

- [ ] Point edit mode via gamepad (currently points editable in settings JSON)
- [ ] Mouse button mapping UI (profiles editable in `settings.json` for now)
- [ ] Per-app profile auto-switching
- [ ] Flick-stick style flick gestures for fast text navigation

## License

MIT
