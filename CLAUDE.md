## Purpose
Create a memory system for AI agents that allows information to be stored via a remote MCP (http or https) to streamline and optimize the use of context memory.

The MCP, which we will call Memory-MCP, should allow memory storage and recall, as well as memory search for information or deletion of information that is no longer useful or necessary.

## How spaces work

A **space** keeps a team’s documents, memories, and profile context focused, so AI retrieves the right knowledge without mixing unrelated work. Use separate spaces for an engineering launch, research project, finance workflow, legal matter, medical knowledge base, or any other shared initiative. Teammates collaborate within the spaces they are allowed to read or write. Name a space to use it for one request without changing your active space. Otherwise, Supermemory uses your active space or account default. Ask to switch spaces when you want future requests to use a different active space.

**Each user or team must have an API KEY and each API KEY is enabled to one or more spaces**

## Tools

Your assistant chooses these tools automatically. Use this table when you need the exact inputs and results.

| Tool | Use it for | Inputs | Result |
| --- | --- | --- | --- |
| `search_memory` | Semantic, keyword, or category recall from one space, with optional profile context | `query`, `keyword`, `category` (at least one required), `includeProfile`, `containerTag` | Profile context and matching memories |
| `add_memory` | Save information or forget outdated information | `content` (required), `action` (`save` or `forget`), `category`, `containerTag` | Save or forget confirmation |
| `listDocuments` | Browse stored source documents and their summaries | `page`, `limit`, `containerTag` | Document IDs, titles, types, status, dates, and summaries |
| `getDocument` | Read the available content of one document | `documentId` (required) | Document metadata, summary, and available content |
| `listMemories` | Browse recent extracted memory entries and their source document IDs | `page`, `limit`, `containerTag` | Memory IDs, text, versions, and source document IDs |
| `listSpaces` | List accessible spaces and resolve a space name to its key | None | Formatted list plus structured `spaces` and `count` fields |
| `whoAmI` | Inspect the authenticated account, permissions, scope, and active space | None | Account and access context |

### Search

`search_memory` accepts a natural-language `query` for semantic recall, a literal `keyword` for exact/substring text matching, and/or a `category` to filter to memories tagged with it — at least one of the three must be given, and they can be combined (e.g. a `keyword` restricted to a `category`). By default, it also includes stable and recent profile context from the same space. Set `includeProfile` to `false` when only matching memories are needed. Use the retrieval tools for different questions:

-   Use `search_memory` to answer a question from remembered context.

-   Use `listDocuments` to discover stored sources, then `getDocument` to read one source in full.
-   Use `listMemories` to inspect the extracted memory entries themselves, including their IDs and source document IDs.

`listDocuments` and `listMemories` default to 10 results per page and accept up to 50.

### Save or forget

`add_memory` saves the supplied `content` by default. Set `action` to `forget` when a fact is outdated or should be removed. There is no separate forget tool. An optional `category` tags a saved memory so it can be filtered later via `search_memory`. If the content is already final, the assistant should use `add_memory`. If you want to review, edit, or choose a space before saving, it should open the `guided-save` widget instead.

### Access control

`whoAmI` returns the current identity, role, access type, granted scope, and active space. `listSpaces` returns only spaces the authenticated account can access, with names, keys, document and memory counts, and recent activity.

## Interactive Widgets

Clients that support [MCP Apps](https://modelcontextprotocol.io/extensions/apps/overview) can open these Memory-MCP widgets directly inside the conversation.

| Tool | What it opens |
| --- | --- |
| `select-space` | A searchable space picker |
| `guided-save` | An editable memory form with a space selector |
| `upload-file` | A local file picker and upload form |
| `memory-graph` | An interactive graph of documents and memories |

### Select a space

`select-space` opens a searchable space picker and changes the active space for future Memory-MCP actions. A one-off request in another space does not change the active space.

### Guided save

`guided-save` opens a draft with editable memory content and a writable-space selector. The assistant can prefill the draft, but nothing is saved until you submit it. 

### File upload

`upload-file` opens a local file picker and uploads one file at a time to a writable space. Supported types include text, Markdown, PDF, Word, CSV, common images, MP3, WAV, M4A, MP4, and WebM.

### Memory graph

`memory-graph` renders the selected space as an interactive graph of source documents and extracted memories. When no space is named, the server automatically uses the active space or account default. If you name a specific space, the assistant calls `listSpaces` to resolve its name to a space key, then opens that space’s graph.

## Resources and context prompt

Some MCP clients also expose resources and prompts:

| Kind | Name or URI | What it returns |
| --- | --- | --- |
| Resource | `memory-mcp://profile` | Stable and recent profile context for the active space |
| Resource | `memory-mcp://spaces` | A compact list of accessible spaces with the active space marked |
| Resource | `memory-mcp://memories` | The most recently created memories (any status) in the active space |
| Prompt | `context` | A ready-to-attach context message for the active space, plus up to three recently active spaces |

The `context` prompt takes no arguments. It returns profile context for the active space and up to three recently active spaces.