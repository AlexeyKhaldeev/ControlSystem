using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ControlSystem.Server.Security;

/// <summary>Single-server, volatile operator sessions. Restart invalidates all UI cookies.</summary>
public sealed class OperatorCircuitSessions(TimeProvider clock)
{
    public const string SessionIdClaimType = "controlsystem:session";
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public string Create(DateTimeOffset expires)
    {
        var now = clock.GetUtcNow();
        foreach (var session in _sessions)
            if (session.Value <= now) _sessions.TryRemove(session.Key, out _);
        if (expires <= now) throw new ArgumentOutOfRangeException(nameof(expires));
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!_sessions.TryAdd(id, expires)) throw new InvalidOperationException("Cannot allocate operator session.");
        return id;
    }

    public bool IsValid(ClaimsPrincipal principal)
    {
        if (!principal.Identities.Any(identity => identity.IsAuthenticated && identity.AuthenticationType == CookieAuthenticationDefaults.AuthenticationScheme)
            || !principal.IsInRole("Operator")) return false;
        var ids = principal.FindAll(SessionIdClaimType).ToArray();
        if (ids.Length != 1 || !_sessions.TryGetValue(ids[0].Value, out var expires)) return false;
        if (clock.GetUtcNow() < expires) return true;
        _sessions.TryRemove(ids[0].Value, out _);
        return false;
    }

    public void Revoke(ClaimsPrincipal principal)
    {
        foreach (var id in principal.FindAll(SessionIdClaimType)) _sessions.TryRemove(id.Value, out _);
    }
}
