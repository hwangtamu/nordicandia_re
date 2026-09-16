using MagicOnion;
using MagicOnion.Server;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

public sealed partial class OfferingServiceApiImpl
{
    /// <summary>
    /// Aesir-offering blessings. The generic stub used <see cref="State.Defaults.Create{T}"/>,
    /// which recursively instantiates the four nested <c>SerializedBuff</c> properties with
    /// <c>DefinitionIntegerId == 0</c> — and definition 0 is <c>MightBuff</c>. The client's
    /// <c>WindowInGame.UpdateBlessings</c> then applied a permanent Might buff to every
    /// character of every class.
    ///
    /// A "no blessing" response leaves all four slots null; the client checks each for null
    /// before calling <c>AddBlessingBuff</c>.
    /// </summary>
    public UnaryResult<GetCurrentBlessingsResponse> GetCurrentBlessings(GetCurrentBlessingsRequest req)
        => UnaryResult.FromResult(new GetCurrentBlessingsResponse
        {
            BlessingOdin = null,
            BlessingTyr = null,
            BlessingFrigg = null,
            BlessingThor = null,
        });
}
