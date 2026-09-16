using MagicOnion;
using SharedNet.Api;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class VirtualCurrencyServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    public UnaryResult<GainSilverResponse> GainSilver(GainSilverRequest req)
    {
        var owner = Owner;
        var amount = req?.Amount ?? 0;
        var total = GameStore.Instance.AddSilver(owner, req.CharacterId, Math.Abs(amount));
        return UnaryResult.FromResult(new GainSilverResponse { NewSilver = total });
    }

    public UnaryResult<SpendSilverResponse> SpendSilver(SpendSilverRequest req)
    {
        var owner = Owner;
        var amount = req?.Amount ?? 0;
        var total = GameStore.Instance.AddSilver(owner, req.CharacterId, -Math.Abs(amount));
        return UnaryResult.FromResult(new SpendSilverResponse { NewSilver = total });
    }

    public UnaryResult<GainVirtualCurrencyResponse> GainVirtualCurrency(GainVirtualCurrencyRequest req)
    {
        var owner = Owner;
        var amount = req?.Amount ?? 0;
        var total = GameStore.Instance.AddOpals(owner, req.CharacterId, Math.Abs(amount));
        return UnaryResult.FromResult(new GainVirtualCurrencyResponse { NewOpals = total });
    }

    public UnaryResult<SpendVirtualCurrencyResponse> SpendVirtualCurrency(SpendVirtualCurrencyRequest req)
    {
        var owner = Owner;
        var amount = req?.Amount ?? 0;
        var total = GameStore.Instance.AddOpals(owner, req.CharacterId, -Math.Abs(amount));
        return UnaryResult.FromResult(new SpendVirtualCurrencyResponse { NewOpals = total });
    }
}
