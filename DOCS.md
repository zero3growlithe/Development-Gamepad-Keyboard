# Development Gamepad Keyboard

**A Windows tray application that turns a gamepad (DualSense / DualShock 4 / Xbox controller) into a full PC input device — a virtual on-screen keyboard operated with the analog sticks, a mouse replacement, and a media/utility remote.**

- Repo: https://github.com/zero3growlithe/Development-Gamepad-Keyboard
- Stack: C# / .NET 8 + WPF, code-only (no XAML files), SendInput P/Invoke
- Runs **without admin rights** (asInvoker); optional admin mode via Task Scheduler
- CI: GitHub Actions build + win-x64 artifact on every push
- Config: `%APPDATA%\DevelopmentGamepadKeyboard\settings.json` (auto-created; delete to re-default)

---

## How it works

The app polls the gamepad on a dedicated high-priority thread (1 ms timer resolution) and translates controller state into keyboard/mouse input via SendInput. All UI is code-built WPF:

- **Keyboard overlay** — a full QWERTY keyboard rendered as a transparent, topmost, click-through window that never steals focus. Two analog sticks shoot **selection rays** from configurable origin points; the key under each ray tip is highlighted and committed by mapped buttons.
- **Legend overlay** — a small, click-through panel showing the current button mappings.
- **Input monitor** — a tiny panel visualizing live gamepad state (draggable, no-activate).
- **Tray icon** — the only always-visible element; full menu access.

Two operating **modes**:

| | Keyboard mode | Mouse mode |
|---|---|---|
| Purpose | Type on the virtual keyboard with stick rays | Control the mouse cursor with the sticks |
| Keyboard overlay | Visible | Hidden |
| Sticks | Aim selection rays | Move cursor (X/Y), right stick scrolls (D-pad also scrolls) |
| Default buttons | A=Space, B=Backspace, X=Tab, LB/RB=Left/RightClick (hold), LT/RT=Hold Shift/Ctrl | A=LeftClick, B=RightClick, X=MiddleClick, LB/RB=Left/RightClick (hold), LT/RT=MiddleClick, Y=Toggle keyboard |
| Speed boost | — | Hold mapped SpeedBoost button for multiplier |

### Session lifecycle

1. **Launch** — starts in **mouse mode with input DISABLED and no GUI** (tray icon only). The gamepad belongs to your games.
2. **Enable** — press the built-in enable combo: **Home(PS) + Menu + Select**, or **L3 + R3 + L1 + R1** when the PS/Xbox button is not detectable. Toast confirms "Input ENABLED".
3. Use Y / Menu to switch between keyboard and mouse mode.
4. **Disable** — press **Select/View** (Sony: Create), or uncheck "Input enabled" in tray. All GUI hides instantly; held modifiers and mouse buttons are force-released, so nothing sticks while you switch to a game.

### Y / B mode switching

- In mouse mode, **Y opens the keyboard** (and switches to keyboard mode).
- In keyboard mode, **B returns to mouse mode** — but *only* if the keyboard was entered via that Y. If you opened the keyboard another way, B keeps its mapping (default Backspace).
- Both buttons remain remappable; combos involving Y/B suppress these fixed transitions.

---

## Button actions (bindable per profile)

Every mappable button accepts any of these actions:

