using System.Text.Json;
using Grpc.Core;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON skill-rank upload.
///
/// Why this exists: the shipped Android client has no source available, so its
/// patch is a tiny native stub hooking LivingPowers.RankUp. Driving the
/// MagicOnion UpgradeCharacterSkillRank RPC from that stub requires fragile
/// IL2CPP runtime reflection (generic method inflation), which crashes on
/// device. A JSON POST needs no reflection at all and mirrors the proven
/// LeaderboardHttp stub pattern. Auth and validation reuse the exact same
/// <see cref="GameStore.UpgradeSkillRank"/> path as the gRPC endpoint, so the
/// two can never disagree.
/// </summary>
internal static class SkillRankHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class SkillRankRequest
    {
        public Guid CharacterId { get; set; }
        public int PowerHashSafe { get; set; }
        public double NewRank { get; set; }
    }

    public static void MapSkillRankJson(this WebApplication app)
    {
        app.MapPost("/api/character/skill-rank", (SkillRankRequest req, HttpContext ctx) =>
        {
            Guid owner;
            try
            {
                owner = GameStore.Instance.RequireUser(ctx.Request.Headers["authorization"].FirstOrDefault());
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
            {
                return Results.Unauthorized();
            }

            if (req == null) return Results.BadRequest("missing body");
            try
            {
                var res = GameStore.Instance.UpgradeSkillRank(owner, req.CharacterId, req.PowerHashSafe, req.NewRank);
                return Results.Json(new { newRank = res.NewRank }, Json);
            }
            catch (RpcException ex)
            {
                return ex.StatusCode switch
                {
                    StatusCode.InvalidArgument => Results.BadRequest(ex.Status.Detail),
                    StatusCode.NotFound => Results.NotFound(ex.Status.Detail),
                    StatusCode.FailedPrecondition => Results.Conflict(ex.Status.Detail),
                    _ => Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 500),
                };
            }
        });
    }
}
