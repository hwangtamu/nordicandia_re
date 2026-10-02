using Microsoft.AspNetCore.Http.HttpResults;
using Game;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants.Game;

namespace Nordicandia.Server.WebApi;

/// <summary>
/// Browser-facing JSON API under <c>/api/web/v1</c>. The native client keeps using
/// MagicOnion; this surface is intentionally small (session + snapshot) and reuses the
/// same authoritative <see cref="GameStore"/>, so the web client never computes its own
/// persistent state.
///
/// Session model: an HttpOnly <c>nord_session</c> cookie carries the auth token. Mutating
/// requests must also send <c>X-Nord-Request: 1</c> (cheap CSRF guard on top of SameSite).
/// </summary>
public static class WebApiEndpoints
{
    private const string CookieName = "nord_session";
    private const string SessionHeader = "Authorization";

    private static bool DevMode =>
        Environment.GetEnvironmentVariable("NORD_WEB_DEV") is "1" or "true";

    public static void MapWebApi(this WebApplication app)
    {
        var group = app.MapGroup("/api/web/v1");

        group.MapGet("/health", () => Results.Ok(new { status = "ok", contentVersion = "m0-1", dev = DevMode }));

        // ----- session -----

        group.MapPost("/session", (HttpContext ctx, WebSessionRequest req) =>
        {
            var identity = NormalizeIdentity(req?.Identity);
            if (string.IsNullOrEmpty(identity) || string.IsNullOrEmpty(req?.Password))
                return Results.BadRequest(new { error = "identity_and_password_required" });

            if (req.CreateAccount && !GameStore.Instance.HasCredential(identity))
            {
                var user = GameStore.Instance.RegisterCredential(identity, req.Password, req.Identity.Trim());
                return CreateSessionResponse(ctx, user.UserId, user.DisplayName);
            }

            var verified = GameStore.Instance.VerifyCredential(identity, req.Password);
            if (verified is null)
                return Results.Json(new { error = "invalid_credentials" }, statusCode: 401);
            return CreateSessionResponse(ctx, verified.UserId, verified.DisplayName);
        });

        // Local-only convenience login for the M0 web slice: creates/reuses a device
        // account without a credential. Off unless NORD_WEB_DEV=1.
        group.MapPost("/dev/session", (HttpContext ctx, DevSessionRequest req) =>
        {
            if (!DevMode) return Results.NotFound();
            var name = string.IsNullOrWhiteSpace(req?.Name) ? "web-m0" : req.Name.Trim();
            var user = GameStore.Instance.GetOrCreateUser("device:" + name);
            return CreateSessionResponse(ctx, user.UserId, user.DisplayName);
        });

        group.MapPost("/session/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/", Secure = ctx.Request.IsHttps });
            return Results.Ok(new { status = "signed_out" });
        });

        // ----- account & characters -----

        group.MapGet("/account", (HttpContext ctx) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                userId = user.UserId,
                displayName = user.DisplayName,
                characters = user.Characters.Select(ToSummary).ToList(),
            });
        });

        group.MapGet("/characters", (HttpContext ctx) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            return Results.Ok(user.Characters.Select(ToSummary).ToList());
        });

        // M0 web character creation. The native client posts a full serialized blob; the
        // browser builds a minimal, server-seeded character instead so the M0 slice is not
        // blocked on reconstructing the client's character factory.
        group.MapPost("/characters", (HttpContext ctx, WebCreateCharacterRequest req) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            if (!HasCsrfHeader(ctx)) return Results.BadRequest(new { error = "csrf_header_required" });

            var name = req?.DisplayName?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 32)
                return Results.BadRequest(new { error = "invalid_display_name" });
            if ((int)(req?.Class ?? -1) is < 0 or > 7)
                return Results.BadRequest(new { error = "invalid_class" });
            if ((int)(req?.Race ?? -1) is < 0 or > 7)
                return Results.BadRequest(new { error = "invalid_race" });
            // M0 scope: softcore Normal/Season only.
            var gameMode = (GameMode)req.GameMode;
            if (gameMode is not (GameMode.Normal or GameMode.Season))
                return Results.BadRequest(new { error = "unsupported_game_mode" });

            var data = Defaults.Create<SerializedCharacterData.SerializedData>();
            data.CombatStats = new SerializedCharacterData.SerializedCombatStats
            {
                // Provisional starter values until the web client reports client-accurate
                // stats, or the character is imported from an existing save.
                Offense = 35, Defense = 20, Recovery = 6,
            };
            var header = GameStore.Instance.CreateCharacter(user.UserId, new CreateCharacterRequest
            {
                DisplayName = name,
                CharacterClass = (CharacterClass)req.Class,
                CharacterRace = (CharacterRace)req.Race,
                CharacterGameMode = gameMode,
                Data = new SerializedCharacterData { Data = data },
            });
            return Results.Ok(ToSummary(header));
        });

        group.MapGet("/characters/{id:guid}/snapshot", (HttpContext ctx, Guid id) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            if (!GameStore.Instance.OwnsCharacter(user.UserId, id)) return Results.NotFound();
            return Results.Ok(GameStore.Instance.ProjectWebSnapshot(user.UserId, id));
        });

        // ----- authoritative combat (M1) -----

        // Poll the live combat state. Advances the simulation by the real elapsed time
        // and persists any progression, so a refresh starts from the last authoritative XP.
        group.MapGet("/characters/{id:guid}/state", (HttpContext ctx, Guid id) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            if (!GameStore.Instance.OwnsCharacter(user.UserId, id)) return Results.NotFound();
            return Results.Ok(CombatRegistry.Instance.Advance(user.UserId, id));
        });

        // Apply one action intent. The command id makes retries idempotent; expectedVersion
        // is the snapshot version the client acted on.
        group.MapPost("/characters/{id:guid}/commands", (HttpContext ctx, Guid id, WebCommandEnvelope envelope) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            if (!HasCsrfHeader(ctx)) return Results.BadRequest(new { error = "csrf_header_required" });
            if (!GameStore.Instance.OwnsCharacter(user.UserId, id)) return Results.NotFound();
            if (envelope is null)
                return Results.BadRequest(new { error = "invalid_command" });

            var command = new WebCommandRequest(envelope.Type, envelope.X, envelope.Z, envelope.ItemId, envelope.SkillId);
            var (applied, reason, state) = CombatRegistry.Instance.ApplyCommand(
                user.UserId, id, envelope.CommandId, envelope.ExpectedVersion, command);
            return Results.Ok(new { applied, reason, state });
        });

        // Inventory + effective equipment stats (does not advance combat).
        group.MapGet("/characters/{id:guid}/inventory", (HttpContext ctx, Guid id) =>
        {
            var user = ResolveUser(ctx);
            if (user is null) return Results.Unauthorized();
            if (!GameStore.Instance.OwnsCharacter(user.UserId, id)) return Results.NotFound();
            return Results.Ok(CombatRegistry.Instance.Inventory(user.UserId, id));
        });
    }

    private static IResult CreateSessionResponse(HttpContext ctx, Guid userId, string displayName)
    {
        var session = GameStore.Instance.CreateSession(userId);
        ctx.Response.Cookies.Append(CookieName, session.AuthToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = ctx.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = session.ExpireTimestamp,
        });
        return Results.Ok(new WebSessionResponse(userId, displayName, session.AuthToken,
            session.ExpireTimestamp ?? DateTime.UtcNow.AddHours(12)));
    }

    private static bool HasCsrfHeader(HttpContext ctx)
        => ctx.Request.Headers["X-Nord-Request"].ToString() is "1" or "true";

    private static WebCharacterSummary ToSummary(SharedNet.Dto.CharacterHeaderDto header)
        => new(header.CharacterId, header.DisplayName, (int)header.Class, (int)header.Race,
            (int)header.GameMode, header.Level,
            header.GameMode is SharedNet.Constants.Game.GameMode.NormalHardcore
                or SharedNet.Constants.Game.GameMode.SeasonHardcore
                or SharedNet.Constants.Game.GameMode.ChallengeHardcore);

    private static string NormalizeIdentity(string raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Contains('@')
            ? "email:" + value.ToLowerInvariant()
            : "username:" + value.ToLowerInvariant();
    }

    private sealed record AccountUser(Guid UserId, string DisplayName, List<SharedNet.Dto.CharacterHeaderDto> Characters);

    private static AccountUser ResolveUser(HttpContext ctx)
    {
        var token = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(token))
        {
            var header = ctx.Request.Headers[SessionHeader].ToString();
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header[7..];
            else if (header.Length > 0) token = header;
        }
        var user = GameStore.Instance.GetUserByAuthToken(token);
        return user is null ? null : new AccountUser(user.UserId, user.DisplayName, GameStore.Instance.Characters(user.UserId));
    }
}

public sealed record DevSessionRequest(string Name);

public sealed record WebCreateCharacterRequest(string DisplayName, int Class, int Race, int GameMode);

public sealed record WebCommandEnvelope(
    string CommandId, long ExpectedVersion, string Type, double X = 0, double Z = 0, Guid ItemId = default, int SkillId = 0);
