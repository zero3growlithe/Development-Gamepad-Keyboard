# AGENTS.md — notes for coding agents working on this repo

Development Gamepad Keyboard (DGK): WPF net8.0-windows app that maps a gamepad
to keyboard/mouse. Two big subsystems: the classic **keyboard overlay** (mouse +
keyboard modes) and **Key Maps mode** (on-screen board of key maps driven by a
gamepad chord). This file is agent-facing operational memory; user-facing docs
live in `README.md` / `DOCS.md`.

## Hard rules

- **CI is the only build authority.** Dev host has NO .NET SDK and must never
  get one. Never attempt `dotnet build` locally — push and let
  `.github/workflows/build.yml` compile. Static checks only: brace balance,
  cross-file signature grep, symbol-reachability audits.
- **C# style (user's, enforced):** descriptive names, NO `var`, always braces
  (even single-statement `if`), no `#region` (use `// ── Section ──`), nested
  types at the bottom, `nameof()` where it fits. `Nullable` enable,
  `LangVersion 12`.
- **No per-tick allocations and no per-tick disk I/O** in poll paths
  (`ControllerMapper.Process`, `KeyMapsMapper.ProcessTick`,
  `GamepadService.PollLoop`, overlay `Update` passes). Settings persist via the
  orchestrator's 2 s `_settingsDirty` debounce timer (`AppSettings.Save()` does
  content-hash dedupe, but call sites should still use the dirty flag while
  sticks are held — see `ApplyKeyMapsAdjust`).
- **Cache/freeze `Brush`es** in overlay code (static frozen brushes; never
  build brushes per frame).
- Telegram delivery: user is NOT on this machine — deliverable files must go
  out via MEDIA, not just saved locally.

## Build / CI mechanics

- Workflow: `.github/workflows/build.yml` → single job `build` on
  `windows-latest`, `Publish` + `Compress-Archive` → artifact
  **`DevelopmentGamepadKeyboard-win-x64`** (the zip inside;
  `DevelopmentGamepadKeyboard.exe` entry point, framework-dependent, needs
  .NET 8 Desktop Runtime).
- Build SHA is injected into the exe: `WriteBuildInfo` target writes
  `BuildInfo.g.cs` from `$(GITHUB_SHA)` — the crash log prints
  `build: <sha>`.
- CI log analysis: job logs endpoint
  `GET /repos/zero3growlithe/Development-Gamepad-Keyboard/actions/jobs/{job_id}/logs`
  302-redirects to a signed URL that **rejects the Bearer header** — follow
  `Location` manually (http.client) and re-request WITHOUT auth.
- Push auth: `git -c http.extraheader="AUTHORIZATION: base64(user:token)"`
  from the token in the protected local env file. Never echo the token.

## Architecture map

- `AppOrchestrator.cs` — composition root, tray, UI tick fan-out, HidHide
  claim lifecycle, settings debounce. **`OnPad()` is the hot path**: runs
  `_mapper.Process(s)` per poll, then queues coalesced 60 Hz UI work. NOTE the
  two separate UI paths: keyboard overlay goes through `RefreshUiCore()`, Key
  Maps overlay goes through its own `_keyMapsOverlay.Update(_mapper)` dispatch
  — anything Key-Maps-related must hook the Key Maps queue, NOT
  `RefreshUiCore` (the move/scale adjust block was dead for months because it
  sat in the wrong queue; now extracted to `ApplyKeyMapsAdjust()` called in
  the Key Maps dispatch).
- `ControllerMapper.cs` — mode state machine (`Keyboard`, `Mouse`,
  `DirectInput`=Key Maps), profile-binding dispatch, combos
  (`EvaluateCombos`/`DispatchKeyMapsAppCombo` — when mirroring the combo
  recipe, port ALL blocks: mute branch must release `HoldLast`, releases use
  `state.Action` not `binding.Action`, guard `Buttons.Count < 2`).
  **Level-relay rule:** actions that mirror a held level into another
  subsystem (`MapsModifierHold/Toggle` → `KeyMapsMapper` flags) must dispatch
  on BOTH edges — placing the case below a `if (!edge) return;` gate latches
  the level forever.
