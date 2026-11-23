using System.Security.Cryptography;
using System.Text;

namespace ControlSystem.Server.Security;

public sealed class AgentCredential
{
    public Guid DeviceId { get; set; }
    public string TokenHash { get; set; } = "";
}

public sealed class SecurityOptions
{
    public string OperatorTokenHash { get; set; } = "";
    public List<AgentCredential> Agents { get; set; } = [];

    public bool IsValid() => TokenDigest.IsHash(OperatorTokenHash) && Agents.Count is > 0 and <= 100 &&
        Agents.All(a => a.DeviceId != Guid.Empty && TokenDigest.IsHash(a.TokenHash)) &&
        Agents.Select(a => a.DeviceId).Distinct().Count() == Agents.Count &&
        Agents.Select(a => a.TokenHash.ToUpperInvariant()).Append(OperatorTokenHash.ToUpperInvariant()).Distinct().Count() == Agents.Count + 1;
}

public static class TokenDigest
{
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool Matches(string token, string expected) => token.Length is >= 32 and <= 512 && IsHash(expected) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), Convert.FromHexString(expected));
}
