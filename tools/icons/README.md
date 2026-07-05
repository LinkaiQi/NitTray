# NitTray icons

`generate_icons.py` draws NitTray's brand mark — a monitor with a glowing
brightness sun on its screen (a nod to the *nit*, the unit of luminance) — and
writes the production assets the app consumes.

## Output (`src/NitTray/Assets/`)

| File | Use |
| --- | --- |
| `app.ico` | app / window / taskbar icon (16–256, multi-size) |
| `tray-light.ico` | white tray glyph, for **dark** taskbars |
| `tray-dark.ico` | dark tray glyph, for **light** taskbars |

The tray glyph ships in two tones; `IconFactory` picks the one that contrasts
with the current taskbar theme at runtime.

## Regenerate

```bash
python3 -m pip install Pillow
python3 tools/icons/generate_icons.py
```

The script is the source of truth for the art: tweak the colors/geometry at the
top of `app_icon()` / `tray_glyph()` and re-run. Output is deterministic
(supersampled 4× and downsampled), so re-running without changes reproduces the
same bytes.