- `Input/KeyMapsMapper.cs` — Key Maps engine: chord selection
  (`SelectMapForChord` = nearest-subset over `CurrentChordMask`), system
  buttons L2/L1/R1 with configurable actions + locks logic
  (`UpdateSystemButton`: chord-member freeze, pre-held latch, re-press unlock
  honoring `MitigateLock`), slot → key delivery with suppression gates
  (`boardOwnsInput` = move-mode owns the pad). Maps key sources, priority:
  sticky toggle (`MapsModifierToggleRequested`), hold
  (`MapsModifierRequested`), physical RT (legacy default). A system button
  whose action IS a maps-modifier is the maps key itself — stripped from
  chords.
- `Input/GamepadService.cs` — polling + `GamepadSnapshot` struct; allocation
  only on device change.
- `UI/KeyMapsOverlayWindow.cs` — the Key Maps overlay (atom view + projected
  keyboard share one window). `Update(mapper)` per 60 Hz tick. Press
  visuals go through `IsSlotPressed`/`IsSlotPressedProjected` — both mute
  while move/scale mode is active (mirrors the dispatch suppression).
  Frozen brushes, fingerprint-gated rebuilds (`LayoutFingerprint` /
  `PromptFingerprint`), `_bindingsDirty` invalidation.
- `UI/KeyMapsBindingsEditorWindow.cs` — bindings editor; first tab
  "System buttons & open combinations" (pseudo-map `__system__`): action
  dropdowns with live "Mitigate lock" checkbox refreshers, open-combo
  checkbox lists; a button mapped to a maps-modifier action grays out of
  every map's combo list and gets stripped from `OpenWith`.
- `UI/SettingsWindow.cs` — main settings; Keyboard Maps tab owns layout
  sliders/toggles (backup/restore around Cancel). Does NOT duplicate the
  system-button/bindings config (it lived here once and was removed as
  redundant — keep it only in the bindings editor).
- `Settings/AppSettings.cs` — whole settings model. Key Maps specifics:
  `KeyMapsSettings` (system-button actions + `*MitigateLock` bools),
  `KeyMapDefinition` (18 slots + `OpenWith` chord string, canonical order
  `L2,L1,R1,L3,R3`), `KeyMapsLayoutSettings` (visual sliders). Stale JSON
  keys are safely ignored by System.Text.Json — removing a property is a
  non-breaking change.

## Action renames (2026-10, do not regress)

- Canonical ids live in `ControllerMapper.NormalizeAction`; legacy saved ids
  (`LeftClick`, `SpeedBoost`, `SubmitLeft`, `CommitLeft`, `PointerMode`,
  `ToggleKeyboardMouseMode`, `KeyboardMode`, `MouseMode`, `KeyMapsMode`,
  `DirectInputMode`, `ToggleKeyMapsMoveMode`, `ToggleMoveScaleKeyboard`,
  `ToggleMoveMode`, `ToggleScaleMode`) normalize at dispatch boundaries
  (`DispatchButton` entry, `KeyMapsMapper.ResolveSlotKeyName`, editor value
  reads). Editors list ONLY canonical ids — preselect legacy values via
  `NormalizeAction`.
- Removed actions (`CycleInputMode`, `TogglePreviewMaps`,
  `HoldPreviewMaps`, `ToggleKeyboard`) dispatch as safe no-ops; the Preview
  Maps feature itself was deleted (mapper property + projected overlay
  plumbing).

## Key Maps semantics (do not regress)

- Maps key = chord root; combos = subset of {L2, L1, R1, L3, R3} per map,
  matched nearest-subset so `{}`, `{RB}`, `{LB}`, `{LB+RB}` all reproduce.
- While the maps key is held: combo-member buttons freeze their configured
  action's current state (pre-held → latched down; re-press while held frees
  it) unless `MitigateLock` is on for that button, then it may be used
  freely; non-member buttons work normally.
