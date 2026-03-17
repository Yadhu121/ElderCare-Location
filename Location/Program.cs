//using System.Net.WebSockets;
//using System.Text;
//using System.Globalization;
//using System.Collections.Concurrent;
//using System.IdentityModel.Tokens.Jwt;
//using Microsoft.IdentityModel.Tokens;
//using System.Security.Claims;

//var builder = WebApplication.CreateBuilder(args);

//builder.Services.AddCors(options =>
//{
//    options.AddDefaultPolicy(policy =>
//    {
//        policy.AllowAnyOrigin()
//              .AllowAnyHeader()
//              .AllowAnyMethod();
//    });
//});

//var app = builder.Build();

//app.UseCors();
//app.UseWebSockets();

//ConcurrentDictionary<int, (double lat, double lng)> latestLocations = new();

//ConcurrentDictionary<int, ConcurrentQueue<(double lat, double lng)>> paths = new();

//var jwtSection = builder.Configuration.GetSection("Jwt");
//var jwtKey = jwtSection["Key"];
//var jwtIssuer = jwtSection["Issuer"];
//var jwtAudience = jwtSection["Audience"];

//if (string.IsNullOrEmpty(jwtKey))
//{
//    throw new Exception("JWT Key not configured in appsettings.json");
//}

//app.Map("/ws", async context =>
//{
//    var token = context.Request.Query["token"].ToString();

//    if (string.IsNullOrEmpty(token))
//    {
//        context.Response.StatusCode = 401;
//        await context.Response.WriteAsync("Missing token");
//        return;
//    }

//    ClaimsPrincipal principal;
//    int elderId;

//    try
//    {
//        var handler = new JwtSecurityTokenHandler();
//        var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

//        var validationParameters = new TokenValidationParameters
//        {
//            ValidateIssuerSigningKey = true,
//            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),

//            ValidateIssuer = true,
//            ValidateAudience = true,

//            ValidIssuer = jwtIssuer,
//            ValidAudience = jwtAudience,

//            ValidateLifetime = true,
//            ClockSkew = TimeSpan.Zero
//        };

//        principal = handler.ValidateToken(token, validationParameters, out _);

//        var userIdClaim = principal.FindFirst(ClaimTypes.NameIdentifier);
//        var roleClaim = principal.FindFirst(ClaimTypes.Role);

//        if (userIdClaim == null || roleClaim == null)
//        {
//            context.Response.StatusCode = 401;
//            await context.Response.WriteAsync("Invalid token claims");
//            return;
//        }

//        if (roleClaim.Value != "Elder")
//        {
//            context.Response.StatusCode = 403;
//            await context.Response.WriteAsync("Only elders can send location");
//            return;
//        }

//        elderId = int.Parse(userIdClaim.Value);
//    }
//    catch (Exception ex)
//    {
//        Console.WriteLine($"Token validation failed: {ex.Message}");
//        context.Response.StatusCode = 401;
//        await context.Response.WriteAsync("Invalid token");
//        return;
//    }

//    if (!context.WebSockets.IsWebSocketRequest)
//    {
//        context.Response.StatusCode = 400;
//        await context.Response.WriteAsync("WebSocket request expected");
//        return;
//    }

//    using var socket = await context.WebSockets.AcceptWebSocketAsync();
//    var buffer = new byte[4096];

//    Console.WriteLine($"Elder {elderId} connected");

//    try
//    {
//        while (socket.State == WebSocketState.Open)
//        {
//            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

//            if (result.MessageType == WebSocketMessageType.Close)
//            {
//                await socket.CloseAsync(
//                    WebSocketCloseStatus.NormalClosure,
//                    "Closing",
//                    CancellationToken.None);
//                break;
//            }

//            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
//            var parts = msg.Split(',');

//            if (parts.Length == 2 &&
//                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
//                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng))
//            {
//                latestLocations[elderId] = (lat, lng);


//                if (!paths.ContainsKey(elderId))
//                    paths[elderId] = new ConcurrentQueue<(double, double)>();

//                paths[elderId].Enqueue((lat, lng));


