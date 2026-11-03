using Microsoft.AspNetCore.HttpOverrides;
using OneBlindMouse.Core;
using OneBlindMouse.Relay;

// One-Blind-Mouse relay: forwards control messages and pointer frames between members of a room.
// All state is in memory, so the relay must run as a single instance.
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Hub>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
	// Trust assumption: in Azure Container Apps the container is reachable only through the platform ingress, which appends the client
	// address to X-Forwarded-For. ForwardLimit = 1 takes only the right-most entry (the one the ingress added), so a client cannot spoof
	// its address by sending its own header. The ingress has no fixed address, so the known proxy lists are cleared.
	options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
	options.ForwardLimit = 1;
	options.KnownIPNetworks.Clear();
	options.KnownProxies.Clear();
});
var app = builder.Build();

// Only honour forwarded headers when hosted behind the Container Apps ingress (which sets CONTAINER_APP_NAME).
// Anywhere else the header could come straight from a client and would let it dodge the per-address limits.
if (Environment.GetEnvironmentVariable("CONTAINER_APP_NAME") is { Length: > 0 })
	app.UseForwardedHeaders();

// Keep-alive pings detect dead clients so their slots are held and released promptly
app.UseWebSockets(new WebSocketOptions
{
	KeepAliveInterval = TimeSpan.FromSeconds(20),
	KeepAliveTimeout = TimeSpan.FromSeconds(30),
});

app.MapGet("/", () => "One-Blind-Mouse relay");
app.MapGet("/healthz", () => "ok");
app.Map(Protocol.Path, async (HttpContext ctx, Hub hub, TimeProvider time) =>
{
	// Only WebSocket upgrades are served here
	if (!ctx.WebSockets.IsWebSocketRequest)
	{
		ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
		return;
	}

	// Refuse before accepting if this address already has too many connections
	var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
	if (!hub.TryOpen(ip))
	{
		ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
		return;
	}

	// Service the connection until it closes
	try
	{
		using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
		await new Session(hub, ws, time, ip).RunAsync();
	}
	finally
	{
		hub.Closed(ip);
	}
});

app.Run();

/// <summary>Entry point. Public so integration tests can host the relay with WebApplicationFactory.</summary>
public partial class Program
{
}