- Move/scale mode (`ToggleMoveScale`, context-aware: Key Maps board vs
  keyboard overlay; legacy ids `ToggleKeyMapsMoveMode` /
  `ToggleMoveScaleKeyboard` normalize to it): right stick moves the overlay
  window, left stick scales via `KeySize` quantized 0.05 (0.6–2.0); the
  toggle's own binding stays dispatchable so the mode can be exited.
- Atom view: 18 atoms (D-Pad 4, Face 4, sticks 5+5); atom = center prompt +
  3 quarks + physical-button icon; green = physical press, amber = active
  for use; R2 held reveals quarks.
- Face colors: A=green, Y=yellow, X=blue, B=red.
- Icon system: L1/L2/R1/R2 use RECTANGLE-ring glyphs
  (`MakeShoulderGlyph`); L3/R3 + faces keep circle rings (`MakeGlyph`);
  Select/Start show word glyphs (`MakeWordGlyph`) — never bespoke elements,
  always `KeyMapsAtom.MakeIcon` so scaling/offset settings apply. Prompt
  icons center on their key/chip (badge X = center − iconSpan/2).

## Known pitfalls (learned the hard way)

- `IsSlotPressed` reads the RAW snapshot — visual state and actual dispatch
  can disagree; any new "mute input" feature must mute BOTH the dispatch
  (mapper) and the visuals (both `IsSlotPressed*` helpers), or the UI will
  lie to the user.
- `ProcessKeyMaps` recomputes `CurrentChordMask` before chord selection each
  tick; `SelectMapForChord` is an instance method on purpose (reads the
  field).
- Settings ctor runs JSON repair per `KeyMapDefinition`; the (name)
  constructor lays spec defaults first, then file values overwrite — keep
  new slot properties in `ApplyDefaults` AND the repair probe.
- Key repeat: KeyDown/KeyUp do NOT trigger Windows key repeat (documented in the
  classic KB article about synthetic keys). "Simulate key repeat" (Keyboard tab,
  off by default) makes InputSender track held repeatable keys and send repeat
  key-downs per the OS schedule (SystemParametersInfo SPI_GETKEYBOARDDELAY=0x16 /
  SPI_GETKEYBOARDSPEED=0xA); modifiers/toggles never repeat. PumpKeyRepeats() runs
  on every mode tick (keyboard, mouse, Key Maps); ClearKeyRepeats() on every ReleaseAllModifiers.
- Mode ping-pong fix: ReleaseAllModifiers re-latches still-held single-button
  binding edges (RelatchHeldBindingEdges) so an app-level action bound in TWO modes
  (Start = SwitchBetweenKeyMapMouseMode) fires once per press, not once per mode.
- GitHub Actions artifact download: 302 → signed URL rejects Bearer →
  follow Location manually.
- `InputSender.MouseMove` default = plain RELATIVE move (classic behavior).
  Only when `AppSettings.UseAbsoluteMouse` ("Use absolute mouse (remote
  desktop fix)", Mouse tab, default OFF) inject the pair: relative move
  FIRST, then ABSOLUTE|VIRTUALDESK re-anchor, both with `MOUSEEVENTF_MOVE`,
  in ONE `SendInput` call. Pure relative = Parsec doesn't track the cursor;
  pure absolute = remote clients smooth the teleports into momentum (brief
  diagonal wobble after fast movement). Never enable absolute unconditionally.
- Analog hysteresis: dispatch-time held tests for triggers/stick-directions go
  through HeldWithHysteresis (enter ≥0.55, exit <0.45). Raw 0.5-threshold checks
  flap around the line while a user holds an analog input lightly → key
  down/up spam at poll rate that looks like instant hyper "key repeat".
- Discrete scroll actions (ScrollUp/Down/Left/Right, DPad) go through
  HeldScroll throttle: 25 notches/s max, first hold-tick fires once
  immediately; raw per-tick dispatch = 250 WM_MOUSEWHEEL events/s. Analog
  scroll (AnalogScroll*) stays smooth per-tick by design.
