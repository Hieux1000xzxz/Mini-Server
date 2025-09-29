using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddHostedService<LobbyCleanupService>();

var app = builder.Build();

app.UseCors("AllowAll");
app.MapControllers();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Urls.Add($"http://*:{port}");

app.Run();

public static class LobbyRegistry
{
    public static ConcurrentDictionary<string, RelayLobbyInfo> Lobbies = new();
}

public class UserInfo
{
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public int AvatarIndex { get; set; } = 0;
    public bool IsReady { get; set; } = false;
}

public class UpdateLobbyNameRequest
{
    public required string LobbyId { get; set; }
    public required string LobbyName { get; set; }
    public string? RequestingUserId { get; set; }
}

public class UpdateReadyRequest
{
    public required string UserId { get; set; }
    public bool IsReady { get; set; }
}

public class RelayLobbyInfo
{
    public required string LobbyId { get; set; }
    public required string LobbyName { get; set; }

    public required string RelayJoinCode { get; set; }

    public int MaxPlayers { get; set; } = 6;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsGameStarted { get; set; } = false;
    public string HostUserId { get; set; } = string.Empty;

    public bool HostMigrated { get; set; } = false;
    public bool GameInProgress { get; set; } = false;
    public bool PreserveOnHostLeave { get; set; } = false;

    public List<UserInfo> Users { get; set; } = new List<UserInfo>();

    public int CurrentPlayers => Users.Count;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
}

public class RelayLobbyRegistrationRequest
{
    public required string LobbyName { get; set; }

    public required string RelayJoinCode { get; set; }

    public int MaxPlayers { get; set; } = 6;
    public string HostName { get; set; } = "Host";
    public int AvatarIndex { get; set; } = 0;
}

public class HostMigrationRequest
{
    public bool HostMigrated { get; set; }
    public bool GameInProgress { get; set; }
    public string? NewRelayJoinCode { get; set; } 
}

