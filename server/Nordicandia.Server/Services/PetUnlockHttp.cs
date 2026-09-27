using System.Text.Json;
using Grpc.Core;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON pet unlock endpoints.
///
/// Why this exists: the shipped Android client has no source available, so its
/// patch is a tiny native stub hooking OfflineCombatPets.Unlock. Driving the
/// MagicOnion UnlockCombatPet RPC from that stub requires fragile IL2CPP
/// runtime reflection (generic method inflation), which crashes on device.
/// A JSON POST needs no reflection at all and mirrors the proven
/// SkillRankHttp stub pattern. Auth and validation reuse the exact same
/// GameStore.UnlockPet/UnlockCombatPet path as the gRPC endpoints, so the
/// two can never disagree.
/// </summary>
internal static class PetUnlockHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class UnlockPetRequest
    {
        public Guid CharacterId { get; set; }
        public int PetDefinitionIntegerId { get; set; }
        public int OpalCost { get; set; }
    }

    public sealed class UnlockCombatPetRequest
    {
        public Guid CharacterId { get; set; }
        public bool PayWithOpals { get; set; }
        public int CombatPetDefinitionIntegerId { get; set; }
        public int Cost { get; set; }
    }

    public static void MapPetUnlockJson(this WebApplication app)
    {
        app.MapPost("/api/character/unlock-pet", (UnlockPetRequest req, HttpContext ctx) =>
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
                var newOpals = GameStore.Instance.UnlockPet(owner, req.CharacterId, req.PetDefinitionIntegerId, req.OpalCost);
                return Results.Json(new { newOpals }, Json);
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

        app.MapPost("/api/character/unlock-combat-pet", (UnlockCombatPetRequest req, HttpContext ctx) =>
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
                var (newCurrency, payWithOpals) = GameStore.Instance.UnlockCombatPet(owner, req.CharacterId, req.PayWithOpals, req.CombatPetDefinitionIntegerId, req.Cost);
                return Results.Json(new { newCurrency, payWithOpals }, Json);
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
