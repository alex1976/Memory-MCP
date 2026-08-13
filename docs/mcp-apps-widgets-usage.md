# Using the Phase 3 MCP Apps widgets

Phase 3 (see [README.md](../README.md#mcp-apps-widgets)) also adds 4 interactive **MCP Apps**
widgets, via the experimental [`ModelContextProtocol.Extensions.Apps`](https://www.nuget.org/packages/ModelContextProtocol.Extensions.Apps)
package. Unlike the tools/resources/prompt covered in
[resources-and-prompt-usage.md](resources-and-prompt-usage.md), a widget isn't something you call and
get a value back from — it's an HTML page that an **MCP Apps-capable host** (e.g. Claude Desktop or VS
Code with Apps support) renders inside a sandboxed iframe in the conversation, which then drives real
tools/resources on your behalf through ordinary MCP requests. This doc explains the moving pieces, at
the protocol level, and walks through each of the four widgets.

| Widget | Opener tool | UI resource | Calls |
| --- | --- | --- | --- |
| select-space | `select_space_ui` | `ui://select-space` | reads `memory-mcp://spaces`, calls `setActiveSpace` |
| guided-save | `guided_save_ui` | `ui://guided-save` | reads `memory-mcp://spaces`, calls `add_memory` |
| upload-file | `upload_file_ui` | `ui://upload-file` | reads `memory-mcp://spaces`, calls `create_document` (PDF: + `getDocument`) then optionally `add_memory` |
| memory-graph | `memory_graph_ui` | `ui://memory-graph` | reads `memory-mcp://graph` |

None of the widgets contain their own business logic — every one of them is a thin HTML/JS shell
(`src/MemoryMcp.Api/Apps/ui/*.html`) that reuses the same tools/resources described in the README and
in [resources-and-prompt-usage.md](resources-and-prompt-usage.md). If you already know how to call
`add_memory`, `setActiveSpace`, `create_document`, or read `memory-mcp://spaces`/`memory-mcp://graph`
directly, you already know everything a widget does under the hood.

## How a host renders a widget

1. The model (or you, explicitly) calls one of the four opener tools, e.g. `guided_save_ui`. The tool
   itself just returns a short confirmation string — the interesting part is its `_meta.ui` field:

   ```json
   // → server
   {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "guided_save_ui", "arguments": {}}}

   // ← server
   {
     "jsonrpc": "2.0",
     "id": 1,
     "result": {
       "content": [{"type": "text", "text": "Showing the guided save form."}],
       "_meta": {"ui": {"resourceUri": "ui://guided-save"}}
     }
   }
   ```

2. An Apps-capable host sees `_meta.ui.resourceUri`, resolves it via `resources/read`, and renders the
   returned HTML in a sandboxed iframe:

   ```json
   // → server
   {"jsonrpc": "2.0", "id": 2, "method": "resources/read", "params": {"uri": "ui://guided-save"}}

   // ← server
   {
     "jsonrpc": "2.0",
     "id": 2,
     "result": {
       "contents": [
         {"uri": "ui://guided-save", "mimeType": "text/html;profile=mcp-app", "text": "<!DOCTYPE html>...<script>...</script></html>"}
       ]
     }
   }
   ```

3. Inside the iframe, the widget's script opens a `window.postMessage` JSON-RPC 2.0 bridge back to the
   host and performs a `ui/initialize` handshake, then issues ordinary `tools/call`/`resources/read`
   requests exactly like any other MCP client — the host simply forwards them over the real session:

   ```js
   // sent by the iframe via window.parent.postMessage
   {"jsonrpc": "2.0", "id": 1, "method": "ui/initialize",
    "params": {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "guided-save", "version": "1.0.0"}}}
   {"jsonrpc": "2.0", "method": "ui/notifications/initialized", "params": {}}

   // later, when the user clicks "Save memory"
   {"jsonrpc": "2.0", "id": 4, "method": "tools/call",
    "params": {"name": "add_memory", "arguments": {"content": "Alex is a PM at Stripe", "action": "save"}}}
   ```

A client that doesn't support MCP Apps just sees four extra tools and four `text/html;profile=mcp-app`
resources it doesn't know how to render — nothing else in the server changes, and every other tool
keeps working exactly as before (same "strictly additive" approach as Phase 2's graph memory).

## select-space

Opens a list of the current API key's accessible spaces (from `memory-mcp://spaces`) and lets you click
one to make it active, via `setActiveSpace`:

```json
// → server
{"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "setActiveSpace", "arguments": {"spaceKey": "research"}}}

// ← server (same shape as listSpaces / memory-mcp://spaces — SpaceSummaryDto[])
{
  "jsonrpc": "2.0",
  "id": 5,
  "result": {
    "content": [{
      "type": "text",
      "text": "[{\"id\":\"...\",\"key\":\"research\",\"name\":\"Research\",\"accessLevel\":\"Read\",\"isDefault\":true,\"documentCount\":2,\"memoryCount\":5},{\"id\":\"...\",\"key\":\"default\",\"name\":\"Default Space\",\"accessLevel\":\"ReadWrite\",\"isDefault\":false,\"documentCount\":4,\"memoryCount\":9}]"
    }]
  }
}
```

`setActiveSpace` can only switch between spaces the key **already** has a grant for (it resolves the
target through the same `ICurrentAccessContext.ResolveGrant` every other tool uses) — it cannot be used
to gain access to a new space. After the switch, every tool/resource that defaults to "the active
space" (i.e. every call that omits `containerTag`) now targets the new one, for the lifetime of that
API key's default grant (not just the current conversation).

## guided-save

A content/category/space form over `add_memory`. Selecting a space in the dropdown sets `containerTag`
on the call; leaving it blank uses the active space, same as calling the tool directly:

```json
// → server
{"jsonrpc": "2.0", "id": 6, "method": "tools/call",
 "params": {"name": "add_memory", "arguments": {"content": "Invoice due the 5th", "category": "finance", "action": "save"}}}

// ← server (AddMemoryResult)
{
  "jsonrpc": "2.0",
  "id": 6,
  "result": {"content": [{"type": "text", "text": "{\"memoryId\":\"...\",\"action\":\"Save\",\"affectedCount\":1,\"message\":\"Memory saved.\",\"memoryIds\":[\"...\"]}"}]}
}
```

This is exactly the `add_memory` flow described in the README's "Graph memory" section — fact
extraction, embedding, and edge creation all happen the same way whether `add_memory` is called
directly or through this widget.

## upload-file

Reads the picked file client-side and stores it as a new document via `create_document`, then
optionally also calls `add_memory` so its text gets extracted into searchable memories. Text-like
files (`.txt`/`.md`/`.csv`) are read as plain text (`FileReader.readAsText`); PDFs are read as base64
(`FileReader.readAsDataURL`, stripping the `data:application/pdf;base64,` prefix) and extracted to text
**server-side** — the browser never parses the PDF itself.

Text-like file:

```json
// → server (from the widget, after picking "roadmap.md")
{"jsonrpc": "2.0", "id": 7, "method": "tools/call",
 "params": {"name": "create_document", "arguments": {"title": "roadmap.md", "docType": "markdown", "content": "# Q3 roadmap\n..."}}}

// ← server (DocumentSummaryDto)
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": {"content": [{"type": "text", "text": "{\"id\":\"...\",\"title\":\"roadmap.md\",\"docType\":\"markdown\",\"status\":\"Processed\",\"summary\":null,\"createdAt\":\"2026-08-13T10:02:00Z\",\"updatedAt\":\"2026-08-13T10:02:00Z\"}"}]}
}

// → server (only if "also extract memories" is checked — the widget already has the plain text)
{"jsonrpc": "2.0", "id": 8, "method": "tools/call",
 "params": {"name": "add_memory", "arguments": {"content": "# Q3 roadmap\n..."}}}
```

PDF: `content` is the base64 PDF bytes, `docType` is `"pdf"`. `create_document` decodes it, runs it
through `IPdfTextExtractor` (`PdfPig`, entirely in-process — no external API, no config needed) and
stores/returns the **extracted text**, not the binary:

```json
// → server (from the widget, after picking "report.pdf")
{"jsonrpc": "2.0", "id": 7, "method": "tools/call",
 "params": {"name": "create_document", "arguments": {"title": "report.pdf", "docType": "pdf", "content": "JVBERi0xLjQKJ...=="}}}

// ← server (DocumentSummaryDto — same shape as any other document)
{
  "jsonrpc": "2.0",
  "id": 7,
  "result": {"content": [{"type": "text", "text": "{\"id\":\"3fa8...\",\"title\":\"report.pdf\",\"docType\":\"pdf\",\"status\":\"Processed\",\"summary\":null,\"createdAt\":\"2026-08-13T10:02:00Z\",\"updatedAt\":\"2026-08-13T10:02:00Z\"}"}]}
}
```

The widget doesn't have the extracted plain text in hand the way it does for text-like files (only the
base64 it sent), so if "also extract memories" is checked it fetches it back first via `getDocument`,
then feeds that into `add_memory` — three tool calls total instead of two, only for PDF:

```json
// → server
{"jsonrpc": "2.0", "id": 8, "method": "tools/call", "params": {"name": "getDocument", "arguments": {"documentId": "3fa8..."}}}

// ← server (DocumentDetailDto — rawContent is the extracted text)
{
  "jsonrpc": "2.0",
  "id": 8,
  "result": {"content": [{"type": "text", "text": "{\"id\":\"3fa8...\",\"title\":\"report.pdf\",\"docType\":\"pdf\",\"status\":\"Processed\",\"summary\":null,\"rawContent\":\"Q3 Report\\n\\nRevenue grew 12%...\",\"createdAt\":\"2026-08-13T10:02:00Z\",\"updatedAt\":\"2026-08-13T10:02:00Z\"}"}]}
}

// → server
{"jsonrpc": "2.0", "id": 9, "method": "tools/call",
 "params": {"name": "add_memory", "arguments": {"content": "Q3 Report\n\nRevenue grew 12%..."}}}
```

If the PDF is corrupt, encrypted, or otherwise unreadable, `create_document` returns a normal tool
error (`isError: true`, message from `DocumentExtractionException`) — not an unhandled exception —
same as passing an unresolvable `containerTag` to any other tool.

In both cases, `create_document` **only stores the document** — it does not run fact extraction on its
own (that would either duplicate `add_memory`'s internal "note" document or require reworking its
well-tested internals; see the README's `upload-file` note). This is why the widget always issues a
separate `add_memory` call instead of folding extraction into `create_document`: the document is your
source-of-truth artifact, the memories are a separate, optional derived product of its text.

**Scope note:** Word, images, and MP3/WAV/M4A/MP4/WebM can still be picked in the file dialog, but the
widget disables the upload button and shows an inline explanation instead of guessing at binary
content — Word text extraction and image OCR/audio transcription are tracked as Phase 4 follow-ups,
not silently attempted.

## memory-graph

Read-only: fetches `memory-mcp://graph` and renders it, no tool calls involved.

```json
// → server
{"jsonrpc": "2.0", "id": 9, "method": "resources/read", "params": {"uri": "memory-mcp://graph"}}

// ← server (SpaceGraphDto)
{
  "jsonrpc": "2.0",
  "id": 9,
  "result": {
    "contents": [{
      "uri": "memory-mcp://graph",
      "mimeType": "application/json",
      "text": "{\"nodes\":[{\"id\":\"a1\",\"text\":\"Alex is a PM at Stripe\",\"category\":null,\"isActive\":false,\"createdAt\":\"2026-08-10T09:00:00Z\"},{\"id\":\"a2\",\"text\":\"Alex now leads a team of 5 at Stripe\",\"category\":null,\"isActive\":true,\"createdAt\":\"2026-08-12T09:14:02Z\"}],\"edges\":[{\"fromMemoryId\":\"a2\",\"toMemoryId\":\"a1\",\"relationType\":\"Extends\"}]}"
    }]
  }
}
```

The widget lays the nodes out with a small hand-rolled force-directed simulation (repulsion between
all pairs, spring attraction along edges, a few hundred ticks to settle — no charting library, no CDN
dependency), colors edges by `relationType` (`Updates`/`Extends`/`Derives`), dims nodes where
`isActive` is `false` (forgotten memories), and lets you drag nodes and hover for the full text. It
shows at most the 50 most recent memories in the space (any status) and only the edges between them —
the same root-agnostic, whole-space view described in the README, as opposed to `search_memory`'s
per-result `relatedMemories` neighborhood.

## From a .NET MCP client

Widgets are just tools + resources, so the same `McpClient` calls from
[resources-and-prompt-usage.md](resources-and-prompt-usage.md#from-a-net-mcp-client) apply — see
`tests/MemoryMcp.Api.Tests/McpAppsEndToEndTests.cs` for the full runnable versions:

```csharp
var client = await McpClient.CreateAsync(transport);

// Confirm the widgets are registered
var tools = await client.ListToolsAsync();
tools.Select(t => t.Name).Should().Contain(["select_space_ui", "guided_save_ui", "upload_file_ui", "memory_graph_ui"]);

var resources = await client.ListResourcesAsync();
resources.Should().Contain(r => r.Uri == "ui://memory-graph" && r.MimeType == "text/html;profile=mcp-app");

// Read a widget's HTML directly (useful for a smoke test even without an Apps-capable client)
var html = await client.ReadResourceAsync("ui://select-space");
var markup = html.Contents.OfType<TextResourceContents>().First().Text; // "<!DOCTYPE html>..."

// Drive the same action a widget would, without a UI
var switched = await client.CallToolAsync("setActiveSpace", new Dictionary<string, object?> { ["spaceKey"] = "research" });
```

## From Claude Desktop / MCP Inspector

Once connected (see the README's [Claude Desktop section](../README.md#7-optional-connect-claude-desktop)),
an MCP Apps-capable host will render the four widgets as interactive iframes when their opener tool is
invoked — e.g. asking for "@memory_graph_ui" or letting the model decide to open `guided_save_ui` while
saving something. Support for MCP Apps is host-dependent (it's a newer, `[Experimental]` extension to
the protocol); a host without it — including the plain [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector) —
still lists and can call the four opener tools and read the `ui://*` resources like any other
tool/resource (you'll just see the raw HTML text rather than a rendered iframe), which is enough to
verify the widgets are wired up correctly even without an Apps-capable client. Every other tool and
resource is unaffected either way.
