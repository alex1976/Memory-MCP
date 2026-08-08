# Memory-MCP

Remote MCP (Model Context Protocol) server for storing, retrieving, and semantically searching
"memories" on behalf of AI agents, organized into multi-tenant **spaces** and protected by API Key.

Full functional specification: [CLAUDE.md](CLAUDE.md).

## Table of contents

- [Architecture](#architecture)
- [Technology](#technology)
- [Data model](#data-model)
- [Available MCP tools](#available-mcp-tools)
- [Project phases](#project-phases)
- [Setup and startup](#setup-and-startup)
- [Tests](#tests)
- [Docker](#docker)

## Architecture

4-layer Clean Architecture, with dependencies flowing in a single direction (`Api` → `Infrastructure`/`Application` → `Domain`):

```
Memory-MCP/
├── src/
│   ├── MemoryMcp.Domain/          # Pure entities (Space, ApiKey, ApiKeySpaceGrant, Document, Memory)
│   │                              # No dependency on EF/HTTP/external libraries.
│   ├── MemoryMcp.Application/     # Use cases: interfaces (IMemoryService, IDocumentService,
│   │                              # ISpaceService, IEmbeddingProvider, repositories, ICurrentAccessContext)
│   │                              # + application service implementations + DTOs.
│   ├── MemoryMcp.Infrastructure/   # EF Core (MemoryDbContext, migrations, repositories), embedding
│   │                              # provider (OpenAI/Azure OpenAI).
│   └── MemoryMcp.Api/             # ASP.NET Core host: MCP hosting, API Key authentication,
│                                  # the 7 MCP tools (thin adapters with no business logic).
└── tests/
    ├── MemoryMcp.Application.Tests/    # Unit tests for the application services (mock/NSubstitute)
    ├── MemoryMcp.Infrastructure.Tests/ # Integration tests for the repositories against a real Postgres
    └── MemoryMcp.Api.Tests/            # End-to-end tests for the 7 tools via an MCP client + WebApplicationFactory
```

**Why Clean Architecture and not Vertical Slice**: all tools share the same data model
(Space/ApiKey/Memory/Document) and the same per-space authorization rules; isolating EF persistence
and the embedding provider behind interfaces in `Application` allows the project to be extended (resources,
prompts, MCP Apps widgets — Phase 2) without touching `Domain`/`Application`.

Every class in `Api/Tools` is a **thin adapter**: it resolves the access context (`ICurrentAccessContext`),
calls the application service, and formats the output — no business logic in the Api layer.

### Authentication and multi-tenancy

- Every **API Key** is associated with one or more **spaces**, with an access level (`Read` or `ReadWrite`) per
  space, and one space marked as "active" (`IsDefault`).
- `ApiKeyAuthenticationHandler` (`src/MemoryMcp.Api/Auth`) reads the key from the `X-Api-Key` header (or
  `Authorization: Bearer <key>`), validates it against the database (SHA-256 hash, never the plaintext key), and
  populates `CurrentAccessContext`, a scoped service injected into the application services.
- The tools' `containerTag` parameter corresponds to the `spaces.key` column; if omitted, the current
  key's "active" space is used.
- Authorization/space-not-found errors are translated into MCP tool results with `isError=true`
  (never unhandled exceptions or 500s).

### Semantic search

`IEmbeddingProvider` is pluggable (OpenAI or Azure OpenAI, the same `EmbeddingClient` under both
implementations). Embeddings are stored as a native Postgres `real[]` column (via Npgsql), and cosine
similarity is computed **in-app** in `MemoryRepository.SearchAsync`.

Besides semantic search, `search_memory` also supports literal keyword search
(`keyword`, case-insensitive match via `ILIKE` in `MemoryRepository.SearchByKeywordAsync`, without generating
an embedding) and filtering/listing by category (`category`, an optional column on `memories`, assignable in
`add_memory`). The three criteria can be combined: `query`/`keyword` can be further restricted by
`category`; if only `category` is given, `MemoryRepository.ListByCategoryAsync` lists the memories in that
category ordered by creation date. At least one of `query`, `keyword`, and `category` must be provided.

> Note: the original specification called for PostgreSQL + the `pgvector` extension with an HNSW index. In this
> environment Docker Desktop is blocked by company policy and the available local Postgres does not have
> `pgvector` installed (nor can it be installed without local admin permissions), so the
> search was implemented without the extension. It works correctly but doesn't scale as well as a native
> vector index on very large volumes — if `pgvector` becomes available in the future, migrating to
> an HNSW index requires revisiting `MemoryConfiguration` and `MemoryRepository.SearchAsync`.

## Technology

| Layer | Technology |
| --- | --- |
| Runtime | .NET 10 (pinned in [global.json](global.json)) |
| MCP Server | `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` 2.1.0 |
| Web host | ASP.NET Core Minimal API |
| Persistence | PostgreSQL + EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Embedding | `OpenAI` / `Azure.AI.OpenAI` (same `EmbeddingClient`, selected via configuration) |
| Authentication | Custom `AuthenticationHandler<T>` scheme based on API Key (SHA-256 hash) |
| Tests | xUnit, NSubstitute, AwesomeAssertions (MIT fork of FluentAssertions), `Microsoft.AspNetCore.Mvc.Testing` |
| Container | Multi-stage Dockerfile + docker-compose (for environments where Docker is available) |

## Data model

| Table | Description |
| --- | --- |
| `spaces` | Logical space (`key` unique = `containerTag`, `name`, `description`) |
| `api_keys` | API key (only the hash is stored, never the plaintext value) |
| `api_key_space_grants` | `Read`/`ReadWrite` permission of an API Key on a space + "active space" flag |
| `documents` | Source document (title, type, status, summary, raw content) |
| `memories` | Extracted memory (text, optional category, `real[]` embedding, version, `is_active` for soft-delete/"forget") |

## Available MCP tools

All 7 tools required by the specification are implemented in `src/MemoryMcp.Api/Tools`:

| Tool | File | Access required | Description |
| --- | --- | --- | --- |
| `search_memory` | `MemoryTools.cs` | Read | Searches a space's memories by semantic similarity (`query`), literal keyword (`keyword`), and/or category (`category`), with optional profile |
| `add_memory` | `MemoryTools.cs` | ReadWrite | Saves (`action=save`, with optional `category`) or removes (`action=forget`) a memory |
| `listDocuments` | `DocumentTools.cs` | Read | Paginated list of a space's source documents |
| `getDocument` | `DocumentTools.cs` | Read | Metadata and content of a document |
| `listMemories` | `MemoryTools.cs` | Read | Paginated list of extracted memories |
| `listSpaces` | `AccessTools.cs` | — | Spaces accessible with the current API Key, with counts |
| `whoAmI` | `AccessTools.cs` | — | Current identity, accessible spaces, active space |

## Project phases

### Phase 1 — Completed

- [x] Domain entities and the relational data model (Space, ApiKey, ApiKeySpaceGrant, Document, Memory)
- [x] EF Core persistence + initial migration
- [x] API Key authentication with per-space permissions
- [x] Pluggable `IEmbeddingProvider` (OpenAI / Azure OpenAI)
- [x] The 7 core MCP tools, exposed via `ModelContextProtocol.AspNetCore` on the `/mcp` HTTP endpoint
- [x] Test suite (unit, integration, end-to-end)

### Phase 2 — Not implemented (to avoid blocking future extensibility)

- [ ] MCP Resources: `memory-mcp://profile`, `memory-mcp://spaces`
- [ ] MCP Prompt: `context`
- [ ] Interactive MCP Apps widgets: `select-space`, `guided-save`, `upload-file`, `memory-graph`
      (require the `ModelContextProtocol.Extensions.Apps` package and iframe-based UI)
- [ ] Reintroducing `Qdrant` with a native HNSW index as the vector DB

These items are added as new classes (`[McpServerResourceType]`, `[McpServerPromptType]`) in the
`Api` project, reusing the existing `Application` services — without changes to `Domain`/`Application`.

## Setup and startup

### Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (version pinned in [global.json](global.json))
- A reachable PostgreSQL instance (local or remote). **The `pgvector` extension is not required.**
- (Optional) an OpenAI or Azure OpenAI API Key, needed only for the `add_memory` and `search_memory` tools

### 1. Configure the connection string

Create/update `src/MemoryMcp.Api/appsettings.Development.json` (file **not versioned**, see
[.gitignore](.gitignore)) with your Postgres connection string:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Username=<user>;Password=<password>;Database=<database>"
  }
}
```

Alternatively, you can set the `ConnectionStrings__Default` environment variable without touching the
configuration files.

### 2. Apply migrations

```bash
dotnet tool restore # if you don't already have dotnet-ef installed: dotnet tool install --global dotnet-ef
dotnet ef database update --project src/MemoryMcp.Infrastructure --startup-project src/MemoryMcp.Api
```

### 3. (Optional) Configure the embedding provider

To use `add_memory`/`search_memory`, in `appsettings.Development.json`:

```json
{
  "Embeddings": {
    "Provider": "OpenAI",
    "ApiKey": "sk-...",
    "Model": "text-embedding-3-small"
  }
}
```

For Azure OpenAI: `"Provider": "AzureOpenAI"`, `"Endpoint": "https://<resource>.openai.azure.com"`,
`"Model"` = deployment name. Without these settings the server still starts and all other
tools work normally: only `add_memory`/`search_memory` will return a tool error.

### 4. Create a test space and API Key

There isn't an administration API yet (out of scope for Phase 1): a command-line command creates a
"default" space and an API Key with `ReadWrite` access, printing the plaintext key once:

```bash
dotnet run --project src/MemoryMcp.Api -- --seed
# Seeded space 'default' with API key: mmcp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

### 5. Start the server

```bash
dotnet run --project src/MemoryMcp.Api
```

The server exposes:
- `GET /health` — health check
- `POST /mcp` — MCP endpoint (Streamable HTTP), protected: requires the `X-Api-Key: <key>` header (or
  `Authorization: Bearer <key>`)

Default development port: `http://localhost:5004` (see
`src/MemoryMcp.Api/Properties/launchSettings.json`).

You can connect an MCP client (e.g. [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector))
to the `http://localhost:5004/mcp` endpoint, passing the `X-Api-Key` header with the key generated in step 4.

### 6. (Optional) Connect Claude Desktop

A sample file is available at [claude_desktop_config.example.json](claude_desktop_config.example.json):

```json
{
  "mcpServers": {
    "memory-mcp": {
      "url": "http://localhost:5004/mcp",
      "headers": {
        "X-Api-Key": "mmcp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
      }
    }
  }
}
```

Copy the content (replacing the key with the one generated in step 4) into Claude Desktop's actual
configuration file, then restart the application:

- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

If an `mcpServers` section already exists with other servers, simply add the `memory-mcp` entry without
overwriting the others. On restart, Memory-MCP's 7 tools will be available in the conversation.

## Tests

```bash
# Unit tests (no external dependencies)
dotnet test tests/MemoryMcp.Application.Tests/MemoryMcp.Application.Tests.csproj

# Integration and end-to-end tests: require a reachable real Postgres
export MEMORYMCP_TEST_CONNECTION_STRING="Host=localhost;Port=5432;Username=<user>;Password=<password>;Database=<database>"
dotnet test
```

> The integration/E2E tests connect directly to the Postgres indicated by
> `MEMORYMCP_TEST_CONNECTION_STRING` (no Testcontainers/Docker, for consistency with the company
> environment). They apply migrations automatically and every test uses keys/spaces with random GUIDs, so it's
> safe to point them at the development database too.

## Docker

`Dockerfile` and `docker-compose.yml` are ready for environments where Docker is available (e.g. CI/CD or
deployment), but **have not been verified in this development environment** (Docker Desktop blocked by
company policy):

```bash
docker compose up --build
```

Starts a standard Postgres (`postgres:17`, without `pgvector`) and the `api` service, exposed on
`http://localhost:8080`.
