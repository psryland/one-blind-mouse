---
name: one-blind-mouse
description: Points things out on the user's own screen with One-Blind-Mouse, a portable Windows tray app that draws an agent pointer, click prompts, hand-drawn circles, highlights and arrows over the desktop without clicking or typing. Use this skill whenever you want to show the user where something is, which button or menu item to click, which area of a window to look at, or the order of steps in a UI, for example while guiding them through an application, explaining a screenshot, or answering "where is X?" or "what do I click?". Also use it to obtain, install or register the One-Blind-Mouse tool or its MCP server, or when the one-blind-mouse MCP tools are missing. Do not use it to operate the UI on the user's behalf; it only directs their attention.
---

# One-Blind-Mouse

One-Blind-Mouse (OBM) draws on top of the user's desktop: an amber, labelled agent pointer that glides into place, click ripples, hand-drawn circles,
rectangles and arrows. It is click-through and never clicks or types. It turns "the Save button is in the toolbar near the top right" into an
animation the user cannot miss.

Pointing complements your chat reply; it does not replace it. Say what you are pointing at in text as well, because the annotation disappears.

## 1. Make sure the tool is available

**MCP tools present** (names ending in `point_at`, `show_click`, `circle`, `highlight_rect`, `trace_path`, `clear` and `get_screen_layout`, usually
prefixed `one-blind-mouse-`): use them directly and skip to section 2. The MCP server starts the tray app itself if it is not running.

**MCP tools missing:** find or obtain the exe, then register the server.

1. Look for an existing copy, in this order:
   - `%LOCALAPPDATA%\OneBlindMouse\OneBlindMouse.exe` (the standard location)
   - a running process: `Get-Process OneBlindMouse -ErrorAction SilentlyContinue | Select-Object Path`
2. If there is none, ask the user before downloading, because it is new software that will run on their PC. Then download the latest release:

   ```powershell
   $dir = Join-Path $env:LOCALAPPDATA 'OneBlindMouse'
   New-Item -ItemType Directory -Force $dir | Out-Null
   Invoke-WebRequest 'https://github.com/psryland/one-blind-mouse/releases/latest/download/OneBlindMouse.exe' -OutFile "$dir\OneBlindMouse.exe"
   ```

   The exe is portable (no installer) and self-contained (no .NET runtime needed). Releases: https://github.com/psryland/one-blind-mouse/releases.
   Windows SmartScreen may warn on first run because the exe is not code-signed; the user must accept that themselves.
3. Ask before editing the global MCP config. Back it up, then add this entry under `mcpServers` in `~\.copilot\mcp-config.json`:

   ```json
   "one-blind-mouse": {
     "type": "local",
     "command": "C:\\Users\\<user>\\AppData\\Local\\OneBlindMouse\\OneBlindMouse.exe",
     "args": ["mcp"],
     "tools": ["*"]
   }
   ```

   Use the real expanded path; the config does not expand environment variables. MCP servers load when a session starts, so the tools appear only in new
   sessions.
4. **Until then**, call the same tools from the shell with the bundled script. It runs `OneBlindMouse.exe mcp` for one call and prints the result:

   ```powershell
   $obm = '<this skill directory>\scripts\obm.ps1'
   & $obm get_screen_layout
   & $obm show_click @{ x = 1520; y = 84; label = 'Click Save' }
   ```

   The `circle` tool and the gliding pointer need v1.1.0 or later. On older versions use `highlight_rect` instead of `circle`, and upgrade by downloading
   again (exit the tray app first, because a running exe is locked).

   The MCP server forwards calls to whichever tray app is already running, which may be an older copy elsewhere (for example in Downloads). An
   `Unknown command` error for a listed tool means that: find it with `Get-Process OneBlindMouse | Select-Object Path` and ask the user to exit it from
   its tray menu.

## 2. Get the coordinates right

All local tools take **physical screen pixels** in the Windows virtual-screen space. Monitors can sit at negative coordinates, and each can have its own
display scale. Wrong coordinates are the main way this goes badly: a confident arrow at the wrong thing is worse than no arrow.

Call `get_screen_layout` first. It returns each monitor's `bounds`, `work_area` and `scale`, the `cursor` position and the `foreground_window` client
rectangle, all in physical pixels.

Convert positions from where you found them:

- **A screenshot of a window:** screen = window origin + image pixel × (physical window width ÷ image width). Use the window's client rectangle from
  `get_screen_layout` (if it is the foreground window) or from the tool that took the screenshot. Screenshots are often downscaled, so never use image
  pixels directly.
- **UI automation element bounds** (for example from computer-use `get_window_state`): check whether they are physical or logical pixels by comparing a
  known element, such as the window itself, with `get_screen_layout`. Multiply logical pixels by that monitor's `scale`.
- **A full-desktop screenshot:** scale by the virtual screen size and offset by its origin.

Sanity-check before drawing: the point should fall inside a monitor's bounds and inside the expected window. If unsure, `point_at` the element with a
label such as "This one?" and ask, rather than drawing a confident click prompt.

## 3. Pick the right annotation

| Intent | Tool | Notes |
|---|---|---|
| "Click this" | `show_click` | Glides there and plays click ripples. Label with the action, e.g. "Click Save". |
| "This is where X is" | `point_at` | Glides there and pulses. Label with the name, e.g. "Build log". |
| "Look at this" | `circle` | Give the bounds of the thing; the circle is drawn just outside them. Best for an item or small group. |
| An exact region, e.g. a panel or table | `highlight_rect` | A precise rectangle outline with a label. |
| A drag, or an order of steps | `trace_path` | An arrow along `[[x, y], ...]` from the first point to the last. |
| Remove everything | `clear` | Hides the pointer and all annotations. |

There is one agent pointer. Each `point_at`, `show_click` or `circle` moves it from where it was (or from the user's cursor when it is hidden), so the
user's eye follows it. For a sequence of steps, show one step at a time and wait for the user to do it, or pause between calls, rather than firing them all
at once.

Options shared by most tools:

- `label`: a few words. Long labels cover the thing you are pointing at.
- `duration`: seconds to keep the annotation after it arrives; the default is 5. Use 0 to keep it until `clear`, for example while the user reads a
  long explanation, then call `clear` when you move on.
- `colour`: `#RRGGBB`. Keep the default amber unless you need to tell several annotations apart.

## 4. Be a good guest on the user's screen

- Point only when it helps the user find something. Do not decorate every answer.
- Clear persistent annotations (`duration: 0`) once they are no longer relevant.
- The overlay is visible in screen shares and recordings, which is intended. Keep labels free of anything private.
- OBM also supports multi-person sessions (`get_session`, `join_session`). Only join a session when the user gives you a code and asks you to.
