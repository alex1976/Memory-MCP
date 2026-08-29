using System.Security.Claims;
using System.Text.Encodings.Web;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace MemoryMcp.Api.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyRepository apiKeyRepository,
    CurrentAccessContext currentAccessContext)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var rawKey = ExtractKey(Request);
        if (string.IsNullOrEmpty(rawKey))
        {
            return AuthenticateResult.NoResult();
        }

        var keyHash = ApiKeyHasher.Hash(rawKey);
        var snapshot = await apiKeyRepository.FindActiveAccessByHashAsync(keyHash, Context.RequestAborted);
        if (snapshot is null)
        {
            return AuthenticateResult.Fail("Invalid or revoked API key.");
        }

        currentAccessContext.Initialize(snapshot);

        // The key identifies the request, the user identifies the person behind it; both are on the
        // principal so request logging can answer "who did what" without reaching into the access context.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, snapshot.ApiKeyId.ToString()),
            new Claim("user_id", snapshot.User.Id.ToString()),
            new Claim(ClaimTypes.Email, snapshot.User.Email),
            new Claim(ClaimTypes.Name, snapshot.User.DisplayName),
            new Claim(ClaimTypes.Role, snapshot.User.Role.ToString()),
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Api-Key", out var headerValue) && !string.IsNullOrWhiteSpace(headerValue))
        {
            return headerValue.ToString();
        }

        var authorizationHeader = request.Headers.Authorization.ToString();
        if (authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader["Bearer ".Length..].Trim();
        }

        return null;
    }
}