[ApiController]
[Route("api/lobby")]
public class LobbyController : ControllerBase
{
    private readonly Random _random = new();

    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new
        {
            Status = "OK",
            Message = "Relay Server is running",
            Timestamp = DateTime.UtcNow,
            ServerVersion = "2.0.0-Relay",
            Features = new[] { "Unity Relay Support", "Cross-platform Multiplayer" }
        });
    }

    [HttpPost("register")]
    public IActionResult Register([FromBody] RelayLobbyRegistrationRequest request)
    {
        if (string.IsNullOrEmpty(request.RelayJoinCode))
        {
            return BadRequest("RelayJoinCode is required");
        }

        if (request.RelayJoinCode.Length < 4 || request.RelayJoinCode.Length > 10)
        {
            return BadRequest("Invalid RelayJoinCode format");
        }

        string lobbyId;
        do
        {
            lobbyId = GenerateLobbyId(6);
        } while (LobbyRegistry.Lobbies.ContainsKey(lobbyId));

        var hostUserId = Guid.NewGuid().ToString();

        var lobbyInfo = new RelayLobbyInfo
        {
            LobbyId = lobbyId,
            LobbyName = request.LobbyName,
            RelayJoinCode = request.RelayJoinCode,
            MaxPlayers = request.MaxPlayers,
            HostUserId = hostUserId,
            Users = new List<UserInfo>
            {
                new UserInfo
                {
                    UserId = hostUserId,
                    UserName = request.HostName,
                    AvatarIndex = request.AvatarIndex
                }
            }
        };

        if (LobbyRegistry.Lobbies.TryAdd(lobbyId, lobbyInfo))
        {
            Console.WriteLine($"[Register] Relay lobby {lobbyId} registered with join code: {request.RelayJoinCode}");
            return Ok(lobbyInfo);
        }

        return StatusCode(500, "Could not register lobby");
    }

    [HttpPost("{lobbyId}/start")]
    public IActionResult StartGame(string lobbyId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            if (!lobby.Users.All(u => u.IsReady))
            {
                return BadRequest("Not all players are ready");
            }

            lobby.IsGameStarted = true;
            lobby.GameInProgress = true;
            lobby.PreserveOnHostLeave = true;

            Console.WriteLine($"[Start Game] Relay lobby {lobbyId} started with {lobby.CurrentPlayers} players");
            return Ok(new { Message = "Game started", Lobby = lobby });
        }
        return NotFound("Lobby not found");
    }


    [HttpPost("{lobbyId}/host-migration")]
    public IActionResult HandleHostMigration(string lobbyId, [FromBody] HostMigrationRequest request)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            lobby.HostMigrated = request.HostMigrated;
            lobby.GameInProgress = request.GameInProgress;
            lobby.PreserveOnHostLeave = true;

            if (!string.IsNullOrEmpty(request.NewRelayJoinCode))
            {
                lobby.RelayJoinCode = request.NewRelayJoinCode;
                Console.WriteLine($"[Host Migration] Lobby {lobbyId} updated with new relay code: {request.NewRelayJoinCode}");
            }

            lobby.LastHeartbeat = DateTime.UtcNow.AddHours(1);
            Console.WriteLine($"[Host Migration] Relay lobby {lobbyId} updated for host migration");
            return Ok(new { Message = "Host migration handled", Lobby = lobby });
        }
        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/rename")]
    public IActionResult RenameLobby(string lobbyId, [FromBody] UpdateLobbyNameRequest request)
    {
        if (!string.Equals(lobbyId, request.LobbyId, StringComparison.OrdinalIgnoreCase))
            return BadRequest("LobbyId in route and body do not match.");

        if (!LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
            return NotFound("Lobby not found");

        if (!string.IsNullOrEmpty(request.RequestingUserId) &&
            !string.IsNullOrEmpty(lobby.HostUserId) &&
            lobby.HostUserId != request.RequestingUserId)
        {
            Console.WriteLine($"[Rename] Non-host user {request.RequestingUserId} tried to rename lobby {lobbyId}");
            return Forbid("Only the host can rename the lobby.");
        }

        lobby.LobbyName = request.LobbyName;
        lobby.LastHeartbeat = DateTime.UtcNow;

        Console.WriteLine($"[Rename] Relay lobby {lobbyId} renamed to '{request.LobbyName}' by {request.RequestingUserId ?? "unknown"}");

        return Ok(new { Message = "Lobby name updated", Lobby = lobby });
    }

    [HttpPost("{lobbyId}/ready")]
    public IActionResult SetReady(string lobbyId, [FromBody] UpdateReadyRequest request)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            var user = lobby.Users.FirstOrDefault(u => u.UserId == request.UserId);
            if (user == null)
                return NotFound("User not found in lobby");

            user.IsReady = request.IsReady;
            lobby.LastHeartbeat = DateTime.UtcNow;

            Console.WriteLine($"[Ready] User {user.UserName} set Ready={request.IsReady} in lobby {lobbyId}");

            bool allReady = lobby.Users.All(u => u.IsReady);
            return Ok(new
            {
                Message = "Ready state updated",
                User = user,
                AllReady = allReady,
                Lobby = lobby
            });
        }
        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/heartbeat")]
    public IActionResult Heartbeat(string lobbyId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            if (!lobby.HostMigrated)
            {
                lobby.LastHeartbeat = DateTime.UtcNow;
            }
            return Ok(new { Message = "Heartbeat received", LobbyId = lobbyId });
        }
        return NotFound("Lobby not found");
    }

    [HttpGet("list")]
    public IActionResult List()
    {
        var availableLobbies = LobbyRegistry.Lobbies.Values
            .Where(lobby => !lobby.IsGameStarted || (lobby.IsGameStarted && lobby.HostMigrated))
            .Select(lobby => new
            {
                lobby.LobbyId,
                lobby.LobbyName,
                lobby.CurrentPlayers,
                lobby.MaxPlayers,
                lobby.CreatedAt,
                lobby.IsGameStarted,
                lobby.HostMigrated,
                lobby.Users,
                RelayJoinCode = (string?)null
            })
            .ToList();

        Console.WriteLine($"[List] Returning {availableLobbies.Count} available relay lobbies");
        return Ok(availableLobbies);
    }

    [HttpGet("find/{id}")]
    public IActionResult Find(string id)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(id, out var lobby))
        {
            Console.WriteLine($"[Find] Found relay lobby {id} with join code: {lobby.RelayJoinCode}");
            return Ok(lobby);
        }

        return NotFound("Lobby not found");
    }

    [HttpDelete("unregister/{id}")]
    public IActionResult Unregister(string id)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(id, out var lobby))
        {
            if (!lobby.IsGameStarted || !lobby.PreserveOnHostLeave)
            {
                if (LobbyRegistry.Lobbies.TryRemove(id, out _))
                {
                    Console.WriteLine($"[Unregister] Relay lobby {id} removed");
                    return Ok("Lobby removed");
                }
            }
            else
            {
                Console.WriteLine($"[Unregister] Preserving relay lobby {id} - game in progress");
                return Ok("Lobby preserved - game in progress");
            }
        }

        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/join")]
    public IActionResult JoinLobby(string lobbyId, [FromBody] UserInfo user)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            if (lobby.CurrentPlayers >= lobby.MaxPlayers)
            {
                Console.WriteLine($"[Join] User {user.UserName} tried to join full lobby {lobbyId}");
                return BadRequest("Lobby is full");
            }

            if (lobby.IsGameStarted && !lobby.HostMigrated)
            {
                Console.WriteLine($"[Join] User {user.UserName} tried to join started game {lobbyId}");
                return BadRequest("Game has already started, cannot join");
            }

            var existingUser = lobby.Users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existingUser == null)
            {
                lobby.Users.Add(user);
                Console.WriteLine($"[Join] User {user.UserName} joined relay lobby {lobbyId} ({lobby.CurrentPlayers}/{lobby.MaxPlayers})");
            }
            else
            {
                existingUser.UserName = user.UserName;
                existingUser.AvatarIndex = user.AvatarIndex;
                Console.WriteLine($"[Join] User {user.UserName} rejoined relay lobby {lobbyId}");
            }

            return Ok(lobby);
        }

        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/leave")]
    public IActionResult LeaveLobby(string lobbyId, [FromBody] UserInfo user)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            var existingUser = lobby.Users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existingUser != null)
            {
                lobby.Users.Remove(existingUser);
                Console.WriteLine($"[Leave] User {user.UserName} left relay lobby {lobbyId} ({lobby.CurrentPlayers}/{lobby.MaxPlayers})");

                if (lobby.Users.Count == 0)
                {
                    if (lobby.PreserveOnHostLeave && lobby.GameInProgress)
                    {
                        Console.WriteLine($"[Leave] Relay lobby {lobbyId} is empty but preserved (game in progress)");
                        return Ok(new { Message = "Player left, lobby preserved but empty", LobbyEmpty = true, Lobby = lobby });
                    }
                    else
                    {
                        LobbyRegistry.Lobbies.TryRemove(lobbyId, out _);
                        Console.WriteLine($"[Leave] Empty relay lobby {lobbyId} removed");
                        return Ok(new { Message = "Player left and lobby removed (empty)", LobbyRemoved = true });
                    }
                }

                if (existingUser.UserId == lobby.HostUserId && lobby.Users.Count > 0)
                {
                    var newHost = lobby.Users.First();
                    lobby.HostUserId = newHost.UserId;
                    Console.WriteLine($"[Leave] Host migrated to {newHost.UserName} in relay lobby {lobbyId}");
                }
            }

            return Ok(lobby);
        }

        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/kick/{userId}")]
    public IActionResult KickPlayer(string lobbyId, string userId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            var user = lobby.Users.FirstOrDefault(u => u.UserId == userId);
            if (user != null)
            {
                lobby.Users.Remove(user);
                Console.WriteLine($"[Kick] User {user.UserName} kicked from relay lobby {lobbyId}");
                return Ok(new { Message = $"User {user.UserName} kicked", Lobby = lobby });
            }
            return NotFound("User not found in lobby");
        }
        return NotFound("Lobby not found");
    }

    [HttpPost("{lobbyId}/cleanup")]
    public IActionResult CleanupEmptyLobby(string lobbyId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            if (lobby.Users.Count == 0)
            {
                LobbyRegistry.Lobbies.TryRemove(lobbyId, out _);
                Console.WriteLine($"[Cleanup] Empty relay lobby {lobbyId} removed");
                return Ok(new { Message = "Empty lobby cleaned up", LobbyRemoved = true });
            }
            return Ok(new { Message = "Lobby still has players", Lobby = lobby });
        }
        return NotFound("Lobby not found");
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var stats = new
        {
            TotalLobbies = LobbyRegistry.Lobbies.Count,
            ActiveLobbies = LobbyRegistry.Lobbies.Values.Count(l => !l.IsGameStarted),
            GamesInProgress = LobbyRegistry.Lobbies.Values.Count(l => l.IsGameStarted),
            TotalPlayers = LobbyRegistry.Lobbies.Values.Sum(l => l.CurrentPlayers),
            Timestamp = DateTime.UtcNow
        };

        return Ok(stats);
    }

    private string GenerateLobbyId(int len)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Repeat(chars, len)
            .Select(s => s[_random.Next(s.Length)]).ToArray());
    }
}

