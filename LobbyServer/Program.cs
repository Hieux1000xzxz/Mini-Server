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
    public static ConcurrentDictionary<string, LobbyInfo> Lobbies = new();
}

public class UserInfo
{
    public required string UserId { get; set; }
    public required string UserName { get; set; }
    public int AvatarIndex { get; set; } = 0;
}

public class UpdateLobbyNameRequest
{
    public required string LobbyId { get; set; }
    public required string LobbyName { get; set; }

    public string? RequestingUserId { get; set; }
}

public class LobbyInfo
{
    public required string LobbyId { get; set; }
    public required string LobbyName { get; set; }
    public required string HostIpAddress { get; set; }
    public required int HostPort { get; set; }
    public int MaxPlayers { get; set; } = 6;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsGameStarted { get; set; } = false;

    public bool HostMigrated { get; set; } = false;
    public bool GameInProgress { get; set; } = false;
    public bool PreserveOnHostLeave { get; set; } = false;

    public List<UserInfo> Users { get; set; } = new List<UserInfo>();

    public int CurrentPlayers => Users.Count;

    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
}

public class LobbyRegistrationRequest
{
    public required string LobbyName { get; set; }
    public required string HostIpAddress { get; set; }
    public required int HostPort { get; set; }
    public int MaxPlayers { get; set; } = 6;
    public string HostName { get; set; } = "Host";
        public int AvatarIndex { get; set; } = 0;

}

public class HostMigrationRequest
{
    public bool HostMigrated { get; set; }
    public bool GameInProgress { get; set; }
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
            Message = "Server is running",
            Timestamp = DateTime.UtcNow,
            ServerVersion = "1.0.0"
        });
    }

    [HttpPost("register")]
    public IActionResult Register([FromBody] LobbyRegistrationRequest request)
    {
        string lobbyId;
        do
        {
            lobbyId = GenerateLobbyId(6);
        } while (LobbyRegistry.Lobbies.ContainsKey(lobbyId));

        var lobbyInfo = new LobbyInfo
        {
            LobbyId = lobbyId,
            LobbyName = request.LobbyName,
            HostIpAddress = request.HostIpAddress,
            HostPort = request.HostPort,
            MaxPlayers = request.MaxPlayers,
            Users = new List<UserInfo>
            {
                new UserInfo
                {
                    UserId = Guid.NewGuid().ToString(),
                    UserName = request.HostName,
                    AvatarIndex = request.AvatarIndex 
                }
            }
        };

        if (LobbyRegistry.Lobbies.TryAdd(lobbyId, lobbyInfo))
            return Ok(lobbyInfo);

        return StatusCode(500, "Could not register lobby");
    }

    [HttpPost("{lobbyId}/start")]
    public IActionResult StartGame(string lobbyId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            lobby.IsGameStarted = true;
            lobby.GameInProgress = true;
            lobby.PreserveOnHostLeave = true;
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

            lobby.LastHeartbeat = DateTime.UtcNow.AddHours(1);
            Console.WriteLine($"[Host Migration] Lobby {lobbyId} updated for host migration");
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

        // if (!string.IsNullOrEmpty(request.RequestingUserId) && lobby.HostUserId != request.RequestingUserId)
        //     return Forbid("Only the host can rename the lobby.");

        lobby.LobbyName = request.LobbyName;
        lobby.LastHeartbeat = DateTime.UtcNow;

        Console.WriteLine($"[Rename] Lobby {lobbyId} renamed to {request.LobbyName} by {request.RequestingUserId ?? "unknown"}");

        return Ok(new { Message = "Lobby name updated", Lobby = lobby });
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
            .ToList();

        return Ok(availableLobbies);
    }

    [HttpGet("find/{id}")]
    public IActionResult Find(string id)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(id, out var lobby))
            return Ok(lobby);

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
                    return Ok("Lobby removed");
            }
            else
            {
                Console.WriteLine($"[Unregister] Preserving lobby {id} - game in progress");
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
                return BadRequest("Phòng đã đầy");

            if (lobby.IsGameStarted && !lobby.HostMigrated)
                return BadRequest("Game đã bắt đầu, không thể tham gia");

            if (!lobby.Users.Any(u => u.UserId == user.UserId))
                lobby.Users.Add(user);

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
                Console.WriteLine($"[Leave] User {user.UserName} left lobby {lobbyId}");

                if (lobby.Users.Count == 0)
                {
                    if (lobby.PreserveOnHostLeave && lobby.GameInProgress)
                    {
                        Console.WriteLine($"[Leave] Lobby {lobbyId} is empty but preserved (game in progress)");
                        return Ok(new { Message = "Player left, lobby preserved but empty", LobbyEmpty = true, Lobby = lobby });
                    }
                    else
                    {
                        LobbyRegistry.Lobbies.TryRemove(lobbyId, out _);
                        return Ok(new { Message = "Player left and lobby removed (empty)", LobbyRemoved = true });
                    }
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
                Console.WriteLine($"[Cleanup] Empty lobby {lobbyId} removed");
                return Ok(new { Message = "Empty lobby cleaned up", LobbyRemoved = true });
            }
            return Ok(new { Message = "Lobby still has players", Lobby = lobby });
        }
        return NotFound("Lobby not found");
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
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(5);
    private readonly TimeSpan _emptyLobbyTimeout = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lobbies = LobbyRegistry.Lobbies.ToList();

            foreach (var kvp in lobbies)
            {
                var lobby = kvp.Value;
                var now = DateTime.UtcNow;

                if (lobby.Users.Count == 0 && (now - lobby.CreatedAt) > _emptyLobbyTimeout)
                {
                    LobbyRegistry.Lobbies.TryRemove(kvp.Key, out _);
                    Console.WriteLine($"[Cleanup] Empty lobby {kvp.Key} removed due to timeout.");
                    continue;
                }

                if (!lobby.HostMigrated && !lobby.PreserveOnHostLeave)
                {
                    if (now - lobby.LastHeartbeat > _timeout)
                    {
                        LobbyRegistry.Lobbies.TryRemove(kvp.Key, out _);
                        Console.WriteLine($"[Cleanup] Lobby {kvp.Key} removed due to heartbeat timeout.");
                    }
                }
                else if (lobby.HostMigrated || lobby.PreserveOnHostLeave)
                {
                    Console.WriteLine($"[Cleanup] Preserving lobby {kvp.Key} - host migration or preserve flag set");
                }
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}