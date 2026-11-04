# Copilot Instructions — One-Blind-Mouse

## Project Overview

One-Blind-Mouse is a portable Windows tray app that shows remote people's (and AI agents') named, coloured pointers on the local screen during screen-share calls.
Instances connect through a cloud WebSocket relay using a short shared code. It is a communication aid, never remote control. See `docs/design.md`.

## Architecture

- `src/OneBlindMouse.Core/` — Platform-neutral protocol, `PointerFrame` (12-byte binary), `SessionCode`, palette, name sanitising, `PointerSmoother`, `RelayClient`
- `src/OneBlindMouse.Relay/` — ASP.NET Core WebSocket relay on Azure Container Apps (in-memory rooms, at most one replica, scales to zero; image on GHCR)
- `src/OneBlindMouse.Windows/` — NativeAOT tray app: Win32 P/Invoke + GDI+ (no UI framework), overlay sprite windows, input hooks, local named-pipe API and MCP server
- `tests/OneBlindMouse.Tests/` — xUnit tests, including end-to-end tests against an in-process relay
- `infra/` — Bicep and `deploy.ps1`
- `web/` — Static landing and join pages (no build step)

## Naming Conventions

- Use `snake_case` for fields, member variables, and local variables
- Prefix class members with `m_`, static fields with `s_`
- Do NOT use `camelCase`
- Use `PascalCase` for class names, method names, and public properties
- Enums use an `E` prefix (e.g. `EPointerFlags`)

## Coding Style

- Tabs for indentation, Allman braces, file-scoped namespaces
- Prefer `var` over explicit types
- Switch on enums with a `default:` arm that throws
- Comments explain "why", not "what"; prefer a blank line before comment blocks

## Key Design Decisions

- No NuGet packages in Core or the Windows app; only built-in .NET APIs. The Windows app must stay NativeAOT and trim-safe (no reflection-based JSON; use the source-generated contexts)
- Validate all network input strictly, on both the relay and the client (lengths, ranges, known message types, sanitised names and labels)
- Positions are fractions of the receiver's shared area; the receiver owns the coordinate space
- The relay URL is compiled into the app. Only Debug builds accept the `OBM_RELAY_URL` / `OBM_WEB_URL` overrides
- Links only ever carry a session code; the `obm://` handler is opt-in and HKCU only
- Overlay windows are click-through and never activate; pointing never injects input

## Build Commands

```powershell
dotnet build OneBlindMouse.slnx
dotnet test OneBlindMouse.slnx
dotnet publish src/OneBlindMouse.Windows -c Release -r win-x64 -o artifacts/publish

# Local relay for testing (Debug app with $env:OBM_RELAY_URL = "ws://localhost:5199/v1")
dotnet run --project src/OneBlindMouse.Relay --urls http://localhost:5199

# Infrastructure (one-time)
./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse
```
