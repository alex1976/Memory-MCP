---
name: memory-mcp
description: 'Use this skill when the "Memory-MCP" MCP server is available (tools search_memory, add_memory, listMemories, listDocuments, getDocument, listSpaces, whoAmI, setActiveSpace, create_document, and the select_space_ui/guided_save_ui/upload_file_ui/memory_graph_ui widgets) and the user asks to remember, save, recall, search, or forget information across different conversations, upload a file, or manage memory spaces/categories.'
---

# Memory-MCP — usage guide for the agent

Memory-MCP is a remote MCP server that gives an AI agent persistent memory across conversations,
organized into **spaces** (multi-tenant) and protected by an API Key. This skill describes how an agent
should use its tools — including the 4 interactive **MCP Apps widgets** — to save, retrieve, search,
and remove memories effectively, without wasting context and without duplicating information that's
already known.

## Available tools

| Tool | Access | Parameters | When to use it |
| --- | --- | --- | --- |
| `whoAmI` | — | none | At the start of a conversation if you don't know which space is active or what permissions you have |
| `listSpaces` | — | none | To discover all spaces accessible with this API Key before saving/searching in a specific one |
| `setActiveSpace` | — | `spaceKey` (required) | To switch the active space yourself, when you already know the exact target key (e.g. from `listSpaces`) and don't need to show the user a picker |
| `search_memory` | Read | `query`, `keyword`, `category` (at least one), `includeProfile` (default `true`), `containerTag` | To retrieve existing memories before answering or before saving a new one |
| `add_memory` | ReadWrite | `content` (required), `action` (`save`/`forget`, default `save`), `category` (only on `save`), `containerTag` | To save a new fact or remove an outdated/incorrect one |
| `listMemories` | Read | `page`, `limit` (max 50), `containerTag` | To browse all extracted memories in a space (not for a targeted search) |
| `listDocuments` | Read | `page`, `limit` (max 50), `containerTag` | To discover the source documents available in a space |
| `getDocument` | Read | `documentId` (required) | To read the full content of a document you've already identified |
| `create_document` | ReadWrite | `title`, `docType` (required, `"text"`/`"markdown"`/`"csv"`/`"pdf"`), `content` (required — base64 PDF bytes when `docType` is `"pdf"`, plain text otherwise), `summary`, `containerTag` | To store content you already have in hand as a document yourself, without the `upload-file` widget's local file picker. Doesn't run fact extraction — call `add_memory` separately if the content should also become searchable memories |

If `containerTag` is omitted, all tools operate on the current API Key's "active" space
(`listSpaces`/`whoAmI` indicate which one that is).

## Search modes in `search_memory`

`search_memory` requires **at least one** of `query`, `keyword`, `category`; they can be combined.

- **`query`** — semantic search (cosine similarity over embeddings). Use it for natural-language
  questions or when you don't know the exact wording used in the original memory (e.g. `"code
  formatting preferences"`).
- **`keyword`** — case-insensitive literal match against the memory's text, no embedding is generated.
  Use it when you need to find an exact term, a proper name, an identifier (e.g. `"INGEST-142"`).
- **`category`** — filters by the label assigned in `add_memory`. Use it alone to list all memories of a
  given kind (e.g. all those with `category: "user-preferences"`), or together with
  `query`/`keyword` to narrow the search scope.
- **`includeProfile`** — if `true` (default), the response also includes the space's stable/recent
  profile: useful for getting a general sense of context even when direct matches are few. Set it to
  `false` when you only need the targeted search result.

Top matches may also include a `relatedMemories` list (each with `text`, `relationType` —
`Updates`/`Extends`/`Derives` — and `hops`): these are memories linked to the match in the memory
graph (see below). Use them to get fuller context (e.g. the detail a match `Extends`, or the fact it
`Updates`) without an extra `search_memory` call.

## Graph memory

When saving, `add_memory` may split `content` into more than one atomic fact and link each fact to
similar existing memories it `Updates`, `Extends`, or `Derives`. When a new fact `Updates` an older
one, that older memory is automatically marked inactive (superseded) — you don't need a separate
`action: "forget"` call for a fact that's a direct, self-contained correction of something already
saved; just save the new fact and let the server relate it. Still use `action: "forget"` explicitly
when you want to remove a memory without replacing it with new content, or when you're not confident
the new content clearly supersedes a specific existing memory.

## MCP Apps widgets — when to open a UI instead of calling a tool directly

