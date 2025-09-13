using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add services
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

var app = builder.Build();
app.UseCors("AllowAll");
app.MapControllers();
app.Run();

// --- LOBBY REGISTRY ---
public static class LobbyRegistry
{
    public static ConcurrentDictionary<string, LobbyInfo> Lobbies = new();
}

// --- MODELS ---
public class UserInfo
{
    public required string UserId { get; set; }       // ID duy nhất của user
    public required string UserName { get; set; }     // Tên hiển thị
}

public class LobbyInfo
{
    public required string LobbyId { get; set; }
    public required string LobbyName { get; set; }
    public required string HostIpAddress { get; set; }
    public required int HostPort { get; set; }
    public int MaxPlayers { get; set; } = 6;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Danh sách người chơi trong phòng
    public List<UserInfo> Users { get; set; } = new List<UserInfo>();

    // Số lượng hiện tại = Users.Count
    public int CurrentPlayers => Users.Count;
}

public class LobbyRegistrationRequest
{
    public required string LobbyName { get; set; }
    public required string HostIpAddress { get; set; }
    public required int HostPort { get; set; }
    public int MaxPlayers { get; set; } = 6;

    // Tên host
    public string HostName { get; set; } = "Host";
}

// --- CONTROLLER ---
[ApiController]
[Route("api/lobby")]
public class LobbyController : ControllerBase
{
    private readonly Random _random = new();

    // POST api/lobby/register
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
                    UserName = request.HostName
                }
            }
        };

        if (LobbyRegistry.Lobbies.TryAdd(lobbyId, lobbyInfo))
            return Ok(lobbyInfo);

        return StatusCode(500, "Could not register lobby");
    }
    // POST api/lobby/{id}/start
    [HttpPost("{lobbyId}/start")]
    public IActionResult StartGame(string lobbyId)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            // Cập nhật trạng thái phòng (đang chơi)
            return Ok(new { Message = "Game started", Lobby = lobby });
        }
        return NotFound("Lobby not found");
    }

    // GET api/lobby/list
    [HttpGet("list")]
    public IActionResult List()
    {
        return Ok(LobbyRegistry.Lobbies.Values.ToList());
    }

    // GET api/lobby/find/{id}
    [HttpGet("find/{id}")]
    public IActionResult Find(string id)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(id, out var lobby))
            return Ok(lobby);

        return NotFound("Lobby not found");
    }

    // DELETE api/lobby/unregister/{id}
    [HttpDelete("unregister/{id}")]
    public IActionResult Unregister(string id)
    {
        if (LobbyRegistry.Lobbies.TryRemove(id, out _))
            return Ok("Lobby removed");

        return NotFound("Lobby not found");
    }

    // POST api/lobby/{id}/join
    [HttpPost("{lobbyId}/join")]
    public IActionResult JoinLobby(string lobbyId, [FromBody] UserInfo user)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            if (lobby.CurrentPlayers >= lobby.MaxPlayers)
                return BadRequest("Phòng đã đầy");

            // Kiểm tra user đã tồn tại chưa
            if (!lobby.Users.Any(u => u.UserId == user.UserId))
                lobby.Users.Add(user);

            return Ok(lobby); // Trả về lobby kèm danh sách người dùng
        }

        return NotFound("Lobby not found");
    }

    // POST api/lobby/{id}/leave
    [HttpPost("{lobbyId}/leave")]
    public IActionResult LeaveLobby(string lobbyId, [FromBody] UserInfo user)
    {
        if (LobbyRegistry.Lobbies.TryGetValue(lobbyId, out var lobby))
        {
            var existingUser = lobby.Users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existingUser != null)
                lobby.Users.Remove(existingUser);

            return Ok(lobby);
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
