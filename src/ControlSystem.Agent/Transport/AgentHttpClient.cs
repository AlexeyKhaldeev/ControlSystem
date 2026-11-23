using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ControlSystem.Agent.Transport;

public static class AgentHttpClient
{
    public static HttpClient Create(AgentOptions options)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (!string.IsNullOrWhiteSpace(options.TrustedCaPath))
        {
            var rootBytes = File.ReadAllBytes(options.TrustedCaPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, presentedChain, errors) =>
            {
                if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                using var root = X509Certificate2.CreateFromPem(System.Text.Encoding.UTF8.GetString(rootBytes));
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                if (presentedChain is not null)
                    foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new("1.3.6.1.5.5.7.3.1"));
                return chain.Build(certificate);
            };
        }
        var client = new HttpClient(handler) { BaseAddress = new(options.ServerUrl), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        return client;
    }
}
