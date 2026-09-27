using System.Text.Json;
using Grpc.Core;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON Aesir offering.
///
/// Why this exists: the Android client applies Aesir offerings through the
/// offline path (<c>WindowAesirOffering.MakeOfferingOffline</c>) and never calls
/// the MakeOffering gRPC, so the blessing was applied locally only and vanished
/// on the next login. A tiny native stub captures the offering size from
/// PromptOfferingPurchase and POSTs the offering here. Mirrors the MakeOffering
/// gRPC exactly (same <see cref="GameStore.MakeOffering"/> path).
/// </summary>
internal static class MakeOfferingHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class MakeOfferingRequest
    {
        public Guid CharacterId { get; set; }
        public int OfferingType { get; set; }
        public int OfferingSize { get; set; }
        public int OfferedOpals { get; set; }
    }

    public static void MapMakeOfferingJson(this WebApplication app)
    {
        app.MapPost("/api/character/make-offering", (MakeOfferingRequest req, HttpContext ctx) =>
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
                var result = GameStore.Instance.MakeOffering(owner, req.CharacterId, req.OfferingType,
                    req.OfferingSize, req.OfferedOpals);
                Console.WriteLine($"[OFFERING] character={req.CharacterId} type={req.OfferingType} size={req.OfferingSize} opals={req.OfferedOpals} newOpals={result.NewOpals} applied={result.Applied}");
                return Results.Json(new { newOpals = result.NewOpals, applied = result.Applied }, Json);
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
