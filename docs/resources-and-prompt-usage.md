# Using the Phase 3 resources and prompt

Phase 3 (see [README.md](../README.md#available-mcp-resources-and-prompts)) adds 3 MCP **resources**
and 1 MCP **prompt**, implemented in `src/MemoryMcp.Api/Resources` and `src/MemoryMcp.Api/Prompts`.
They sit next to the 7 tools on the same `/mcp` endpoint, protected by the same `X-Api-Key` header —
there is no separate auth path for them. Not every MCP client surfaces resources/prompts in its UI
(support varies); this doc shows how to call them directly, at the protocol and client-library level,
for clients that do.

| Kind | URI / Name | Returns |
| --- | --- | --- |
| Resource | `memory-mcp://profile` | JSON array of `MemorySummaryDto` — recent active memories in the active space |
| Resource | `memory-mcp://spaces` | JSON array of `SpaceSummaryDto` — accessible spaces, active one marked |
| Resource | `memory-mcp://memories` | JSON `PagedResult<MemorySummaryDto>` — first page of the active space's memories (any status) |
| Prompt | `context` | A single ready-to-attach text message (no arguments) |

All three resources are fixed, non-templated URIs scoped to the API key's *active* space — none take
a `containerTag`/space argument. If you need another space's data, switch that key's active space
(via a grant with `IsDefault=true`) rather than parameterizing the URI.

## Discovering what's available

```json
// → server
{"jsonrpc": "2.0", "id": 1, "method": "resources/list"}

// ← server
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "resources": [
      { "uri": "memory-mcp://profile", "name": "profile", "mimeType": "application/json",
        "description": "Stable and recent profile context (the same recent-active-memories set attached to search_memory) for the active space." },
      { "uri": "memory-mcp://spaces", "name": "spaces", "mimeType": "application/json",
        "description": "A compact list of spaces accessible to the current API key, with the active space marked." },
      { "uri": "memory-mcp://memories", "name": "memories", "mimeType": "application/json",
        "description": "The most recently created memories (any status) in the active space — the first page of listMemories." }
    ]
  }
}
```

```json
// → server
{"jsonrpc": "2.0", "id": 2, "method": "prompts/list"}

// ← server
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "prompts": [
      { "name": "context",
        "description": "A ready-to-attach context message: profile for the active space, plus up to three other recently active spaces." }
    ]
  }
}
```

## Reading a resource

`resources/read` takes the URI verbatim — no arguments object, since these are direct (non-templated)
resources:

```json
// → server
{"jsonrpc": "2.0", "id": 3, "method": "resources/read", "params": {"uri": "memory-mcp://spaces"}}

// ← server
{
  "jsonrpc": "2.0",
  "id": 3,
  "result": {
    "contents": [
      {
        "uri": "memory-mcp://spaces",
        "mimeType": "application/json",
        "text": "[{\"id\":\"3fa85f64-5717-4562-b3fc-2c963f66afa6\",\"key\":\"default\",\"name\":\"Default Space\",\"accessLevel\":\"ReadWrite\",\"isDefault\":true,\"documentCount\":4,\"memoryCount\":9}]"
      }
    ]
  }
}
```

The `text` field is a JSON string (the resource's `mimeType` is `application/json`); decode it like
any other tool response. Pretty-printed, that payload is a `SpaceSummaryDto[]`:

```json
[
  {
    "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
    "key": "default",
    "name": "Default Space",
    "accessLevel": "ReadWrite",
    "isDefault": true,
    "documentCount": 4,
    "memoryCount": 9
  }
]
```

`memory-mcp://profile` and `memory-mcp://memories` both decode to memory lists
(`MemorySummaryDto` — `id`, `text`, `version`, `documentId`, `isActive`, `createdAt`, `category`;
`documentId`/`category` are omitted when null). The difference is shape and filtering:

```json
// memory-mcp://profile → MemorySummaryDto[] (active memories only, capped at 5, newest first)
[
  { "id": "b7e6...", "text": "Alex is a PM at Stripe", "version": 1, "isActive": true, "createdAt": "2026-08-12T09:14:02Z" }
]
```

```json
// memory-mcp://memories → PagedResult<MemorySummaryDto> (any status, page 1, 10 per page)
{
  "items": [
    { "id": "b7e6...", "text": "Alex is a PM at Stripe", "version": 1, "isActive": true, "createdAt": "2026-08-12T09:14:02Z" }
  ],
  "page": 1,
  "limit": 10,
  "totalCount": 1
}
```

If the active space can't be resolved (e.g. an API key with no default grant), the read fails with an
MCP protocol error carrying the same message `add_memory`/`search_memory` would give for the
equivalent case (`SpaceNotFoundException`) — there's no `isError` field for resources the way there is
for tool calls, so treat any error response here as fatal to that read, not as a soft failure.

## Getting the prompt

`prompts/get` takes no arguments for `context`:

```json
// → server
{"jsonrpc": "2.0", "id": 4, "method": "prompts/get", "params": {"name": "context"}}

// ← server
{
  "jsonrpc": "2.0",
  "id": 4,
  "result": {
    "messages": [
      {
        "role": "user",
        "content": {
          "type": "text",
          "text": "Active space: \"Default Space\" (default)\n\nRecent memories:\n- Alex is a PM at Stripe\n- Invoice due the 5th\n\nOther recently active spaces:\n- \"Research\" (research) — last activity 2026-08-10\n- \"Personal\" (personal) — no activity yet"
        }
      }
    ]
  }
}
```

The message text (unescaped) looks like this — it's meant to be pasted or attached directly into a
conversation as context, not parsed programmatically:

```
Active space: "Default Space" (default)

Recent memories:
- Alex is a PM at Stripe
- Invoice due the 5th

Other recently active spaces:
- "Research" (research) — last activity 2026-08-10
- "Personal" (personal) — no activity yet
```

Notes on how it's built (`src/MemoryMcp.Api/Prompts/ContextPrompt.cs`):
- "Recent memories" is the same `memory-mcp://profile` content for the active space; if there are none
  yet, the line reads `No memories saved yet in this space.` instead.
- "Other recently active spaces" lists up to 3 of the API key's *other* granted spaces, ranked by each
  space's most recent memory (there's no "last used" timestamp on a grant, so recency is a proxy).
  A space with no memories yet still appears, sorted last, as `no activity yet`. If the key only has
  one space, this section is omitted entirely.

## From a .NET MCP client

Using `ModelContextProtocol.Client.McpClient` (the same client the end-to-end tests use — see
`tests/MemoryMcp.Api.Tests/McpResourcesAndPromptsEndToEndTests.cs` for the full runnable versions):

```csharp
var client = await McpClient.CreateAsync(transport); // transport carries the X-Api-Key header

// Resources
var resources = await client.ListResourcesAsync();
var spacesResult = await client.ReadResourceAsync("memory-mcp://spaces");
var spacesJson = spacesResult.Contents.OfType<TextResourceContents>().First().Text;
var spaces = JsonSerializer.Deserialize<List<SpaceSummaryDto>>(spacesJson, new JsonSerializerOptions(JsonSerializerOptions.Web));

// Prompt
var prompt = await client.GetPromptAsync("context");
var contextMessage = prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().First().Text;
```

## From Claude Desktop / MCP Inspector

Once connected (see the README's [Claude Desktop section](../README.md#7-optional-connect-claude-desktop)),
a client that implements the Resources/Prompts primitives will list `memory-mcp://profile`,
`memory-mcp://spaces`, `memory-mcp://memories`, and the `context` prompt alongside the 7 tools — for
example, in the [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector)'s "Resources" and
"Prompts" tabs, where you can read/get them interactively without writing any JSON-RPC by hand. Support
for these two primitives is client-dependent; if they don't show up, the 7 tools are unaffected and
work exactly as before.
