# The `context` MCP prompt: how it works and how to use it

Memory-MCP exposes exactly one MCP **prompt**, named `context`, implemented in
[ContextPrompt.cs](../src/MemoryMcp.Api/Prompts/ContextPrompt.cs). It lives on the same `/mcp`
endpoint as the tools, the resources, and the widgets, behind the same `X-Api-Key` header — there is
no separate auth path for prompts.

A prompt is not a tool: the client does not call it to *do* something and get a result back. It
fetches a pre-built **message** that it can drop into the conversation as-is. `context` answers one
question — *"what should the model already know before I start talking in this space?"* — and answers
it in a single block of plain text, ready to paste or attach.

| | |
| --- | --- |
| Name | `context` |
| Arguments | none |
| Returns | one `user` message with a single text content block |
| Access required | Read on the active space (any authenticated key with a default grant) |
| Source | `src/MemoryMcp.Api/Prompts/ContextPrompt.cs` |
| Tests | `tests/MemoryMcp.Api.Tests/McpResourcesAndPromptsEndToEndTests.cs` |

## What it contains

The message has two sections:

```
Active space: "Default Space" (default)

Recent memories:
- Alex is a PM at Stripe
- Invoice due the 5th

Other recently active spaces:
- "Research" (research) — last activity 2026-08-10
- "Personal" (personal) — no activity yet
```

1. **Header** — the active space's display name and key, so the reader (human or model) can tell
   immediately which space the rest of the message came from.
2. **Recent memories** — the *profile* of the active space: the 5 most recent **active** memories,
   newest first (`MemoryService.GetProfileAsync` → `ListRecentActiveAsync`, `ProfileTake = 5`). This
   is byte-for-byte the same set that `search_memory` attaches when `includeProfile` is true, and the
   same set the `memory-mcp://profile` resource returns — only rendered as bullets instead of JSON.
   When the space has no memories yet, the section is replaced by the single line
   `No memories saved yet in this space.`
3. **Other recently active spaces** — up to 3 of the key's *other* granted spaces, so the reader knows
   what else is reachable without calling `listSpaces`. Omitted entirely when the key has only one
   space.

## How the space list is ranked

There is no "last used" timestamp on `ApiKeySpaceGrant`, so recency is a **proxy**: for each other
granted space the prompt asks `ListMemoriesAsync(page: 1, limit: 1)` and uses the `createdAt` of that
single newest memory. Spaces are then ordered newest-first and the top 3 are kept.

Two consequences worth knowing:

- A space you have read a lot from but never written to looks "old" — reads don't move it up.
- A space with no memories at all still appears (sorted last, rendered as `no activity yet`) rather
  than being filtered out, so a freshly created space is still discoverable from the prompt.

The ranking loop is one query per granted space. That is fine for the handful of spaces a key
normally holds; it is not something to point at a key granted on hundreds of spaces.

## Which space it uses

`context` always uses the **active space** — `ICurrentAccessContext.ActiveGrant`, which is the grant
flagged `IsDefault` for the presented API key. The prompt takes no `containerTag` argument, so there
is no way to ask it for a different space in a single call. To point it elsewhere, switch the active
space first (`setActiveSpace`, or the `select-space` widget) and fetch the prompt again.

If the key has no default grant, `ActiveGrant` is null and the prompt fails with `SpaceNotFoundException`,
which `McpExecution` turns into an `McpException` carrying the same message the tools would give for
the equivalent case. Unlike a tool call there is no `isError` flag to inspect — a `prompts/get` error
response is a hard failure of that fetch, not a soft one.

Everything in the message is scoped to that one space, and to the grants of the key that asked. A
`Reader` gets exactly the same message a `Writer` would: reading the prompt requires Read, and the
prompt writes nothing.

## Calling it over JSON-RPC

Discovery:

```json
// → server
{"jsonrpc": "2.0", "id": 1, "method": "prompts/list"}

// ← server
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "prompts": [
      { "name": "context",
        "description": "A ready-to-attach context message: profile for the active space, plus up to three other recently active spaces." }
    ]
  }
}
```

Fetch — no `arguments` object, because `context` declares no arguments:

```json
// → server
{"jsonrpc": "2.0", "id": 2, "method": "prompts/get", "params": {"name": "context"}}

// ← server
{
  "jsonrpc": "2.0",
  "id": 2,
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

The text is meant to be **attached, not parsed**. It is prose formatting produced by
`ContextPrompt.BuildMessage`, and its layout can change; if you need structured data, read
`memory-mcp://profile` and `memory-mcp://spaces` instead, which return JSON DTOs on purpose.

## From a .NET MCP client

```csharp
var client = await McpClient.CreateAsync(transport); // transport carries the X-Api-Key header

var prompts = await client.ListPromptsAsync();       // contains "context"

var prompt = await client.GetPromptAsync("context");
var contextMessage = prompt.Messages
    .Select(m => m.Content)
    .OfType<TextContentBlock>()
    .First()
    .Text;
```

The runnable version is `Prompt_context_returns_a_ready_to_attach_message_for_the_active_space` in
[McpResourcesAndPromptsEndToEndTests.cs](../tests/MemoryMcp.Api.Tests/McpResourcesAndPromptsEndToEndTests.cs).

## From a host UI

Hosts that implement the Prompts primitive surface prompts as something the user picks explicitly —
in Claude Desktop, `context` appears among the server's prompts (typically via the attachment/slash
menu); in the [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector) it sits in the
"Prompts" tab, where you can fetch it and read the rendered message without writing JSON-RPC by hand.
Because it takes no arguments, there is no form to fill in — selecting it is the whole interaction.

Support for prompts is client-dependent. A client that ignores the primitive simply won't show
`context`; the tools, resources, and widgets are unaffected.

## When to use it (and when not to)

- **Use `context`** at the *start* of a conversation, to prime the model with who/what this space is
  about, without spending a tool call and without knowing what to search for yet.
- **Use `search_memory`** once there is an actual question — the prompt is a fixed recency window, not
  a retrieval mechanism, and it will not surface an old memory just because it is relevant.
- **Use `memory-mcp://profile`** when you want the same 5 memories as structured JSON (ids, versions,
  categories, attribution) rather than as bullets.
- **Use `listSpaces`** when you need the full set of accessible spaces with access levels and counts —
  the prompt deliberately shows at most 3, names only, as orientation.

## Related

- [resources-and-prompt-usage.md](resources-and-prompt-usage.md) — the three JSON resources alongside
  this prompt, with the same protocol-level detail.
- [mcp-apps-widgets-usage.md](mcp-apps-widgets-usage.md) — the widgets, including `select-space`,
  which is how a user changes the active space this prompt reports on.
- [multi-user-spaces.md](multi-user-spaces.md) — grants, roles, and how the active space is resolved.
