using System.Text.Json;
using Grpc.Core;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON upload of the client's full power (passive-skill) state.
///
/// Why this exists: passive skills level through the time-based training system
/// (LivingPowers.StartTraining -> HasFinishedTraining -> RankUp), which has no
/// upload RPC. The client's LivingPowers.Serialize() is the authoritative snapshot
/// (ranks + training windows), so a tiny native stub hooks it and POSTs the list
/// here. Mirrors the proven SkillRankHttp pattern; GameStore.SavePowers does the
/// actual merge.
/// </summary>
internal static class PowersHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class PowersRequest
    {
        public Guid CharacterId { get; set; }
        public List<PowerSyncEntry> Powers { get; set; }
    }

    public static void MapPowersJson(this WebApplication app)
    {
        app.MapPost("/api/character/powers", (PowersRequest req, HttpContext ctx) =>
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
                GameStore.Instance.SavePowers(owner, req.CharacterId, req.Powers);
                return Results.Json(new { saved = req.Powers?.Count ?? 0 }, Json);
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
