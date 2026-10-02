# Key Maps Mode (DirectInput) — Spec v1

New `MapperMode` value alongside Keyboard/Mouse (enum value `DirectInput`; action name: `KeyMapsMode`).
Concept: gamepad-as-keyboard via **maps** of key assignments + modifier keys, for fast typing.

## Modifiers (special use — excluded from per-map bindings)
- `L2` = Shift (hold)
- `L1` = Ctrl (hold)
- `R1` = Alt (hold)
- `R2` = **Maps key** (hold): freezes Shift/Ctrl/Alt/Windows in their current state? NO — R2 locks nothing; R2 selects maps. While R2 is held the current map switches to **Symbols Map 1**; combining:
  - R2 alone → Symbols Map 1
  - R2 + R1 → Symbols Map 2
  - R2 + L1 → Symbols Map 3
  - R2 + L1 + R1 → Function Keys Map
  - R2 released → back to Utility Map
- `Windows key` is a mappable key (Utility map: RightStickPress), NOT a modifier selection input.

## Maps (defaults; every binding user-editable later — settings UI is a LATER task)
Key naming: use existing input-sender key vocabulary.

### Utility Map (default, active when R2 not held)
- Select = (unbound), Start = **Switch to mouse mode** (reuse MouseMode action)
- DPad: U=ArrowUp D=ArrowDown L=ArrowLeft R=ArrowRight
- Y=Delete, A=Enter, X=Space, B=Backspace
- LeftStick: U=PageUp D=PageDown L=Home R=End; Press=**CapsLock toggle**
- RightStick: U=Insert D=PrintScreen L=Escape R=Tab; Press=**Windows key**

### Symbols Map 1 (R2)
- DPad: W/S/A/D; Y=I A=K X=J B=L
- LeftStick: U=E D=Z L=Q R=C; Press=(unbound)
- RightStick: U=U D=M L=O R=P; Press=(unbound)

### Symbols Map 2 (R2+R1)
- DPad: R/C/F/T; Y=Y A=B X=G B=H
- LeftStick: U=`+` D=`-` L=`[` R=`]`; Press=`.`
- RightStick: U=`'` D=`` ` `` L=V R=N; Press=`,`

### Symbols Map 3 (R2+L1)
- DPad: 1/2/3/4; Y=5 A=6 X=7 B=8
- LeftStick: U=0 D=`,` L=`'` R=`;`; Press=`/`
- RightStick: U=9 D=`.` L=`-` R=`=`; Press=`\`

### Function Keys Map (R2+L1+R1)
- Select=ScrollLock, Start=PauseBreak
- DPad: F1/F2/F3/F4; Y=F5 A=F6 X=F7 B=F8
- LeftStick: U=F9 D=F10 L=F11 R=F12; Press=**XButton1** (mouse back)
- RightStick: U=VolumeUp D=VolumeDown L=(browser stop) R=(play/pause); Press=**XButton2** (mouse forward)

## Behavior
- Modifier keys (Ctrl/Shift/Alt) held via L1/L2/R1 combine with map keys on release/press (same modifier semantics as keyboard mode).
- Sticks use edge-triggered taps at deflection thresholds (like keyboard mode stick→ray or 8-way), NOT continuous repeat; LStick press / RStick press are button presses.
- Mode entry: `KeyMapsMode` action binds like KeyboardMode/MouseMode; mode name shown in overlay.
- Overlay (WPF, shown only while input enabled AND mode==DirectInput AND overlay setting on):
  - Laid out like a gamepad; each map's keys shown; **modifier row at top**: `[Ctrl] [Shift] [Alt] [Windows] CurrentMapName` — modifiers highlight while held/locked.
  - **Shadow keys** shown while R2 is held: L1-combo map keys to the LEFT of active keys, R1-combo RIGHT, L1+R1-combo BELOW; ~50% transparent; opaque while their combo is held; when a combo map becomes active, swap opacities (previous keys become shadows, chosen map becomes full) — positions FIXED, allkeys visible.
  - Shadow labels list the keys of that combo's map (from the map's own positions).
- Log: one `key maps mode: entered` line on entry; per-map flips only at most 1 line (rate-limited 1/s) — respect log-size policy.

## Engineering constraints (mandatory)
- No allocs in the hot tick path; snapshot structs; edge-trigger state machines per button/stick.
- Sends via InputSender (Dispatch is zero-alloc `fixed` + `SendInputUnsafe` w/ `EntryPoint="SendInput"`).
- NEVER let exceptions escape into the poll tick — swallow+log, one line, rate-limited.
- Any new P/Invoke: explicit `EntryPoint` matching the real export; sweep all DllImports before commit.
- Styles/look consistent with existing InputMonitorWindow/keyboard overlay (rounded rects, subtle glow on active).
- Files: new `Input/KeyMapsMapper.cs`(+ overlay `UI/KeyMapsOverlayWindow.cs`); wire into `ControllerMapper.Process` switch (replace DirectInput stub), `AppOrchestrator` overlay orchestration, `BindingsEditorWindow` action list ("KeyMapsMode"), `AppSettings` (per-map binding tables, serialized, defaults per spec above), DOCS.md.
- C# style: descriptive names, no abbreviations, no `var`, always braces, no #region (use `// ── Section ──`), nested types at bottom, `nameof()` where expressions fit.