- Active combos CONSUME their buttons for the frame (`_comboConsumedButtons`):
  DispatchSingleBindings skips consumed buttons but keeps tracking
  `state.Previous` (hysteresis) so no phantom edges fire on combo exit —
  the combo owns the action while prefix+last are held, the single binding
  must stay silent (DUp=ScrollUp beside RT+DUp=PageUp case). Removal line
- Select/Start icon = word-only glyph (MakeWordGlyph, KeyMapsAtom): ring removed + 7.5px text; the old 6px-in-ring clipped to unreadable fragments at small scales.
- KeyboardOverlay NO LONGER draws KeyMaps-trigger modifier badges (AttachModifierBadge/RefreshModifierBadges REMOVED, _modifierBadges field gone) — binding prompts cover modifiers; the old path leaked Maps-mode badges into Keyboard mode.
- Keyboard-prompt geometry is INDEPENDENT of Key Maps layout sliders: `AppSettings.KeyboardPromptOffsetX/Y` (±100/±60 px) + `KeyboardPromptIconScale` (0.3–3.0) own Keyboard-mode prompt positioning/sizing; Keyboard prompt icons NEVER read `KeyMaps.Layout.IconScale`/`PromptOffset*`. SettingsWindow builds these with `MakeKeyboardPromptSliderHost` (live apply on ValueChanged: write + Save + `NotifyMappingsChanged`; Save() also writes them authoritatively on OK).
- "Show button prompts" (Keyboard tab, `AppSettings.ShowKeyboardButtonPrompts`, default OFF):
  Keyboard-mode pad-button prompts in Maps-Mode style — `ControllerMapper.CollectKeyboardPrompts`
  classifies ACTIVE keyboard-profile bindings: on-layout key VKs get icon badges (right-upper
  corner, `KeyMapsAtom.MakeIcon`, PromptIconScale×IconScale, PromptOffset, chain ≤3 icons
  leftward); off-layout VKs (Vol+/−/Mut, media, NumPad) render as extra info tiles right of the
  grid (rows 0..5, `VkLabel` names); app-level actions render [icon label] list UNDER the
  grid (wraps at keyboard width, camel-case split labels). Hold*/Toggle* actions resolve to
  their modifier-family VK. Rebuild triggers: RebuildKeys (all paths) + cheap
  `RefreshBindingPromptsIfDirty()` fingerprint (profile/spacing/opacity/bindings) called from
  RefreshUiCore; SizeToContent extends _baseW/_baseH by `_promptExtraDefs`/`_promptListHeight`.
  When enabled, the legacy Maps-trigger modifier badge on Shift/Ctrl/Alt/Win is hidden for
  keys that binding prompts already cover (restored visibly when the option turns off).
- "Show special keys" (Keyboard tab, `AppSettings.ShowSpecialKeys`, default OFF):
  `KeyboardLayout.Build(bool)` is REBUILDABLE on the SAME instance — orchestrator holds
  `_keyboardLayout` and re-Builds it in `NotifyKeyboardLayoutChanged()` (keyboard overlay +
  ray targeting read Keys/GridW live); the projected Key Maps keyboard re-Builds via the
  `_projectedSpecialKeys` flag + ShowSpecialKeys term in PromptFingerprint. GridW grows
  16 → 19.25 (PrtSc/ScrLk/Pause at 13..15 on row 0 WITHOUT gaps; nav block at 16.25..18.25:
  Ins/Home/PgUp, Del/End/PgDn, arrows ↑(row4) ←↓→(row5)). ScrollLock (VK_SCROLL=0x91) joins
  CapsLock as an OS-toggle tint (NativeMethods.ScrollLockActive).
