using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Client.Logout;

public interface IMrWhoLogoutManager
{
    ValueTask<FrontChannelLogoutRequest> BuildFrontChannelLogoutAsync(FrontChannelLogoutOptions? options = null, CancellationToken cancellationToken = default);

    ValueTask<BackchannelLogoutValidationResult> ValidateBackchannelLogoutAsync(string logoutToken, CancellationToken cancellationToken = default);
}
