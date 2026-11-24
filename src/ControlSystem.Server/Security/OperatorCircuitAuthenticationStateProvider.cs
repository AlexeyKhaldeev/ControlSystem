using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace ControlSystem.Server.Security;

public sealed class OperatorCircuitAuthenticationStateProvider(
    ILoggerFactory loggerFactory, OperatorCircuitSessions sessions)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromSeconds(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.IsValid(authenticationState.User));
}