- Stuck-slot guard (Key Maps): the mapper tracks every key/mouse button a
  SLOT sent down (`_downSlotKeys`); when the selected map changes
  (maps-key chord selects another map while a press is still held, the
  release edge then re-routes to the NEW map's slot) or ReleaseAll runs,
  everything in that registry gets KeyUp/mouse-up in one sweep (`ReleaseDownSlotKeys`)
  — otherwise the old map's key stays down and the repeater echoes it forever.  is 0.45 / enter 0.55 — analog prefixes must not flap the set.
- Rate limits: GitHub API 403s come fast with tight polling; watchers use
  ≥120 s backoff.

## Delivery convention

Commits straight to `main`. After push, watch the **Build** run for the push
SHA (query `/actions/runs?head_sha=<sha>` — don't trust "latest run"), verify
`conclusion=success`, download + extract the artifact zip, deliver to the
user via Telegram MEDIA in Polish.

## Parsec host: injected keyboard swallowed → Interception mode

Parsec host's low-level keyboard hook eats keys that arrive with the
LLKHF_INJECTED flag while a session is connected (AutoHotkey "Sends on the
host" fails the same way; mouse injection is NOT filtered — that's why mouse
mode kept working). Our SendInput keys therefore vanish on a Parsec host.

Fix (Keyboard tab): "Send keys through the Interception driver"
(`AppSettings.InterceptionKeyboard`, default OFF + `InterceptionDllPath`
optional). `Native/InterceptionKeyboard.cs` = lazy singleton: loads
`interception.dll` (configured path → exe dir → `C:\Interception\library\x64`
→ PATH), binds `interception_create_context/_destroy_context/_send/
_get_hardware_id`, picks the first keyboard slot (1..10) with a hardware id,
sends `InterceptionKeyStroke { ushort code; ushort state; uint info }`
strides of 8 B in one `send` call (TapKey = 2 strokes, nstroke=2), falling
back to SendInput per key on ANY failure (10 s rate-limited re-probe after a
failure; repeat-pump follows the path the hold started on).

Scancodes: `MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC_EX=4, GetKeyboardLayout(0))`
— high byte 0xE0 → state|=KEY_E0(0x02) (matches the caller's `extended`
flag too), 0xE1 (Pause) → falls back. States follow ntddkbd KEYBOARD_INPUT_
DATA.Flags: make=0, break=1 (NOT the header enum's DOWN=0/UP=1 confusion —
UP is the bit 0x01).

Driver install (user, once): github.com/oblitum/Interception →
`command line installer\install-interception.exe /install` → REBOOT; copy the
zip's `library\x64\interception.dll` next to the exe (or any probed path).
DLL/driver are NOT bundled (license + it's the user's machine). Caveat:
Memory Integrity (Core isolation) can block this old driver on some Win11s.

## Keyboard overlay above the Start menu = UIAccess build

Win10/11 Start menu (StartMenuExperienceHost/SearchHost) lives in a shell
z-band (`ZBID_IMMERSIVE_MOGO`) above every classic `HWND_TOPMOST` window; any
topmost overlay is hidden under Start. SetWindowBand/CreateWindowInBand from
a normal process always fail (ACCESS DENIED — only the OS/IAM keys); the ONE
supported workaround is a UIAccess token: Authenticode-signed exe +
`uiAccess="true"` manifest + secure location (Program Files) → its windows
(incl. our topmost overlay) automatically land in `ZBID_UIACCESS`, the
highest z-band — above Start, Task Manager, everything. Same mechanism as
osk.exe. Implementation: `app.uiaccess.manifest` (copy of app.manifest,
`uiAccess="true"`) published by CI via
`-p:ApplicationManifest=app.uiaccess.manifest` into the zip's `uiaccess\`
subfolder; `installer/Drivers-installer.bat` → `Drivers-installer.ps1`
self-elevates, stops running instances, copies to Program Files, creates a
self-signed CodeSigningCert (reuse by exact Subject match
`CN=Development Gamepad Keyboard UIAccess`, 20 y validity), imports it into
LocalMachine\Root, `Set-AuthenticodeSignature`, marks status Valid, creates
the Start-menu shortcut. Unsigned/misplaced uiAccess exe = does not start
(ERROR_INVALID_IMAGE_HASH 577) — the zip keeps the stock (uiAccess false)
build at root. No app-code z-order changes needed (band is per-process at
window creation). Caveat: in the UIAccess process EVERY window floats above
normal topmost windows (band ordering dominates) — Settings too; acceptable.
Single-instance mutex `DevelopmentGamepadKeyboard_SingleInstance` is shared
by both flavors (mutual exclusion, no double-input).

## Installer v2 — optional Interception driver step (2026-10-16)

`installer/Drivers-installer.ps1` takes `-UiAccess` / `-Interception` switches
(both = both features) and asks interactively when neither is passed; the
choice is resolved BEFORE the UAC relaunch and re-passed through the elevated
command line (`-Wait` keeps the flow synchronous; final `Read-Host` keeps the
elevated window open). A Y/N question no longer exists — see "Installer v4".
Driver step: `Interception.zip` v1.0.1 (official author release
`https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip`,
LGPL non-commercial binaries — NOT bundled in the repo/zip) — prefers an
`Interception.zip` dropped next to Drivers-installer.bat (offline path), else
TLS12 + Invoke-WebRequest -UseBasicParsing download to
%TEMP%\DevelopmentGamepadKeyboard-Interception, Expand-Archive, verify members, run
`command line installer\install-interception.exe /install` (silent; drivers
load at BOOT → print REBOOT REQUIRED), copy `library/x64/interception.dll`
to BOTH Program Files install dir and the zip extraction dir ($PSScriptRoot,
app probes exe dir first). Step failures are NON-fatal to the UIAccess
install (try/catch, prints manual-download instructions). Zip layout facts:
top-level root folder `Interception/`, single `install-interception.exe`
handles /install and /uninstall.

## Installer v4 — renamed pair + Driver uninstaller (2026-10-27)

The installer pair was renamed and now covers both directions; the CI workflow
copies all FOUR files into the zip root (that `Copy-Item` list is the usual
casualty of a rename — a missing one ships a batch that calls a script which is
not in the zip):

- `installer/Drivers-installer.bat` → `Drivers-installer.ps1` — ONE
  `choice /c:123Q /n` menu: 1 = UIAccess, 2 = Interception driver, 3 = both,
  Q = quit. Each branch passes `-UiAccess` / `-Interception` /
  `-UiAccess -Interception` to the ps1; the ps1 accepts the same switches
  directly and asks 1/2/3/Q itself when given none.
- `installer/Driver-uninstaller.bat` → `Driver-uninstaller.ps1` — identical
  menu semantics in reverse.

Ordering rules that keep the flows honest: the UIAccess source check
(`uiaccess\DevelopmentGamepadKeyboard.exe` next to the script) runs INSIDE the
`if ($doUiAccess)` block, so an Interception-only install works from any folder;
the Interception step copies `interception.dll` into the Program Files dir only
when `Test-Path $InstallDir` (an Interception-only install has no installed
copy), and always into `$PSScriptRoot`.

Uninstaller specifics: it stops `DevelopmentGamepadKeyboard`, deletes the
Start-menu shortcut and the Program Files dir, then walks
`Cert:\CurrentUser\My` + `Cert:\LocalMachine\My` + `Cert:\LocalMachine\Root`
matching the EXACT Subject `CN=Development Gamepad Keyboard UIAccess` and
removes each hit (the root trust is what would otherwise keep the machine
trusting a cert nobody uses). The driver side runs
`install-interception.exe /uninstall` from the same offline/downloaded package
as the installer and deletes the `interception.dll` copies; a nonzero exit code
is reported as "may simply mean the driver was not installed", not as a
failure. Both paths end with `Read-Host 'Press Enter to close'` — an elevated
console closes on exit and flashing output reads as a crashed uninstaller.
Removing the driver needs a reboot to unload it; the app falls back to
SendInput on its own once the toggle is off.