public class LobbyCleanupService : BackgroundService
{
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _emptyLobbyTimeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _relayCodeTimeout = TimeSpan.FromMinutes(30); 

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("[Cleanup Service] Relay lobby cleanup service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            var lobbies = LobbyRegistry.Lobbies.ToList();
            var now = DateTime.UtcNow;
            int cleanedCount = 0;

            foreach (var kvp in lobbies)
            {
                var lobby = kvp.Value;

                if (lobby.Users.Count == 0 && (now - lobby.CreatedAt) > _emptyLobbyTimeout)
                {
                    LobbyRegistry.Lobbies.TryRemove(kvp.Key, out _);
                    Console.WriteLine($"[Cleanup] Empty relay lobby {kvp.Key} removed due to timeout");
                    cleanedCount++;
                    continue;
                }

                if (!lobby.HostMigrated && !lobby.PreserveOnHostLeave)
                {
                    if (now - lobby.LastHeartbeat > _timeout)
                    {
                        LobbyRegistry.Lobbies.TryRemove(kvp.Key, out _);
                        Console.WriteLine($"[Cleanup] Relay lobby {kvp.Key} removed due to heartbeat timeout");
                        cleanedCount++;
                        continue;
                    }
                }

                if (now - lobby.CreatedAt > _relayCodeTimeout && !lobby.GameInProgress)
                {
                    LobbyRegistry.Lobbies.TryRemove(kvp.Key, out _);
                    Console.WriteLine($"[Cleanup] Old relay lobby {kvp.Key} removed (relay code may have expired)");
                    cleanedCount++;
                    continue;
                }

                if (lobby.HostMigrated || lobby.PreserveOnHostLeave)
                {
                    if ((now.Minute % 5) == 0)
                    {
                        Console.WriteLine($"[Cleanup] Preserving relay lobby {kvp.Key} - host migration or preserve flag set");
                    }
                }
            }

            if (cleanedCount > 0)
            {
                Console.WriteLine($"[Cleanup] Cleaned up {cleanedCount} relay lobbies. Remaining: {LobbyRegistry.Lobbies.Count}");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}