//                if (paths[elderId].Count > 1000)
//                    paths[elderId].TryDequeue(out _);

//                Console.WriteLine($"Elder {elderId}: {lat}, {lng}");
//            }
//        }
//    }
//    catch (Exception ex)
//    {
//        Console.WriteLine($"WebSocket error for elder {elderId}: {ex.Message}");
//    }
//    finally
//    {
//        Console.WriteLine($"Elder {elderId} disconnected");
//    }
//});

//app.MapGet("/location/{elderId:int}", (int elderId) =>
//{
//    if (latestLocations.TryGetValue(elderId, out var loc))
//        return Results.Ok(new { lat = loc.lat, lng = loc.lng });

//    return Results.NotFound(new { error = "No location data" });
//});

//app.MapGet("/path/{elderId:int}", (int elderId) =>
//{
//    if (paths.TryGetValue(elderId, out var path))
//        return Results.Ok(path.Select(p => new { lat = p.lat, lng = p.lng }));

//    return Results.Ok(new List<object>());
//});

//app.MapGet("/health", () =>
//    Results.Ok(new
//    {
//        status = "healthy",
//        active_elders = latestLocations.Count
//    })
//);

//app.Run();



using System.Net.WebSockets;
using System.Text;
using System.Globalization;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Data.SqlClient;
using System.Net.Mail;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

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

ConcurrentDictionary<int, (double lat, double lng)> latestLocations = new();
ConcurrentDictionary<int, ConcurrentQueue<(double lat, double lng)>> paths = new();
ConcurrentDictionary<int, bool> elderOutsideGeofence = new();

var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["Key"];
var jwtIssuer = jwtSection["Issuer"];
var jwtAudience = jwtSection["Audience"];

var emailSection = builder.Configuration.GetSection("EmailSettings");

var connStr = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrEmpty(jwtKey))
    throw new Exception("JWT Key not configured in appsettings.json");

static double GetDistanceKm(double lat1, double lon1, double lat2, double lon2)
{
    const double R = 6371;
    double dLat = (lat2 - lat1) * Math.PI / 180;
    double dLon = (lon2 - lon1) * Math.PI / 180;
    double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
               Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
               Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
    return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
}

async Task SendGeofenceEmail(string host, int port, bool ssl, string userName, string password, string from,
    string toEmail, string elderName, string type)
{
    bool isLeft = type == "LEFT";
    string subject = isLeft
        ? $"GEOFENCE ALERT - {elderName} has left the safe zone"
        : $"GEOFENCE ALERT - {elderName} has returned to the safe zone";

    string color = isLeft ? "#EF4444" : "#10B981";
    string body = isLeft
        ? $"{elderName} has moved more than 1km away from their home location."
        : $"{elderName} has returned within 1km of their home location.";

    using var client = new SmtpClient(host, port);
    client.EnableSsl = ssl;
    client.Credentials = new NetworkCredential(userName, password);

    var message = new MailMessage();
    message.From = new MailAddress(from);
    message.To.Add(new MailAddress(toEmail));
    message.Subject = subject;
    message.IsBodyHtml = true;
    message.Body = $@"
        <div style='font-family: sans-serif; max-width: 600px; margin: 0 auto;'>
            <div style='background: {color}; padding: 20px; border-radius: 12px 12px 0 0;'>
                <h2 style='color: white; margin: 0;'>{subject}</h2>
            </div>
            <div style='background: #F8FAFC; padding: 20px; border-radius: 0 0 12px 12px; border: 1px solid #E2E8F0;'>
                <p style='font-size: 16px; color: #0F172A;'>{body}</p>
                <p style='color: #64748B; font-size: 14px;'>Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>
                <hr style='border: 1px solid #E2E8F0; margin: 20px 0;'/>
                <p style='color: #94A3B8; font-size: 12px;'>Wellnest - Compassionate Care</p>
            </div>
        </div>";

    await client.SendMailAsync(message);
}

