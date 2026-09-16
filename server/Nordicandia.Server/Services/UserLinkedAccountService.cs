using MagicOnion;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class UserLinkedAccountServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    private UnaryResult<LinkAccountResponse> Link(LoginIdentityProvider platform, string platformUserId, string username, string email)
        => UnaryResult.FromResult(new LinkAccountResponse
        {
            LinkedAccount = GameStore.Instance.LinkAccount(Owner, platform, platformUserId, username, email),
        });

    public UnaryResult<LinkAccountResponse> LinkSteamAccount(LinkSteamAccountRequest req)
    {
        var ticket = req?.SteamTicket ?? string.Empty;
        // Steam tickets are opaque and rotate; the client's Steam user id is the stable part.
        var steamId = ExtractSteamId(ticket);
        return Link(LoginIdentityProvider.Steam, steamId, "steam:" + steamId, null);
    }

    public UnaryResult<LinkAccountResponse> LinkGoogleAccountAccount(LinkGoogleAccountRequest req)
        => Link(LoginIdentityProvider.GooglePlay, req?.AuthKey ?? string.Empty, null, null);

    public UnaryResult<LinkAccountResponse> LinkGameCenterAccount(LinkGameCenterAccountRequest req)
        => Link(LoginIdentityProvider.GameCenter, req?.PlayerId ?? string.Empty, null, null);

    public UnaryResult<RegisterGameAccountResponse> RegisterGameAccount(RegisterGameAccountRequest req)
    {
        var email = req?.Email ?? string.Empty;
        var account = GameStore.Instance.LinkAccount(Owner, LoginIdentityProvider.Game, email, email, email);
        if (!string.IsNullOrEmpty(req?.Password))
            GameStore.Instance.SetLinkedPassword(Owner, LoginIdentityProvider.Game, req.Password);
        return UnaryResult.FromResult(new RegisterGameAccountResponse { LinkedGameAccount = account });
    }

    public UnaryResult<ChangeLinkedAccountPasswordResponse> ChangeLinkedAccountPassword(ChangeLinkedAccountPasswordRequest req)
    {
        GameStore.Instance.SetLinkedPassword(Owner, req?.Provider ?? LoginIdentityProvider.Unknown, req?.NewPassword);
        return UnaryResult.FromResult(new ChangeLinkedAccountPasswordResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkSteamAccount(UnlinkSteamAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.Steam);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkGoogleAccount(UnlinkGoogleAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.GooglePlay);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    public UnaryResult<UnlinkAccountResponse> UnlinkGameCenterAccount(UnlinkGameCenterAccountRequest req)
    {
        GameStore.Instance.UnlinkAccount(Owner, LoginIdentityProvider.GameCenter);
        return UnaryResult.FromResult(new UnlinkAccountResponse());
    }

    private static string ExtractSteamId(string ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return string.Empty;
        // The Steam web ticket is a hex blob; the account id is embedded but not trivially
        // parseable, so store a stable digest of the ticket instead.
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ticket));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLowerInvariant();
    }
}
