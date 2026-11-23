namespace ControlSystem.Agent;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "https://localhost:7443/";
    public Guid DeviceId { get; set; }
    public string Token { get; set; } = "";
    public string? TrustedCaPath { get; set; }

    public bool IsValid() => DeviceId != Guid.Empty && Token.Length is >= 32 and <= 512 &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/";
}
