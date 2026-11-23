using System.Net;
using System.Security.Claims;
using ControlSystem.Server.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerProgram = ControlSystem.Server.Program;

namespace ControlSystem.Tests;

public sealed class AuthenticationTests
{
    private const string AgentToken = "unit-only-agent-token-00000000000000000000000000";
    private const string OperatorToken = "unit-only-operator-token-1111111111111111111111";

    [Theory]
    [InlineData(AgentToken, "Agent")]
    [InlineData(OperatorToken, "Operator")]
    [InlineData("invalid-token-that-does-not-match-000000", null)]
    public async Task Bearer_handler_enforces_roles_and_device_identity(string token, string? expectedRole)
    {
        await using var app = CreateApp();
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Headers.Authorization = "Bearer " + token;
        var result = await context.AuthenticateAsync(TokenAuthenticationHandler.SchemeName);
        if (expectedRole is null) Assert.False(result.Succeeded);
        else
        {
            Assert.True(result.Succeeded);
            Assert.True(result.Principal!.IsInRole(expectedRole));
            var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            Assert.Equal(expectedRole == "Agent" ? TestData.DeviceId.ToString() : "operator", subject);
        }
    }

    [Fact]
    public async Task Api_dashboard_and_signalr_endpoints_carry_required_authorization()
    {
        await using var app = CreateApp();
        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        void HasPolicy(string route, string policy)
        {
            var endpoint = Assert.Single(routes, e => e.RoutePattern.RawText == route);
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), data => data.Policy == policy);
        }
        HasPolicy("/api/v1/heartbeats", "Agent");
        HasPolicy("/api/v1/devices", "OperatorApi");
        HasPolicy("/", "OperatorUi");
        HasPolicy("/_blazor/negotiate", "OperatorUi");
    }

    [Theory]
    [InlineData("/", HttpStatusCode.Redirect)]
    [InlineData("/_blazor/negotiate", HttpStatusCode.Unauthorized)]
    public async Task Cookie_challenge_redirects_browser_page_but_returns_401_for_signalr(string path, HttpStatusCode expectedStatus)
    {
        await using var app = CreateApp();
        await using var scope = app.Services.CreateAsyncScope();
        var endpoint = Assert.Single(
            ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>(),
            route => route.RoutePattern.RawText == path);
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = path;
        context.SetEndpoint(endpoint);

        await context.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal((int)expectedStatus, context.Response.StatusCode);
        if (expectedStatus == HttpStatusCode.Redirect)
            Assert.Equal("/login", new Uri(context.Response.Headers.Location.ToString()).AbsolutePath);
    }

    private static WebApplication CreateApp() => ServerProgram.CreateApp(["--environment", "Testing"], builder =>
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "InMemory",
            ["Security:OperatorTokenHash"] = TokenDigest.Hash(OperatorToken),
            ["Security:Agents:0:DeviceId"] = TestData.DeviceId.ToString(),
            ["Security:Agents:0:TokenHash"] = TokenDigest.Hash(AgentToken)
        }));
}
