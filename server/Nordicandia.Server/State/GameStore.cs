using System.Collections.Concurrent;
using SharedNet.Constants;
using SharedNet.Dto;

namespace Nordicandia.Server.State;

/// <summary>In-memory account/session store (persistable to JSON on shutdown).</summary>
public sealed class GameStore
{
    public static readonly GameStore Instance = new();

    private readonly ConcurrentDictionary<string, Guid> _identityToUser = new();
    private readonly ConcurrentDictionary<Guid, UserDto> _users = new();
    private readonly ConcurrentDictionary<string, SessionDto> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _refresh = new();

    private GameStore() { }

    public UserDto GetOrCreateUser(string identity)
    {
        if (_identityToUser.TryGetValue(identity, out var id) && _users.TryGetValue(id, out var existing))
        {
            existing.LastLogin = DateTime.UtcNow;
            return existing;
        }
        var user = new UserDto
        {
            UserId = Guid.NewGuid(),
            DisplayName = identity,
            Created = DateTime.UtcNow,
            LastLogin = DateTime.UtcNow,
            Role = UserRole.Player,
        };
        _identityToUser[identity] = user.UserId;
        _users[user.UserId] = user;
        return user;
    }

    public UserDto FindUserByRefreshToken(string refreshToken)
    {
        if (refreshToken != null && _refresh.TryGetValue(refreshToken, out var id) && _users.TryGetValue(id, out var u))
            return u;
        return null;
    }

    public SessionDto CreateSession(Guid userId)
    {
        var now = DateTime.UtcNow;
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var refresh = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var session = new SessionDto
        {
            UserId = userId,
            AuthToken = token,
            RefreshToken = refresh,
            NewlyCreated = true,
            CreateTimestamp = now,
            ExpireTimestamp = now.AddHours(12),
            RefreshExpireTimestamp = now.AddDays(30),
        };
        _sessions[token] = session;
        _refresh[refresh] = userId;
        return session;
    }

    public UserDto GetUserByAuthToken(string token)
    {
        if (token != null && _sessions.TryGetValue(token, out var s) && _users.TryGetValue(s.UserId, out var u))
            return u;
        return null;
    }
}
