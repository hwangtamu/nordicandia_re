using MagicOnion;
using MagicOnion.Server;
using SharedNet.Api;
using SharedNet.Dto;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed class LoginService : ServiceBase<ILoginServiceApi>, ILoginServiceApi
{
    public UnaryResult<LoginResponse> LoginWithStandaloneDeviceIdAsync(LoginWithStandaloneDeviceIdRequest req)
        => Login(req?.DeviceId, "device", SharedNet.Constants.LoginIdentityProvider.StandaloneDevice);
    public UnaryResult<LoginResponse> LoginWithSteamAsync(LoginWithSteamRequest req)
        => Login(req?.DeviceId ?? req?.SteamTicket, "steam", SharedNet.Constants.LoginIdentityProvider.Steam);
    public UnaryResult<LoginResponse> LoginWithGooglePlayAsync(LoginWithGooglePlayRequest req)
        => Login(req?.DeviceId ?? req?.ServerAuthCode, "google", SharedNet.Constants.LoginIdentityProvider.GooglePlay);
    public UnaryResult<LoginResponse> LoginWithGameCenterAsync(LoginWithGameCenterRequest req)
        => Login(req?.PlayerId ?? req?.DeviceId, "gc", SharedNet.Constants.LoginIdentityProvider.GameCenter);
    public UnaryResult<LoginResponse> LoginWithEmailAsync(LoginWithEmailRequest req)
        => Login(req?.Email ?? req?.DeviceId, "email", SharedNet.Constants.LoginIdentityProvider.Game);
    public UnaryResult<LoginResponse> LoginWithAdminAsync(LoginWithAdminRequest req)
        => Login(req?.UserId.ToString() ?? req?.DeviceId, "admin");
    public UnaryResult<LoginResponse> LoginWithUsernameAsync(LoginWithUsernameRequest req)
        => Login(req?.Username ?? req?.DeviceId, "username", SharedNet.Constants.LoginIdentityProvider.Game);

    public UnaryResult<LoginResponse> LoginWithRefreshTokenAsync(LoginWithRefreshAuthTokenRequest req)
    {
        var user = GameStore.Instance.FindUserByRefreshToken(req?.RefreshToken);
        if (user == null) return UnaryResult.FromResult(new LoginResponse());
        return UnaryResult.FromResult(BuildLogin(user));
    }

    private static UnaryResult<LoginResponse> Login(string identity, string kind,
        SharedNet.Constants.LoginIdentityProvider? platform = null)
    {
        if (string.IsNullOrEmpty(identity)) identity = "guest-" + Guid.NewGuid().ToString("N");
        var user = GameStore.Instance.GetOrCreateUser(kind + ":" + identity);
        // The client checks GetUserAccountData.LinkedAccounts after login and shows the
        // account as "unregistered" when it is empty, so link the identity that was used.
        if (platform is { } p)
            GameStore.Instance.LinkAccount(user.UserId, p, kind + ":" + identity, kind + ":" + identity,
                kind is "email" or "username" ? identity : null);
        return UnaryResult.FromResult(BuildLogin(user));
    }

    internal static LoginResponse BuildLogin(UserDto user) => new LoginResponse
    {
        User = user,
        Session = GameStore.Instance.CreateSession(user.UserId),
        ServerTime = DateTime.UtcNow,
    };

    public UnaryResult<GetNonceResponse> GetNonce(GetNonceRequest req)
        => UnaryResult.FromResult(new GetNonceResponse { Nonce = Guid.NewGuid().ToString("N") });

    public UnaryResult<GetUserAccountDataResponse> GetUserAccountData(GetUserAccountDataRequest req)
    {
        var owner = GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));
        var response = Defaults.Create<GetUserAccountDataResponse>();
        response.AccountData.NumCharacterSlots = 3;
        response.LinkedAccounts = new UserLinkedAccountListDto { LinkedAccounts = GameStore.Instance.GetLinkedAccounts(owner) };
        return UnaryResult.FromResult(response);
    }

    public UnaryResult<KeepAliveResponse> KeepAlive(KeepAliveRequest req)
        => UnaryResult.FromResult(new KeepAliveResponse { ServerTime = DateTime.UtcNow });

    public UnaryResult<SendAnalyticsResponse> SendAnalytics(SendAnalyticsRequest req)
        => UnaryResult.FromResult(new SendAnalyticsResponse());

    public UnaryResult<ForgotPasswordResponse> ForgotPassword(ForgotPasswordRequest req)
        => UnaryResult.FromResult(new ForgotPasswordResponse());

    public UnaryResult<ChangeEmailPasswordResponse> ChangeEmailPassword(ChangeEmailPasswordRequest req)
        => UnaryResult.FromResult(new ChangeEmailPasswordResponse());
}
