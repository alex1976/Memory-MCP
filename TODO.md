# TODO — architectural follow-ups

Items identified during the architecture review of 2026-08-26 that were **not** implemented, because each
changes runtime behaviour, data, or infrastructure rather than just code structure. Ordered by impact.

The review's structural fixes (centralized access checks, `ValidationException` mapping, real DB health
check, startup options validation, HTTP/stdio registration dedupe, embedding-batch guard) are already done
and on `main`'s working tree.

---

## 1. Replace in-app cosine similarity with pgvector

**Where:** `src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs` (`SearchAsync`)

`SearchAsync` loads *every* active memory in the space that has an embedding into application memory, then
scores in C#. With `Embeddings:Dimensions = 3072` that is ~12 KB per row, so 10k memories is roughly 120 MB
materialized per search — and `add_memory` pays the same cost, because it runs a search to find extraction
candidates. Entities are also loaded **tracked** (deliberately, so `ForgetAsync` can soft-delete via change
tracking), which means the read path carries change-tracking overhead for the whole space.

The existing code comments say pgvector was unavailable "in this environment"; if that constraint has
lifted, this is the single largest scalability win.

Work involved:
- Swap the `postgres:17` image in `docker-compose.yml` for `pgvector/pgvector:pg17`; confirm the Fly.io
  Postgres has the extension available.
- `CREATE EXTENSION vector;` in a new EF migration, and change the `Embedding` column from `real[]` to
  `vector(N)`.
- Add an HNSW (or IVFFlat) index; order by the `<=>` cosine-distance operator in the query.
- Decouple the read path from `ForgetAsync`'s reliance on tracked entities — return untracked projections
  from search, and have `ForgetAsync` re-fetch by id for the handful of rows it actually mutates.
- Note the interaction with `VectorSettings.Dimensions` / `EmbeddingOptions.Dimensions`: a `vector(N)`
  column pins the width at the schema level, so changing dimensions becomes a migration, not just config.

## 2. Cache API-key lookups

**Where:** `src/MemoryMcp.Infrastructure/Persistence/Repositories/ApiKeyRepository.cs`,
`src/MemoryMcp.Api/Auth/ApiKeyAuthenticationHandler.cs`

Every single HTTP request performs two database queries to authenticate (the key row, then a join for its
space grants). An `IMemoryCache` keyed on the key hash removes that from the hot path.

**Open decision:** caching delays revocation. A revoked or deleted key stays usable until its entry
expires. Pick an acceptable revocation delay (30–60s is typical) before implementing, or add explicit
cache eviction to whatever admin path eventually revokes keys.

Related: `ApiKeyAuthenticationHandler` populates `CurrentAccessContext` as a side effect of
`HandleAuthenticateAsync`. Authentication handlers can run more than once per request, so this coupling is
implicit and fragile. Consider populating the context from a middleware or a factory that reads the
authenticated principal instead.

## 3. Fix the graph-enrichment N+1

**Where:** `src/MemoryMcp.Application/Memories/MemoryService.cs` (`SearchMemoryAsync`, the
`RelatedMemoriesTopMatches` loop)

Enriching the top 3 matches costs 9 sequential round trips: per match, two recursive CTEs
(`TraverseOutgoingAsync` / `TraverseIncomingAsync`) plus one `GetByIdsAsync`.

- Cheap version: hoist `GetByIdsAsync` out of the loop and batch it across all roots → 7 round trips.
- Better version: add a batched `GetRelatedAsync(IReadOnlyList<Guid> rootIds, ...)` that traverses all
  roots in a single CTE seeded from the id list → 2 round trips.

Pure refactor, no semantic change. `MemoryGraphService` and `IMemoryEdgeRepository` both need the new
signature.

## 4. Decide the fate of `Memory.Version`

**Where:** `src/MemoryMcp.Domain/Memory.cs`, `MemorySummaryDto`, `listMemories` tool output

`Version` is set to `1` in the constructor and never incremented anywhere, but it is exposed through
`MemorySummaryDto` and therefore in `listMemories` results — clients see a field that always reads `1`.

Either:
- implement real versioning (increment when a memory is superseded via `Forget(supersededBy:)`, so the
  `SupersededBy` chain has a meaningful ordering), or
- drop it from the DTO and the domain entity, and remove the column in a migration.

---

## Smaller observations

- **e2e tests hit the real dev database.** `McpApiFactory.ConfigureWebHost` adds an in-memory
  `ConnectionStrings:Default`, but it does not appear to take effect — the tests connect using the value
  from `appsettings.Development.json`. Worth confirming the configuration ordering, otherwise a test run
  writes into the developer's working database. (Space keys are randomized per run, so it does not
  currently collide, but rows accumulate.)
- **No rate limiting on `/mcp`**, which is a public HTTPS endpoint guarded only by an API key. ASP.NET
  Core's built-in rate limiter would bound brute-force and abuse.
- **No request size limit** on `create_document`, which accepts base64-encoded PDF bytes inline.
- **No structured logging or tracing of tool calls** — there is currently no way to see which tool was
  invoked, for which space, or how long it took.
- **`ApiKeyHasher` uses unsalted SHA-256, and that is correct here** — keys are 128 bits of randomness, so
  there is no dictionary attack to salt against, and bcrypt/argon2 would add latency to every request.
  Worth a code comment so nobody "fixes" it later.
- **`appsettings.Development.json` holds a live Gemini API key in plaintext.** It is gitignored, so it has
  not leaked to the repository, but consider moving it to user secrets (`dotnet user-secrets`).
