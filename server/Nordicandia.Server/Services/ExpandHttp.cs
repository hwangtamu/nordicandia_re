using System.Text.Json;
using Grpc.Core;
using Nordicandia.Server.State;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON slot-expansion endpoints.
///
/// Why this exists: the shipped Android client has no source available, so its
/// patch is a tiny native stub hooking the *Offline slot-expansion methods
/// (SkillGrid.ExpandSkillSlotOffline / InventoryGrid.ExpandPotionSlotOffline).
/// Those methods never call the server, so the purchase was applied locally and
/// the slot silently vanished on the next login. Driving the MagicOnion RPC from
/// the stub requires fragile IL2CPP runtime reflection, which crashes on device;
/// a JSON POST needs none and mirrors the proven SkillRankHttp / PetUnlockHttp
/// pattern. Auth and validation reuse the exact same
/// GameStore.ExpandSkillSlots / ExpandInventoryRow path as the gRPC endpoints,
/// so the two can never disagree.
///
/// The stub bodies send:
///   POST /api/character/expand-skill-slots  {"characterId","expandType","opalCost"}
///   POST /api/character/expand-potion-slots {"characterId","opalCost"}
/// </summary>
internal static class ExpandHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class ExpandSkillSlotsRequest
    {
        public Guid CharacterId { get; set; }
        /// <summary>Raw <see cref="ExpandCharacterSkillSlotTypes"/> value sent by the
        /// client stub (Active=1 / Passive=2 / PassiveTraining=3).</summary>
        public int ExpandType { get; set; }
        public int OpalCost { get; set; }
    }

    public sealed class ExpandPotionSlotsRequest
    {
        public Guid CharacterId { get; set; }
        public int OpalCost { get; set; }
    }

    public static void MapExpandJson(this WebApplication app)
    {
        app.MapPost("/api/character/expand-skill-slots", (ExpandSkillSlotsRequest req, HttpContext ctx) =>
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
                var res = GameStore.Instance.ExpandSkillSlots(owner, req.CharacterId,
                    (ExpandCharacterSkillSlotTypes)req.ExpandType, req.OpalCost);
                return Results.Json(new { newOpals = res.NewOpals, newNumSlots = res.NewNumSlots }, Json);
            }
            catch (RpcException ex)
            {
                return MapError(ex);
            }
        });

        app.MapPost("/api/character/expand-potion-slots", (ExpandPotionSlotsRequest req, HttpContext ctx) =>
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
                // The client hook is ExpandPotionSlotOffline, which has no type discriminator;
                // potion slots always map to ExpandInventoryRowType.Potions.
                var res = GameStore.Instance.ExpandInventoryRow(owner, req.CharacterId,
                    ExpandInventoryRowType.Potions, req.OpalCost);
                return Results.Json(new { newOpals = res.NewOpals, newNumRows = res.NewNumRows }, Json);
            }
            catch (RpcException ex)
            {
                return MapError(ex);
            }
        });
    }

    private static IResult MapError(RpcException ex) => ex.StatusCode switch
    {
        StatusCode.InvalidArgument => Results.BadRequest(ex.Status.Detail),
        StatusCode.NotFound => Results.NotFound(ex.Status.Detail),
        StatusCode.FailedPrecondition => Results.Conflict(ex.Status.Detail),
        _ => Results.Json(new { error = ex.Status.Detail }, Json, statusCode: 500),
    };
}
