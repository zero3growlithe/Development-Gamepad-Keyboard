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

## Building

```sh
dotnet build src/GamepadKeyboard/GamepadKeyboard.csproj -c Release
```

Or grab a ready exe from [Actions](../../actions) (artifact `DevelopmentGamepadKeyboard-win-x64`, self-contained, single file).

## Roadmap

- [ ] Point edit mode via gamepad (currently points editable in settings JSON)
- [ ] Mouse button mapping UI (profiles editable in `settings.json` for now)
- [ ] Per-app profile auto-switching
- [ ] Flick-stick style flick gestures for fast text navigation

## License

MIT