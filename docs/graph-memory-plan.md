# Phase 2: Graph Memory for Memory-MCP

## Context

Phase 1 delivered a flat memory store: `add_memory` embeds whole chunks of text and
`search_memory` does vector/keyword/category lookup with no relationships between memories
(`README.md`, `src/MemoryMcp.Application/Memories/MemoryService.cs`). The goal for Phase 2 is to
move Memory-MCP beyond plain RAG towards a **graph memory** model, as described by Supermemory's
docs (`how-it-works`, `graph-memory`): content is decomposed into atomic facts, and those facts
are linked by typed relations — **Updates** (a newer fact supersedes an older one, history kept
for audit), **Extends** (a fact adds detail to an existing one without invalidating it), and
**Derives** (a fact inferred from combining existing ones). Retrieval then returns not just the
top matches but their connected `relatedMemories` via graph traversal.

Constraints (confirmed unchanged from Phase 1, see `README.md`'s "Note" callout): Docker Desktop
is blocked by company policy and the local Postgres cannot have extensions installed (no admin
rights). This rules out Apache AGE, Neo4j/Memgraph as a server, and pgvector — the graph must be
built entirely from **already-available open-source components**: the existing PostgreSQL + EF
Core stack, modeled relationally, with traversal done in SQL (`WITH RECURSIVE`) rather than a
dedicated graph engine. Fact/relation extraction reuses the existing OpenAI-compatible client
pattern (`IEmbeddingProvider`) so it works unmodified against a self-hosted open-source model
(Ollama, vLLM, LM Studio — all expose an OpenAI-compatible endpoint) by config alone, with no code
change required to stay 100% open-source end-to-end.

The existing `Memory.SupersededBy`/`IsActive`/`Version` fields already encode a one-directional
"Updates" pointer — this plan generalizes that into a typed edge table without removing or
breaking the existing fields/behavior (extend, don't replace).

## Architecture additions

```
Domain:        MemoryEdge (Id, FromMemoryId, ToMemoryId, SpaceId, RelationType, Note?, CreatedAt)
               RelationType enum { Updates, Extends, Derives }

Application:   IMemoryEdgeRepository   (Abstractions) — persistence for edges
               IMemoryGraphService      — traversal use case, called by MemoryService
               IFactExtractor           (Abstractions) — mirrors IEmbeddingProvider
               MemoryDtos: MemorySearchResultDto gains RelatedMemories (additive, non-breaking)
               MemoryService: SaveAsync now extracts facts + links edges; SearchMemoryAsync
                              attaches related memories to top matches

Infrastructure: MemoryEdgeConfiguration + migration `AddMemoryEdges`
               MemoryEdgeRepository — recursive-CTE traversal via EF Core's Database.SqlQuery<T>
               LlmFactExtractor + ExtractionOptions — same OpenAI/Azure OpenAI SDK already
                              referenced, lazy ChatClient like CreateEmbeddingClient
```

No new NuGet packages, no new external services, no schema outside Postgres.

## Domain model

`src/MemoryMcp.Domain/MemoryEdge.cs` (new):
- `Id`, `SpaceId` (denormalized, indexed — mirrors `Memory.SpaceId`'s own indexing rationale),
  `FromMemoryId`, `ToMemoryId`, `RelationType`, `Note` (nullable, e.g. why the extractor linked
  them), `CreatedAt`.
- Direction convention: `From` **acts on** `To` — e.g. `Updates` means `From` supersedes `To`.
- No `Guid?` FK nullability tricks needed; edges are immutable once created (no "forget an edge"
  operation in this phase — forgetting a *memory* via `Memory.Forget()` already exists and is
  reused, see below).

`src/MemoryMcp.Domain/RelationType.cs` (new): `enum RelationType { Updates, Extends, Derives }`.

`Memory.SupersededBy`/`IsActive`/`Version`/`Forget()` are **not changed**. When a save creates an
`Updates` relation, `MemoryService` will call the existing `Memory.Forget(supersededBy: newId)` on
the old memory (exactly as `ForgetAsync` does today) *and* insert a `MemoryEdge(From: newId, To:
oldId, RelationType.Updates)`. The edge table becomes the canonical, generalized graph;
`SupersededBy` remains a convenience pointer for the single most common case, now always kept in
sync with an equivalent edge.

## Fact extraction ("dreaming" equivalent)

`src/MemoryMcp.Application/Abstractions/IFactExtractor.cs` (new), same shape family as
`IEmbeddingProvider.cs`:

```csharp
public interface IFactExtractor
{
    Task<IReadOnlyList<ExtractedFact>> ExtractAsync(
        string content,
        IReadOnlyList<MemoryCandidateDto> relatedCandidates,
        CancellationToken cancellationToken = default);
}

public sealed record ExtractedFact(
    string Text,
    string? Category,
    IReadOnlyList<ExtractedRelation> RelationsToExisting);

public sealed record ExtractedRelation(Guid ExistingMemoryId, RelationType RelationType);
```

`relatedCandidates` are the top-K semantically similar *active* memories in the space, fetched via
the existing `IMemoryRepository.SearchAsync` (already used in `MemoryService.ForgetAsync`) — no
new retrieval primitive needed there.

`src/MemoryMcp.Infrastructure/Extraction/LlmFactExtractor.cs` (new):
- Uses the OpenAI SDK's chat client (already a project dependency via `OpenAI`/`Azure.AI.OpenAI`,
  used today only for embeddings) with **structured output** (JSON schema mode) to get back
  `{ facts: [{ text, category, relations: [{ existingMemoryId, relationType }] }] }` reliably,
  avoiding brittle text parsing.
- Prompt instructs: split `content` into atomic, self-contained statements; for each, compare
  against the supplied candidates and classify `Updates` (contradicts/replaces), `Extends` (adds
  detail, doesn't invalidate), or omit the relation if unrelated. `Derives` (inferring a new fact
  from *combining* two unrelated existing memories) is explicitly best-effort/optional in the
  prompt — Supermemory's hardest case, and not required for correctness of this phase.
- `ExtractionOptions` (new, mirrors `EmbeddingOptions.cs`): `Provider`, `Endpoint`, `ApiKey`,
  `Model`. Wired in `MemoryMcp.Infrastructure/DependencyInjection.cs` with the same lazy-client
  pattern as `CreateEmbeddingClient` — constructing the client only when extraction is actually
  invoked, so the server still starts and non-extraction tools keep working when unconfigured.
- **Fallback when unconfigured**: `MemoryService.SaveAsync` catches "extractor not configured"
  (same signal `IEmbeddingProvider` already surfaces for missing config) and falls back to
  today's exact behavior — save the whole content as one memory, zero edges. This keeps the
  feature strictly additive: existing tests and callers who never configure extraction see no
  behavior change.

Because `LlmFactExtractor` talks to the chat client via the OpenAI-compatible protocol, pointing
`Extraction:Endpoint` at a self-hosted OSS model (Ollama/vLLM/LM Studio's OpenAI-compatible API)
requires **zero code changes** — only configuration — which satisfies "open source components
only" without inventing a second extractor implementation.

## Graph storage & traversal (no external graph DB)

`src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryEdgeConfiguration.cs` (new):
- Table `memory_edges`. Indexes on `(SpaceId, FromMemoryId)` and `(SpaceId, ToMemoryId)` for cheap
  neighbor lookups in either direction. FKs to `memories` with `DeleteBehavior.Cascade` (matches
  `MemoryConfiguration`'s existing FK style).

`src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryEdgeRepository.cs` (new), implementing
`IMemoryEdgeRepository`:
- `Add(MemoryEdge edge)` — plain EF `Add`, same pattern as `MemoryRepository.Add`.
- `GetRelatedAsync(Guid rootMemoryId, int maxHops, CancellationToken)` — issues a
  `WITH RECURSIVE` CTE via EF Core's `Database.SqlQuery<T>` (parameterized, no string
  concatenation — avoids SQL injection), bounded by `maxHops` (default 2) and a visited-node array
  in the recursive term to guarantee termination on cycles:

  ```sql
  WITH RECURSIVE graph(to_id, relation_type, hops, path) AS (
      SELECT to_memory_id, relation_type, 1, ARRAY[from_memory_id, to_memory_id]
      FROM memory_edges WHERE from_memory_id = {rootId}
      UNION ALL
      SELECT e.to_memory_id, e.relation_type, g.hops + 1, g.path || e.to_memory_id
      FROM memory_edges e
      JOIN graph g ON e.from_memory_id = g.to_id
      WHERE g.hops < {maxHops} AND e.to_memory_id <> ALL(g.path)
  )
  SELECT DISTINCT to_id AS "RelatedMemoryId", relation_type AS "RelationType", MIN(hops) AS "Hops"
  FROM graph GROUP BY to_id, relation_type;
  ```

  This is plain Postgres SQL — no extension, no admin rights, works in the existing constrained
  environment, and stays inside EF Core's own connection/transaction handling.

## Retrieval changes

`MemoryService.SearchMemoryAsync` (`src/MemoryMcp.Application/Memories/MemoryService.cs`): after
building `matches` for the semantic/keyword/category branch (unchanged logic), for each of the
top few matches call `IMemoryGraphService.GetRelatedAsync` and populate a new
`MemorySearchResultDto.RelatedMemories` field (list of `{Id, Text, RelationType, Hops}`) — additive
to the DTO, so `search_memory`'s existing contract keeps working for clients that ignore the new
field. This is the direct analogue of Supermemory's per-result `relatedMemories`.

## add_memory changes

`MemoryService.SaveAsync`:
1. Fetch top-K similar active memories via `memoryRepository.SearchAsync` (reused, not new).
2. Call `IFactExtractor.ExtractAsync(content, candidates)`. On "not configured", fall back to the
   current single-memory save path unchanged.
3. For each `ExtractedFact`: embed via `IEmbeddingProvider` (unchanged), create+persist a `Memory`
   exactly as today.
4. For each `ExtractedRelation`: insert a `MemoryEdge`; if `RelationType.Updates`, also call
   `existingMemory.Forget(supersededBy: newMemory.Id)` — reusing the existing method so `IsActive`
   filtering in `SearchAsync`/`SearchByKeywordAsync`/`ListByCategoryAsync` keeps working unmodified.

## Migration steps (in order)

1. `Domain`: `MemoryEdge.cs`, `RelationType.cs`.
2. `Application`: `IMemoryEdgeRepository`, `IFactExtractor` + records, `IMemoryGraphService`,
   extend `MemoryDtos.cs` with `RelatedMemories`, update `MemoryService.cs` and `IMemoryService.cs`
   if the interface needs new members (it shouldn't — same public methods, richer results).
3. `Infrastructure`: `MemoryEdgeConfiguration.cs`, EF migration `AddMemoryEdges` (run
   `dotnet ef migrations add AddMemoryEdges --project src/MemoryMcp.Infrastructure --startup-project src/MemoryMcp.Api`),
   `MemoryEdgeRepository.cs`, `Extraction/ExtractionOptions.cs`, `Extraction/LlmFactExtractor.cs`,
   wire both in `DependencyInjection.cs` next to the existing embedding registration.
4. `Api`: no new tools required; `MemoryTools.SearchMemory`'s return type already flows through
   `SearchMemoryResult` → `MemorySearchResultDto`, so the enriched field surfaces automatically.
5. Tests:
   - `tests/MemoryMcp.Application.Tests/Memories/`: extend `MemoryService` unit tests
     (NSubstitute) to cover the new extraction branch and the unconfigured-fallback path.
   - `tests/MemoryMcp.Infrastructure.Tests/`: new `MemoryEdgeRepositoryTests.cs` against real
     Postgres (same `PostgresFixture` pattern as `MemoryRepositoryTests.cs`), asserting multi-hop
     traversal, cycle termination, and `maxHops` bounding.
   - `tests/MemoryMcp.Api.Tests/McpToolsEndToEndTests.cs`: extend to add two related memories and
     assert `search_memory`'s result includes `relatedMemories`.
6. Docs: update `README.md`'s data model table (`memory_edges`), tools table (note enrichment),
   and the "Project phases" checklist; mention the new `Extraction` config section alongside
   `Embeddings` in the setup instructions.

## Verification

- `dotnet build` — solution compiles.
- `dotnet test tests/MemoryMcp.Application.Tests/MemoryMcp.Application.Tests.csproj` — unit tests,
  no external dependencies.
- `dotnet ef database update --project src/MemoryMcp.Infrastructure --startup-project src/MemoryMcp.Api`
  against the dev Postgres, then `dotnet test` with `MEMORYMCP_TEST_CONNECTION_STRING` set, per
  `README.md`'s existing test instructions.
- Manual, via the seeded dev space and an MCP client (or `.http` file): `add_memory` "Alex is a PM
  at Stripe", then `add_memory` "Alex now leads a team of 5 at Stripe" (expect an `Extends` edge),
  then `add_memory` "Alex left Stripe and joined a startup" (expect an `Updates` edge and the
  Stripe-PM memory to become `IsActive=false`); confirm `search_memory("Alex's job")` returns
  `relatedMemories` chaining across all three.

## Decisions confirmed with the user

- Environment constraints (no Docker, no Postgres admin/extensions) still hold — this is why the
  graph lives in plain Postgres via recursive CTEs rather than a dedicated graph database.
- Fact/relation extraction is LLM-based via a pluggable provider (mirrors `IEmbeddingProvider`),
  reusing the already-configured OpenAI/Azure OpenAI endpoint, but written against the
  OpenAI-compatible protocol so a self-hosted open-source model works by config alone.
- Graph relations extend the existing `Memory.SupersededBy`/`IsActive`/`Version` fields rather than
  replacing them — `MemoryEdge` generalizes `SupersededBy`, kept in sync with it.
