using System.Net.WebSockets;
using System.Text;
using System.Globalization;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Enable CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors();
app.UseWebSockets();

// ============================
// In-memory storage
// ============================

// Latest location per elder
ConcurrentDictionary<int, (double lat, double lng)> latestLocations = new();

// Full path history per elder
ConcurrentDictionary<int, ConcurrentQueue<(double lat, double lng)>> paths = new();

// ============================
// JWT Settings (must match main app)
// ============================

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

if (string.IsNullOrEmpty(jwtKey))
{
    throw new Exception("JWT Key not configured in appsettings.json");
}

// ============================
// WebSocket Endpoint
// ============================

app.Map("/ws", async context =>
{
    var token = context.Request.Query["token"].ToString();

    if (string.IsNullOrEmpty(token))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Missing token");
        return;
    }

    // Validate JWT
    ClaimsPrincipal principal;
    int elderId;

    try
    {
        var handler = new JwtSecurityTokenHandler();
        var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),

            ValidateIssuer = true,
            ValidateAudience = true,

            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        principal = handler.ValidateToken(token, validationParameters, out _);

        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
        var roleClaim = principal.FindFirst(ClaimTypes.Role);

        if (userIdClaim == null || roleClaim == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid token claims");
            return;
        }

        // Only elders can send location
        if (roleClaim.Value != "Elder")
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Only elders can send location");
            return;
        }

        elderId = int.Parse(userIdClaim.Value);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Token validation failed: {ex.Message}");
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Invalid token");
        return;
    }

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket request expected");
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[4096];

    Console.WriteLine($"Elder {elderId} connected");

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
                break;
            }

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var parts = msg.Split(',');

            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng))
            {
                // Store latest location
                latestLocations[elderId] = (lat, lng);

                // Store path history
                if (!paths.ContainsKey(elderId))
                    paths[elderId] = new ConcurrentQueue<(double, double)>();

                paths[elderId].Enqueue((lat, lng));

                // Limit path size
                if (paths[elderId].Count > 1000)
                    paths[elderId].TryDequeue(out _);

                Console.WriteLine($"Elder {elderId}: {lat}, {lng}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WebSocket error for elder {elderId}: {ex.Message}");
    }
    finally
    {
        Console.WriteLine($"Elder {elderId} disconnected");
    }
});

// ============================
// REST APIs
// ============================

// Latest location
app.MapGet("/location/{elderId:int}", (int elderId) =>
{
    if (latestLocations.TryGetValue(elderId, out var loc))
        return Results.Ok(new { lat = loc.lat, lng = loc.lng });

    return Results.NotFound(new { error = "No location data" });
});

// Full path
app.MapGet("/path/{elderId:int}", (int elderId) =>
{
    if (paths.TryGetValue(elderId, out var path))
        return Results.Ok(path.Select(p => new { lat = p.lat, lng = p.lng }));

    return Results.Ok(new List<object>());
});

// Health check
app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "healthy",
        active_elders = latestLocations.Count
    })
);

app.Run();
