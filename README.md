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
