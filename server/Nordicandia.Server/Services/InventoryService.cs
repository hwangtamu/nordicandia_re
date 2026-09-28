using MagicOnion;
using SharedNet.Api;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class InventoryServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    /// <summary>
    /// The client journals every inventory change (loot, move, delete, consume) through
    /// this unary call. We replay the journal against the persisted character so items
    /// survive a relogin; add operations carry the full <c>SerializedItem</c>.
    /// </summary>
    public UnaryResult<ItemOperationResponse> ItemOperation(ItemOperationRequest req)
    {
        var owner = Owner;
        if (req == null || req.CharacterId == Guid.Empty) throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        GameStore.Instance.ApplyItemOperations(owner, req.CharacterId, req.ItemOperations);
        return UnaryResult.FromResult(new ItemOperationResponse { Ok = true });
    }

    /// <summary>Consumes stacks of a single item. This was an unimplemented stub returning 0, so
    /// the client treated every "use" as failed — the Niflheim portal (and other consume-gated
    /// items) could not be used, and consumables were never deducted server-side.</summary>
    public UnaryResult<ConsumeItemResponse> ConsumeItem(ConsumeItemRequest req)
    {
        if (req == null || req.CharacterId == Guid.Empty || req.ConsumedItem == null)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing consume request"));
        var (consumed, allStacks) = GameStore.Instance.ConsumeItem(Owner, req.CharacterId,
            req.ConsumedItem.ItemId, req.ConsumedItem.ConsumeStackAmount);
        return UnaryResult.FromResult(new ConsumeItemResponse { ConsumedStackAmount = consumed, ConsumedAllStacks = allStacks });
    }
}
