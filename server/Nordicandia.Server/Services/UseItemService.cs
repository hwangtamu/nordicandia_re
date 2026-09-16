using MagicOnion;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

public sealed partial class UseItemServiceApiImpl
{
    /// <summary>Consuming a buff item. The generic stub returned <c>Defaults.Create</c>, whose
    /// nested <c>GivenBuff</c> defaulted to <c>DefinitionIntegerId == 0</c> (MightBuff) and was
    /// applied by the client. We do not model per-item buffs yet, so return no buff.</summary>
    public UnaryResult<UseItemWithBuffResponse> UseItemWithBuff(UseItemWithBuffRequest req)
        => UnaryResult.FromResult(new UseItemWithBuffResponse { GivenBuff = null });
}
