using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace ControlSystem.Server.Security;

public static class OperatorEndpoints
{
    public static void MapOperatorEndpoints(this WebApplication app)
    {
        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            var token = HtmlEncoder.Default.Encode(tokens.RequestToken!);
            return Results.Content($$"""
                <!doctype html><html lang="ru"><meta charset="utf-8"><meta name="viewport" content="width=device-width">
                <title>Вход — ControlSystem</title><link rel="stylesheet" href="/app.css">
                <main class="login"><p class="eyebrow">CONTROLSYSTEM / MONITORING CORE</p><h1>Вход оператора</h1>
                <p>Введите ключ оператора, созданный при настройке сервера.</p>
                <form method="post" action="/login">
                <input type="hidden" name="__RequestVerificationToken" value="{{token}}">
                <label for="token">Ключ доступа</label><input id="token" type="password" name="token" required maxlength="512" autocomplete="current-password">
                <button type="submit">Открыть мониторинг</button></form></main></html>
                """, "text/html; charset=utf-8");
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery, IOptions<SecurityOptions> security) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            if (!TokenDigest.Matches(form["token"].ToString(), security.Value.OperatorTokenHash)) return Results.Unauthorized();
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, "operator"), new(ClaimTypes.Role, "Operator")
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).RequireAuthorization("OperatorUi");
    }
}
