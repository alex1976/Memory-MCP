using Microsoft.AspNetCore.Authentication;

namespace MemoryMcp.Api.Auth;

public sealed class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "ApiKey";
}
