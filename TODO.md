# TODO — architectural follow-ups

Items identified during the architecture review of 2026-08-26 that are **not** implemented, because each
changes runtime behaviour, data, or infrastructure rather than just code structure. Ordered by impact.

Already done and on `main`'s working tree:

- **Structural fixes** — centralized access checks, `ValidationException` mapping, real DB health check,
  startup options validation, HTTP/stdio registration dedupe, embedding-batch guard.
- **pgvector migration** (was item 1, the largest item on this list) — `halfvec(3072)` column with an HNSW
  cosine index, KNN pushed into SQL, embedding width made schema-bound. See
  [docs/pgvector-halfvec-search.md](docs/pgvector-halfvec-search.md).

---

## 1. Cache API-key lookups

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

## 2. Fix the graph-enrichment N+1

**Where:** `src/MemoryMcp.Application/Memories/MemoryService.cs` (`SearchMemoryAsync`, the
`RelatedMemoriesTopMatches` loop)

Enriching the top 3 matches costs 9 sequential round trips: per match, two recursive CTEs
(`TraverseOutgoingAsync` / `TraverseIncomingAsync`) plus one `GetByIdsAsync`.

- Cheap version: hoist `GetByIdsAsync` out of the loop and batch it across all roots → 7 round trips.
- Better version: add a batched `GetRelatedAsync(IReadOnlyList<Guid> rootIds, ...)` that traverses all
  roots in a single CTE seeded from the id list → 2 round trips.

Pure refactor, no semantic change. `MemoryGraphService` and `IMemoryEdgeRepository` both need the new
signature.

## 3. Decide the fate of `Memory.Version`

**Where:** `src/MemoryMcp.Domain/Memory.cs`, `MemorySummaryDto`, `listMemories` tool output

`Version` is set to `1` in the constructor and never incremented anywhere, but it is exposed through
`MemorySummaryDto` and therefore in `listMemories` results — clients see a field that always reads `1`.

Either:
- implement real versioning (increment when a memory is superseded via `Forget(supersededBy:)`, so the
  `SupersededBy` chain has a meaningful ordering), or
- drop it from the DTO and the domain entity, and remove the column in a migration.

## 4. Verify pgvector on the deploy target before the next Fly.io release

**Where:** [fly.toml](fly.toml) (`release_command`), the migration `AddPgvectorHalfvecEmbedding`

The migration now runs `CREATE EXTENSION vector`, which needs **superuser** — pgvector is not a *trusted*
extension. If the managed Postgres behind the deploy either lacks pgvector ≥ 0.7.0 or doesn't grant that
permission, the `release_command` fails and blocks the whole deploy. Check before releasing:

```bash
psql "<connection-string>" -c "CREATE EXTENSION IF NOT EXISTS vector; SELECT extversion FROM pg_extension WHERE extname='vector';"
```

Also note `halfvec` requires pgvector ≥ 0.7.0 specifically, and that pgvector binaries won't load on a
PostgreSQL *release candidate* — the ABI only stabilizes at GA.

---

## Smaller observations

- **e2e tests hit the real dev database.** `McpApiFactory.ConfigureWebHost` adds an in-memory
  `ConnectionStrings:Default`, but it does not appear to take effect — the tests connect using the value
  from `appsettings.Development.json`. Worth confirming the configuration ordering, otherwise a test run
  writes into the developer's working database. (Space keys are randomized per run, so it does not
  currently collide, but rows accumulate.) Now that pgvector is in play, the test database also needs the
  extension — and Testcontainers with `pgvector/pgvector:pg17` would fix the isolation problem at the same
  time, if a container runtime ever becomes usable here.
- **HNSW recall is unmeasured.** No benchmark compares the index's results against exact KNN on this
  corpus. Not yet urgent: at the current row count the planner still prefers a sequential scan, so the
  index isn't actually serving queries. Worth measuring once the corpus grows enough for it to kick in.
- **Filtered vector search can under-return.** `SpaceId`/`IsActive`/`Category` are applied as post-index
  filters, so a highly selective filter over a large corpus can yield fewer than `topK` rows. pgvector
  0.8.0+ iterative index scans address this and are not enabled.
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