Memory-MCP also exposes 4 interactive **widgets**, each opened by calling a dedicated tool that takes
no meaningful arguments and returns only a short confirmation string (e.g. `"Showing the guided save
form."`) — the real effect is a `_meta.ui.resourceUri` on the result that an MCP Apps-capable host (VS
Code, Claude Desktop with Apps support, etc.) resolves into a rendered iframe. **Don't try to parse the
opener tool's result for data** — it never contains memories/spaces/graph data, only a confirmation
that the UI was requested.

| Opener tool | Shows | Activate it when |
| --- | --- | --- |
| `select_space_ui` | A clickable list of accessible spaces (switches active space via `setActiveSpace`) | The user wants to browse/choose a space interactively, or you're unsure which space they mean and want them to pick |
| `guided_save_ui` | A content/category/space form that calls `add_memory` on submit | The user wants to review or edit content before it's saved, or explicitly asks for a form/UI to save something |
| `upload_file_ui` | A local file picker (text/Markdown/CSV/PDF) that calls `create_document`, then optionally `add_memory` | **Always** — this is the only way to read a file from the user's machine; you have no file-system access yourself, so defer any "upload/import this file" request to this widget |
| `memory_graph_ui` | An interactive, draggable visualization of the active space's memory graph | The user wants to visually explore how memories relate to each other, not just get a textual answer |

For everything else — routine autonomous saves, searches, or space switches where you already know
the target — call the underlying tool directly (`add_memory`, `search_memory`, `setActiveSpace`,
`create_document`) instead of opening a widget: it's faster and doesn't require an Apps-capable host.
If the user reports that nothing appeared after you opened a widget, their client likely doesn't
support MCP Apps yet — fall back to the equivalent direct tool call instead of repeating the widget
call. Widgets and their backing tools share the exact same effects (a `guided-save` submission is a
normal `add_memory` call), so switching between the two mid-conversation never causes duplicated or
inconsistent state.

## Recommended workflow

1. **At the start of a conversation** where persistent memory is relevant, consider calling
   `whoAmI`/`listSpaces` to understand the active space and your permissions, especially if the user
   works across multiple projects/spaces.
2. **Before saving something new**, always run a `search_memory` (with `query` and/or `category`)
   to check that the information isn't already present or in conflict with an existing memory. If you
   find an outdated fact that contradicts it, you can usually just save the new fact — graph memory
   extraction (when configured on the server) will detect the contradiction and mark the old memory
   superseded on its own. Use `add_memory` with `action: "forget"` when you want the old memory gone
   without a specific new fact replacing it, or when you can't rely on extraction being configured.
3. **When saving** (`add_memory`, `action: "save"`):
   - Write `content` as an atomic, self-contained fact (a sentence or a few lines), not an entire
     transcript or an unfiltered block of text.
   - Assign a consistent `category` when the information belongs to a reusable grouping
     (e.g. `user-preferences`, `project-x`, `config-credentials`, `contacts`) so it can be retrieved
     by category later. If no natural category emerges, you can omit the parameter.
   - Don't save secrets, plaintext credentials, or sensitive data unless the user explicitly requests
     it and the space is the right one for that kind of data.
4. **When retrieving** (`search_memory`), pick the mode that best fits (see above) instead of always
   using semantic search: a `keyword` or a `category` are more precise and cheaper when the
   term or grouping is already known.
5. **When a piece of information is outdated or wrong and you have no replacement fact to save**, use
   `add_memory` with `action: "forget"`, passing in `content` text that describes/recalls the memory to
   remove (matching is by similarity, not by ID) — don't let contradictory versions of the same
   information coexist. If you do have a replacement fact, prefer saving it (see step 2).
6. **To explore what's been saved** without a precise query, use `listMemories`/`listDocuments`
   (paginated) instead of `search_memory`, which is meant for targeted retrieval.
7. **When the user asks to upload/import a local file**, open `upload_file_ui` rather than asking them
   to paste the content — you can't read their filesystem yourself. For any other interactive request
   ("let me pick a space", "show me the memory graph", "give me a form to save this"), open the
   matching widget from the table above instead of approximating the interaction in text.

## Error handling

Authorization or space-not-found errors arrive as a tool result with `isError: true` and a
readable message (e.g. non-existent space, insufficient permissions, none of `query`/`keyword`/
`category` provided to `search_memory`). Report the message to the user instead of blindly retrying or
inventing a silent fallback.
