using Game;
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

    /// <summary>Smelts the items the client placed in the Blacksmith source slot into Steel,
    /// using the recovered essence->steel coefficients.</summary>
    public UnaryResult<SmeltItemsResponse> SmeltItems(SmeltItemsRequest req)
    {
        if (req == null || req.CharacterId == Guid.Empty)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        var (successful, sourceItems, result) = GameStore.Instance.SmeltItems(Owner, req.CharacterId);
        return UnaryResult.FromResult(new SmeltItemsResponse
        {
            Successful = successful,
            SourceItems = sourceItems,
            SmeltingResult = result,
        });
    }

    /// <summary>Relic craft: consumes the Blacksmith source relics and blesses the target item's
    /// affixes (RelicOfBlessing.InternalCraft).</summary>
    public UnaryResult<CraftRelicItemResponse> CraftRelicItem(CraftRelicItemRequest req)
    {
        if (req == null || req.CharacterId == Guid.Empty)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        var (success, sourceItems, result) = GameStore.Instance.CraftRelicItem(Owner, req.CharacterId);
        return UnaryResult.FromResult(new CraftRelicItemResponse
        {
            Success = success,
            SourceItems = sourceItems,
            Result = result,
        });
    }

    /// <summary>Essence craft. Consumes Iron with the recovered cost formula and rolls the recovered
    /// success chance, merging the source items' affixes onto the Blacksmith target on success.</summary>
    public UnaryResult<CraftEssenceItemResponse> CraftEssenceItem(CraftEssenceItemRequest req)
    {
        if (req == null || req.CharacterId == Guid.Empty)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        var (ok, success, result, _, _) = GameStore.Instance.CraftEssenceItem(Owner, req.CharacterId, req.OverheatSliderValue);
        return UnaryResult.FromResult(new CraftEssenceItemResponse
        {
            OperationSuccessful = ok,
            SuccessFirstTime = success,
            Result = result,
            Tools = new SerializedItems { Items = new List<SerializedItem>() },
            ChangedIronInstances = new List<IronStackEntry>(),
        });
    }

    /// <summary>Essence craft (v2 request shape: Iron + Titansteel hints).</summary>
    public UnaryResult<CraftEssenceItemResponse2> CraftEssenceItem2(CraftEssenceItemRequest2 req)
    {
        if (req == null || req.CharacterId == Guid.Empty)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        var (ok, success, result, _, _) = GameStore.Instance.CraftEssenceItem(Owner, req.CharacterId, req.OverheatSliderValue);
        return UnaryResult.FromResult(new CraftEssenceItemResponse2
        {
            OperationSuccessful = ok,
            SuccessFirstTime = success,
            Result = result,
            Tools = new SerializedItems { Items = new List<SerializedItem>() },
            ChangedIronInstances = new List<ReagentStackEntry>(),
            ChangedTitansteelInstances = new List<ReagentStackEntry>(),
        });
    }

    /// <summary>Disassembles the Blacksmith source items into Iron. The selection rule matches the
    /// client (not unique, not a set item); the material amount is Provisional.</summary>
    public UnaryResult<DisassembleItemsResponse> DisassembleItems(DisassembleItemsRequest req)
    {
        if (req == null || req.CharacterId == Guid.Empty)
            throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "Missing character"));
        var (successful, sourceItems, result) = GameStore.Instance.DisassembleItems(Owner, req.CharacterId);
        return UnaryResult.FromResult(new DisassembleItemsResponse
        {
            Successful = successful,
            SourceItems = sourceItems,
            DisassembleResult = result,
        });
    }
}