- **Hold modifiers**: HoldShift / HoldCtrl / HoldAlt / HoldWin — key held while the button is held (tint on the affected key)
- **Toggle modifiers**: ToggleShift / ToggleCtrl / ToggleAlt / ToggleWin — persistent until toggled off
- **Mouse**: LeftClick / RightClick / MiddleClick / XButton1 / XButton2 — all **hold-to-click** (press-and-hold = button held; supports drag & drop)
- **Mouse wheel**: ScrollUp / ScrollDown / ScrollLeft / ScrollRight; **SpeedBoost** (cursor speed multiplier while held)
- **Keys**: Space, Backspace, Tab, Enter, Escape, Delete, Insert, arrows, PageUp/PageDown, Home/End, CapsLock, NumLock, F1–F12 — sent as taps (edge-triggered)
- **Media/volume**: VolumeUp / VolumeDown / VolumeMute, MediaPlayPause / MediaNext / MediaPrev
- **Keyboard-overlay actions**: CommitLeft / CommitRight (commit the highlighted key on the left/right ray)
- **App control**: DisableInput (free the gamepad for games), ToggleKeyboardMouseMode (switch modes), KeyboardMode / MouseMode, ToggleOverlay / ToggleKeyboard (show-hide keyboard), ToggleLegend, SwitchKeyboardProfile / SwitchMouseProfile (cycle profiles)
- **Any keyboard key**: `Key:<name>` — arbitrary single key press (set via the bindings editor's "Pool for keyboard key…")
- **None** — unbound

### Built-in (non-removable) combos

| Combo | Effect |
|---|---|
| Home + Menu + Select *(or L3+R3+L1+R1)* | Enable input (works while disabled) |
| Select/View | Disable input — GUI hides, gamepad freed |
| L2 + R2 + D-pad ←/→ | Switch keyboard profile (prev/next) |
| L3 (tap) | Move-overlay mode toggle (left stick moves keyboard window) |
| R3 (tap) | Scale-overlay mode toggle (right stick scales 0.5×–2.5×) |
| Y (mouse mode) | Open keyboard (→ keyboard mode) |
| B (keyboard via Y) | Back to mouse mode |

---

## Profiles

Independent profile sets per mode, each with its own bindings:

- **Keyboard profiles**: per-button bindings + per-profile **origin points** (normalized 0–1 X/Y for both sticks), **ray lengths** (normalized), response-curve exponent (finer control near center), custom combos. Switch with L2+R2+D-pad or a mapped action.
- **Mouse profiles**: per-button bindings + custom combos. Switch via mapped action.

Overlay position/scale persists globally (OverlayLeft/Top/Scale) with a 2 s debounce.

---

## GUI (Settings window)

Tray → **Settings…**:

- Key spacing, overlay scale/position, mouse speed + boost, scroll speed, **stick deadzone (0.000–0.5, default 0.005, live-applied)**, legend settings, startup options (run on startup, admin launch), profile toast timing.
- Buttons to open the three editors below.

### Gamepad bindings editor (keyboard mode / mouse mode — separate windows)

- **Profile list**: dropdown + Add / Duplicate / Delete; editable profile name; active profile marked "(active)". Changes persist immediately.
- **Two-column binding list**: left = gamepad button (A, B, X, Y, LB, RB, LT, RT, L3, R3, View, Menu, D-pad×4; keyboard mode also Y+D-pad×4), right = action dropdown from the full action catalog.
- **"Pool for keyboard key…"** as the first option: waits for any keyboard keypress (A–Z, 0–9, F1–F12, arrows, etc.; Esc cancels) and binds it as `Key:<VK>`. Pure modifier presses are ignored.
- **Custom combos section**: "+ Add new custom binding" opens a dialog with **five narrow button dropdowns** + one action dropdown + Cancel/Confirm. Fill 2–5 buttons: the first n−1 are **modifiers held simultaneously**, the **last press triggers the action** (e.g. `LB + Y + A → F5`). While modifiers are held, the last button's own binding is suppressed. Combos are listed with delete (✕) per entry. Stored as `"LB+Y+A=F5"` in settings.json.
- **"Reset to defaults"** button (with confirmation): restores all bindings of the current profile to factory defaults and clears its combos.

### Stick center points editor (keyboard mode)

- Same profile management (add/duplicate/delete/rename).
- **Normalized X/Y sliders** for left (orange) and right (blue) ray origin points — every change **refreshes the points in real time on the virtual keyboard**.
- **Ray length sliders** (0.05–1.00) per stick: 1.0 = the LeftCtrl→Backspace key-distance maximum; live preview.
- Note: editors persist instantly — the Settings window's Cancel cannot undo editor changes.

---

## Toggled-key tint

Keys held or toggled through Hold*/Toggle* bindings get a **semi-transparent green background (#28BE5A)** on the virtual keyboard:

- Toggle* — green until toggled off; Hold* — green only while held
- cleared when input is disabled (all modifiers released); blue ray-highlight border takes priority; state restores after profile switches/rebuilds

---

## Hardware support

- **Windows.Gaming.Input (WGI)** primary path + **raw RawGameController** fallback (used for DualSense, which WGI wraps unreliably) + **XInput** fallback.
- **DualSense (PS5) via USB**: raw path with auto-calibration (stick neutral 0.5 → ±1.0, triggers 0–1), Sony button-order map (Square/Cross/Circle/Triangle, L1/R1, L2/R2, Create/Options, L3/R3, PS, touchpad), D-pad as hat switch.
- Works alongside DS4Windows-style virtual controllers (detected as "Xbox 360 Controller for Windows").
- One-time diagnostics are logged (button labels, axis calibration) to `crash.log` next to the exe; all state changes and input errors are logged there too.
- **Stick deadzone** (default 0.005) applies to all input paths, re-read live every tick.

## Limitations (Windows shell)

- Windows 11 GameInput service / Game Bar can consume gamepad input at shell level (Explorer navigation, guide button) — the app cannot block this without a driver; disable steps: GameInput Service → Disabled, Game Bar / GameDVR / Xbox-mode / Guide toggles off.
- The gamepad's native HID input to Explorer (gamepad UI navigation) cannot be suppressed without a driver-level solution (e.g. HidHide).

---

## Settings.json notes (migration)

Newer versions changed defaults; **saved old values are not overwritten**. If the app behaves oddly after an update, check these in `%APPDATA%\DevelopmentGamepadKeyboard\settings.json`:

- `"Y": "ToggleLegend"` → change to `"ToggleKeyboard"` (or delete the file) — mouse-mode Y opens the keyboard
- `"StickDeadzone": 0.12` → change to `0.005`
- `"StartInMouseMode": false` → change to `true` — launch in mouse mode with input disabled
- Old ray-scale keys (`LeftRayScale`, `RightRayScale`, per-profile `RayScale`) are ignored; use the new per-profile `LeftRayLength`/`RightRayLength` (0–1) in the stick points editor

## Build

```
dotnet build src/GamepadKeyboard/GamepadKeyboard.csproj -c Release
```

Artifact: framework-dependent win-x64 (`.NET 8 Desktop Runtime` prerequisite), zipped by CI as `DevelopmentGamepadKeyboard-win-x64.zip`.

License: MIT