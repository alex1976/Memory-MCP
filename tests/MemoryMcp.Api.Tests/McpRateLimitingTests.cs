using System.Net;
using AwesomeAssertions;

namespace MemoryMcp.Api.Tests;

[Collection(McpApiCollection.Name)]
public sealed class McpRateLimitingTests(McpApiFactory factory)
{
    private static StringContent Json() => new("{}", System.Text.Encoding.UTF8, "application/json");

    [Fact]
    public async Task Requests_beyond_the_limit_are_rejected_with_429_and_retry_after()
    {
        const int permitLimit = 3;
        await using var limited = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:PermitLimit", permitLimit.ToString());
            builder.UseSetting("RateLimiting:WindowSeconds", "60");
        });
        var client = limited.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", $"mmcp_{Guid.NewGuid():N}");

        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < permitLimit + 1; i++)
        {
            last = await client.PostAsync("/mcp", Json());
            statuses.Add(last.StatusCode);
        }

        // Within the budget the (unknown) key is rejected by authentication; past it, by the limiter,
        // which runs first and so never reaches the database.
        statuses.Take(permitLimit).Should().AllBeEquivalentTo(HttpStatusCode.Unauthorized);
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests);
        last!.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task Each_key_has_its_own_budget_and_health_is_never_limited()
    {
        await using var limited = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:PermitLimit", "1"));

        var first = limited.CreateClient();
        first.DefaultRequestHeaders.Add("X-Api-Key", $"mmcp_{Guid.NewGuid():N}");
        var second = limited.CreateClient();
        second.DefaultRequestHeaders.Add("X-Api-Key", $"mmcp_{Guid.NewGuid():N}");

        (await first.PostAsync("/mcp", Json())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await first.PostAsync("/mcp", Json())).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await second.PostAsync("/mcp", Json())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        for (var i = 0; i < 3; i++)
        {
            (await first.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
