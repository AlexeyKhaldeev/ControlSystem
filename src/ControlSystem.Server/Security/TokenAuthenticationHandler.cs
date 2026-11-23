using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ControlSystem.Server.Security;

public sealed class TokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SecurityOptions> credentials) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceToken";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = header[7..];
        var config = credentials.Value;
        if (TokenDigest.Matches(token, config.OperatorTokenHash)) return Success("operator", "Operator");
        var agent = config.Agents.FirstOrDefault(a => TokenDigest.Matches(token, a.TokenHash));
        return agent is null ? Task.FromResult(AuthenticateResult.Fail("Invalid token.")) : Success(agent.DeviceId.ToString(), "Agent");
    }

    private Task<AuthenticateResult> Success(string subject, string role)
    {
        var identity = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, subject), new(ClaimTypes.Role, role)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new(new(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