app.Map("/ws", async context =>
{
    var token = context.Request.Query["token"].ToString();

    if (string.IsNullOrEmpty(token))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Missing token");
        return;
    }

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
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                break;
            }

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var parts = msg.Split(',');

            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double lat) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double lng))
            {
                latestLocations[elderId] = (lat, lng);

                if (!paths.ContainsKey(elderId))
                    paths[elderId] = new ConcurrentQueue<(double, double)>();

                paths[elderId].Enqueue((lat, lng));

                if (paths[elderId].Count > 1000)
                    paths[elderId].TryDequeue(out _);

                Console.WriteLine($"Elder {elderId}: {lat}, {lng}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"[GEOFENCE] Checking elder {elderId}");

                        using var con = new SqlConnection(connStr);
                        using var cmd = new SqlCommand(@"
                            SELECT c.Email, e.elderName, e.HomeLat, e.HomeLng
                            FROM CaretakerElderMap m
                            JOIN caretakerTable c ON c.CareTakerID = m.CareTakerID
                            JOIN elderTable e ON e.ElderId = m.ElderID
                            WHERE m.ElderID = @elderId", con);

                        cmd.Parameters.AddWithValue("@elderId", elderId);
                        con.Open();

                        using var reader = cmd.ExecuteReader();
                        if (!reader.Read())
                        {
                            Console.WriteLine($"[GEOFENCE] No caretaker mapping for elder {elderId}");
                            return;
                        }

                        string caretakerEmail = reader["Email"].ToString();
                        string elderName = reader["elderName"].ToString();
                        Console.WriteLine($"[GEOFENCE] Caretaker: {caretakerEmail}, Elder: {elderName}");

                        if (reader["HomeLat"] == DBNull.Value || reader["HomeLng"] == DBNull.Value)
                        {
                            Console.WriteLine($"[GEOFENCE] HomeLat/HomeLng is NULL for elder {elderId}");
                            return;
                        }

                        double homeLat = (double)reader["HomeLat"];
                        double homeLng = (double)reader["HomeLng"];
                        reader.Close();

                        double distance = GetDistanceKm(lat, lng, homeLat, homeLng);
                        bool isOutside = distance > 1.0; //
                        bool wasOutside = elderOutsideGeofence.GetOrAdd(elderId, false);

                        Console.WriteLine($"[GEOFENCE] Distance: {distance}km, isOutside: {isOutside}, wasOutside: {wasOutside}");

                        if (isOutside && !wasOutside)
                        {
                            elderOutsideGeofence[elderId] = true;
                            await SendGeofenceEmail(
                                emailSection["Host"], int.Parse(emailSection["Port"]),
                                bool.Parse(emailSection["EnableSsl"]),
                                emailSection["UserName"], emailSection["Password"], emailSection["From"],
                                caretakerEmail, elderName, "LEFT");
                            Console.WriteLine($"[GEOFENCE] LEFT email sent to {caretakerEmail}");
                        }
                        else if (!isOutside && wasOutside)
                        {
                            elderOutsideGeofence[elderId] = false;
                            await SendGeofenceEmail(
                                emailSection["Host"], int.Parse(emailSection["Port"]),
                                bool.Parse(emailSection["EnableSsl"]),
                                emailSection["UserName"], emailSection["Password"], emailSection["From"],
                                caretakerEmail, elderName, "RETURNED");
                            Console.WriteLine($"[GEOFENCE] RETURNED email sent to {caretakerEmail}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[GEOFENCE] Exception: {ex.Message}");
                        Console.WriteLine($"[GEOFENCE] StackTrace: {ex.StackTrace}");
                    }
                });
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

app.MapGet("/location/{elderId:int}", (int elderId) =>
{
    if (latestLocations.TryGetValue(elderId, out var loc))
        return Results.Ok(new { lat = loc.lat, lng = loc.lng });

    return Results.NotFound(new { error = "No location data" });
});

app.MapGet("/path/{elderId:int}", (int elderId) =>
{
    if (paths.TryGetValue(elderId, out var path))
        return Results.Ok(path.Select(p => new { lat = p.lat, lng = p.lng }));

    return Results.Ok(new List<object>());
});

app.MapGet("/health", () =>
    Results.Ok(new
    {
        status = "healthy",
        active_elders = latestLocations.Count
    })
);

app.Run();