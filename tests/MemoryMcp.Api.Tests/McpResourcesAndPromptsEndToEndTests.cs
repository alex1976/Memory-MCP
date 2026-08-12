using System.Text.Json;
using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Memories;
using MemoryMcp.Application.Spaces;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemoryMcp.Api.Tests;

[Collection(McpApiCollection.Name)]
public sealed class McpResourcesAndPromptsEndToEndTests(McpApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web);

    [Fact]
    public async Task The_three_resources_and_the_context_prompt_are_registered()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var resources = await client.ListResourcesAsync();
        resources.Select(r => r.Uri).Should().Contain(["memory-mcp://profile", "memory-mcp://spaces", "memory-mcp://memories"]);

        var prompts = await client.ListPromptsAsync();
        prompts.Select(p => p.Name).Should().Contain("context");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Resource_memory_mcp_spaces_lists_the_active_space()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var result = await client.ReadResourceAsync("memory-mcp://spaces");
        var json = result.Contents.OfType<TextResourceContents>().First().Text;
        var spaces = JsonSerializer.Deserialize<List<SpaceSummaryDto>>(json, JsonOptions);

        spaces.Should().ContainSingle(s => s.Key == factory.SpaceKey && s.IsDefault);

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Resources_memory_mcp_profile_and_memories_reflect_saved_content()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var addResult = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "The resources test memory" });
        (addResult.IsError ?? false).Should().BeFalse();

        var profileJson = await client.ReadResourceAsync("memory-mcp://profile");
        var profile = JsonSerializer.Deserialize<List<MemorySummaryDto>>(
            profileJson.Contents.OfType<TextResourceContents>().First().Text, JsonOptions);
        profile.Should().Contain(m => m.Text == "The resources test memory");

        var memoriesJson = await client.ReadResourceAsync("memory-mcp://memories");
        var memories = JsonSerializer.Deserialize<PagedResult<MemorySummaryDto>>(
            memoriesJson.Contents.OfType<TextResourceContents>().First().Text, JsonOptions);
        memories!.Items.Should().Contain(m => m.Text == "The resources test memory");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task Prompt_context_returns_a_ready_to_attach_message_for_the_active_space()
    {
        using var httpClient = factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add("X-Api-Key", factory.RawApiKey);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(httpClient.BaseAddress!, "/mcp") },
            httpClient);

        var client = await McpClient.CreateAsync(transport);

        var addResult = await client.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "The context prompt test memory" });
        (addResult.IsError ?? false).Should().BeFalse();

        var prompt = await client.GetPromptAsync("context");
        var text = prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().First().Text;

        text.Should().Contain(factory.SpaceKey);
        text.Should().Contain("The context prompt test memory");

        await client.DisposeAsync();
    }
}
