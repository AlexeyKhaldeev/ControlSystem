using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ControlSystem.Server.Security;

public sealed class OperatorCircuitCookieEvents(OperatorCircuitSessions sessions) : CookieAuthenticationEvents
{
    public override Task SigningIn(CookieSigningInContext context)
    {
        var expires = context.Properties.ExpiresUtc
            ?? throw new InvalidOperationException("Operator cookie requires an expiry.");
        if (context.Principal?.Identity is not ClaimsIdentity identity)
            throw new InvalidOperationException("Operator cookie requires a claims identity.");
        foreach (var claim in context.Principal.FindAll(OperatorCircuitSessions.SessionIdClaimType).ToArray())
            claim.Subject?.RemoveClaim(claim);
        identity.AddClaim(new Claim(OperatorCircuitSessions.SessionIdClaimType, sessions.Create(expires)));
        return Task.CompletedTask;
    }

    public override Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null || !sessions.IsValid(context.Principal)) context.RejectPrincipal();
        return Task.CompletedTask;
    }

    public override Task SigningOut(CookieSigningOutContext context)
    {
        sessions.Revoke(context.HttpContext.User);
        return Task.CompletedTask;
    }
}
