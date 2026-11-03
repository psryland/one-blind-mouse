# One-Blind-Mouse

Point at things on someone else's screen.

One-Blind-Mouse is a portable Windows tray app for screen-share calls. Run it, share the code it shows, and everyone in the session gets a named,
coloured pointer that the others can see on top of their screens. It is a way to communicate, not remote control: pointers never click, type or take focus.

AI agents can use it too, to show you where to click or where something is on your screen.

## Using it

1. Run `OneBlindMouse.exe` (no install, no sign-in). A popup shows your code, e.g. `K7QM-4PZX`.
2. Paste the code into the call chat. The other person runs the app, pastes the code into **Join someone** and presses Enter.
3. **Hold Right Ctrl** and move the mouse to point. While held:
   - left click shows a ripple, left drag draws a laser trail, right click pulses, the wheel changes pointer size;
   - your clicks don't reach your own apps;
   - release the key or press Esc to stop.

Remote pointers appear on the monitor (or window) you choose under **What I'm sharing** in the tray menu. It should match what you share in the call.
The pointer you send is mapped onto the meeting window under your cursor, or onto a frame you draw with **Pointing frames**, so it lands in the same place on the other screen.

Optional: **Open join links with this app** registers `obm://` links for your user, so a shared join link opens the app directly.

## AI agents

`OneBlindMouse.exe mcp` is an MCP server (stdio) that draws pointers, clicks, highlights and arrows on the local screen:

```json
{ "mcpServers": { "one-blind-mouse": { "command": "C:\\Tools\\OneBlindMouse.exe", "args": ["mcp"] } } }
```

`OneBlindMouse.exe mcp --join CODE --name "My agent"` joins a session as a remote member instead, so an agent on another machine can point on your screen.

## How it works

Every instance keeps one outbound WebSocket (TLS, port 443) to a small relay in Azure, so it works behind NAT, proxies and firewalls.
A code is a room id plus a secret; the relay checks it with an HMAC verifier and locks a room after repeated wrong guesses.
Pointer positions are 12-byte frames, expressed as fractions of the receiver's shared area, so screen sizes and DPI don't matter.

See [docs/design.md](docs/design.md) for the full design.

## Repository

| Path | Contents |
|---|---|
| `src/OneBlindMouse.Core` | Protocol, session codes, relay client (platform-neutral) |
| `src/OneBlindMouse.Relay` | WebSocket relay (ASP.NET Core) |
| `src/OneBlindMouse.Windows` | Tray app and MCP server (NativeAOT, Win32) |
| `tests/OneBlindMouse.Tests` | Unit and end-to-end tests |
| `infra/` | Azure Bicep and deployment script |
| `web/` | Landing and join pages (Static Web App) |

## Building

Requires the .NET 10 SDK.

```powershell
dotnet build OneBlindMouse.slnx
dotnet test OneBlindMouse.slnx

# Portable exe (about 6 MB, no runtime needed)
dotnet publish src/OneBlindMouse.Windows -c Release -r win-x64 -o artifacts/publish
```

### Local testing

```powershell
# Run a relay on http://localhost:5199
dotnet run --project src/OneBlindMouse.Relay --urls http://localhost:5199

# Debug builds can use it; --profile lets two instances run side by side
$env:OBM_RELAY_URL = "ws://localhost:5199/v1"
.\src\OneBlindMouse.Windows\bin\Debug\net10.0-windows\OneBlindMouse.exe --profile A
.\src\OneBlindMouse.Windows\bin\Debug\net10.0-windows\OneBlindMouse.exe --profile B --join CODE
```

### Deployment (one-time)

```powershell
./infra/deploy.ps1 -Subscription <name or id> -ResourceGroup rg-one-blind-mouse
```

Then put the printed relay URL and site hostname into `src/OneBlindMouse.Windows/AppConfig.cs`, and set the download link in `web/js/config.js`.
