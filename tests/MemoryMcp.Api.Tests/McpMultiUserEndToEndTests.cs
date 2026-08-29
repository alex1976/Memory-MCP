using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using MemoryMcp.Application.Abstractions;
using MemoryMcp.Application.Documents;
using MemoryMcp.Application.Memories;
using MemoryMcp.Application.Spaces;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MemoryMcp.Api.Tests;

/// <summary>
/// The multi-user rules, exercised over real HTTP with two credentials belonging to two people:
/// a Writer and a Reader sharing one space (see <see cref="McpApiFactory"/>). These are the guarantees
/// that only become observable once a space has more than one member.
/// </summary>
[Collection(McpApiCollection.Name)]
public sealed class McpMultiUserEndToEndTests(McpApiFactory factory)
{
    private static readonly JsonSerializerOptions ToolResultOptions = CreateToolResultOptions();

    private static JsonSerializerOptions CreateToolResultOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    [Fact]
    public async Task WhoAmI_reports_the_person_behind_the_key_and_their_role()
    {
        var writerClient = await factory.CreateMcpClientAsync(factory.RawApiKey);
        var readerClient = await factory.CreateMcpClientAsync(factory.ReaderRawApiKey);

        var writer = await CallAsync<WhoAmIResult>(writerClient, "whoAmI", []);
        var reader = await CallAsync<WhoAmIResult>(readerClient, "whoAmI", []);

        writer.UserDisplayName.Should().Be(McpApiFactory.WriterDisplayName);
        writer.UserRole.Should().Be("Writer");
        reader.UserDisplayName.Should().Be(McpApiFactory.ReaderDisplayName);
        reader.UserRole.Should().Be("Reader");

        // Two people, two identities, same space.
        writer.UserId.Should().NotBe(reader.UserId);
        reader.ActiveSpaceKey.Should().Be(factory.SpaceKey);

        await writerClient.DisposeAsync();
        await readerClient.DisposeAsync();
    }

    [Fact]
    public async Task A_readers_grant_is_reported_as_read_even_though_the_grant_row_says_read_write()
    {
        var readerClient = await factory.CreateMcpClientAsync(factory.ReaderRawApiKey);

        var spaces = await CallAsync<List<SpaceSummaryDto>>(readerClient, "listSpaces", []);

        spaces.Should().ContainSingle(s => s.Key == factory.SpaceKey && s.AccessLevel == "Read");

        await readerClient.DisposeAsync();
    }

    [Fact]
    public async Task A_reader_cannot_write_but_can_read_everything_a_writer_saved()
    {
        var writerClient = await factory.CreateMcpClientAsync(factory.RawApiKey);
        var readerClient = await factory.CreateMcpClientAsync(factory.ReaderRawApiKey);

        var uniqueFact = $"The release train departs on Thursday {Guid.NewGuid():N}";
        var saved = await writerClient.CallToolAsync("add_memory", new Dictionary<string, object?> { ["content"] = uniqueFact });
        (saved.IsError ?? false).Should().BeFalse();

        // Write attempts by a Reader are refused for both entity types.
        var readerSave = await readerClient.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = "a reader should not be able to save this" });
        (readerSave.IsError ?? false).Should().BeTrue();

        var readerForget = await readerClient.CallToolAsync(
            "add_memory", new Dictionary<string, object?> { ["content"] = uniqueFact, ["action"] = "forget" });
        (readerForget.IsError ?? false).Should().BeTrue();

        var readerUpload = await readerClient.CallToolAsync(
            "create_document",
            new Dictionary<string, object?> { ["title"] = "nope", ["docType"] = "text", ["content"] = "nope" });
        (readerUpload.IsError ?? false).Should().BeTrue();

        // ...but reads are not filtered by author: the Reader sees the Writer's fact, attributed.
        var search = await CallAsync<SearchMemoryResult>(
            readerClient,
            "search_memory",
            new Dictionary<string, object?> { ["keyword"] = "release train", ["includeProfile"] = false });

        search.Matches.Should().Contain(m => m.Text.Contains("release train") && m.CreatedBy == McpApiFactory.WriterDisplayName);

        await writerClient.DisposeAsync();
        await readerClient.DisposeAsync();
    }

    [Fact]
    public async Task Memories_and_documents_saved_through_the_tools_carry_their_author()
    {
        var writerClient = await factory.CreateMcpClientAsync(factory.RawApiKey);

        var title = $"Handbook {Guid.NewGuid():N}";
        var created = await CallAsync<DocumentSummaryDto>(
            writerClient,
            "create_document",
            new Dictionary<string, object?> { ["title"] = title, ["docType"] = "text", ["content"] = "content" });

        created.CreatedBy.Should().Be(McpApiFactory.WriterDisplayName);
        created.CreatedByUserId.Should().NotBeNull();

        var detail = await CallAsync<DocumentDetailDto>(
            writerClient, "getDocument", new Dictionary<string, object?> { ["documentId"] = created.Id });
        detail.CreatedBy.Should().Be(McpApiFactory.WriterDisplayName);
        detail.UpdatedBy.Should().Be(McpApiFactory.WriterDisplayName);

        var memories = await CallAsync<PagedResult<MemorySummaryDto>>(
            writerClient, "listMemories", new Dictionary<string, object?> { ["limit"] = 50 });
        memories.Items.Should().Contain(m => m.CreatedBy == McpApiFactory.WriterDisplayName);

        var documents = await CallAsync<PagedResult<DocumentSummaryDto>>(
            writerClient, "listDocuments", new Dictionary<string, object?> { ["limit"] = 50 });
        documents.Items.Should().Contain(d => d.Title == title && d.CreatedBy == McpApiFactory.WriterDisplayName);

        await writerClient.DisposeAsync();
    }

    [Fact]
    public async Task Reads_never_cross_into_a_space_the_key_holds_no_grant_on()
    {
        var writerClient = await factory.CreateMcpClientAsync(factory.RawApiKey);

        // The seeded memory in the ungranted space is written directly to the database with the same
        // embedding a save would produce, so a leaking search would rank it first rather than miss it.
        var semantic = await CallAsync<SearchMemoryResult>(
            writerClient,
            "search_memory",
            new Dictionary<string, object?>
            {
                ["query"] = McpApiFactory.UngrantedSpaceMemoryText,
                ["includeProfile"] = false,
            });
        semantic.Matches.Should().NotContain(m => m.Text == McpApiFactory.UngrantedSpaceMemoryText);

        var keyword = await CallAsync<SearchMemoryResult>(
            writerClient,
            "search_memory",
            new Dictionary<string, object?> { ["keyword"] = "no test key can reach", ["includeProfile"] = false });
        keyword.Matches.Should().BeEmpty();

        // Naming the space explicitly is refused too — an ungranted space is indistinguishable from a
        // nonexistent one.
        var byTag = await writerClient.CallToolAsync(
            "search_memory",
            new Dictionary<string, object?>
            {
                ["keyword"] = "no test key can reach",
                ["containerTag"] = factory.UngrantedSpaceKey,
            });
        (byTag.IsError ?? false).Should().BeTrue();

        await writerClient.DisposeAsync();
    }

    private static async Task<T> CallAsync<T>(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        (result.IsError ?? false).Should().BeFalse($"{tool} should have succeeded but returned: {Text(result)}");

        return JsonSerializer.Deserialize<T>(Text(result), ToolResultOptions)
            ?? throw new InvalidOperationException($"{tool} returned a null {typeof(T).Name}.");
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
