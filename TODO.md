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

# Team memory — from single-author to shared spaces

Scoped out on 2026-08-27. Not a single item but a programme of work: what has to change for a `Space` to
be written to by **several people** rather than one.

**Starting point: the multi-tenancy already exists.** `Space`, `ApiKey`, `ApiKeySpaceGrant` and
`AccessLevel` are in place, and every service entry point goes through `RequireSpace` /
`RequireSpaceAccess` in
[AccessContextExtensions.cs](src/MemoryMcp.Application/Abstractions/AccessContextExtensions.cs). Isolation
*between* spaces is solid. What is missing is everything that only matters once two authors share one
space.

**Prerequisite decision — one key per person, not one key per team.** Everything below assumes it. A
shared team key makes attribution (T1) impossible and turns revocation into "rotate the key for
everyone". Recorded here because several items become meaningless if this is decided the other way.

## T1. Attribution: writes have no author

**Where:** `src/MemoryMcp.Domain/Memory.cs`, `Document.cs`, `MemoryEdge.cs`,
`src/MemoryMcp.Application/Memories/MemoryDtos.cs`

All three entities carry `SpaceId` but no `CreatedBy`. In a shared space that means you cannot tell who
wrote a fact, cannot filter "mine vs the team's", cannot weight trust, and cannot reconstruct anything
after the fact.

- `Memory.CreatedByUserId` **and** `CreatedByApiKeyId` — the second distinguishes "the person wrote it"
  from "their agent/CI wrote it".
- Same on `Document` and `MemoryEdge`.
- Expose them on `MemorySearchResultDto` and `MemorySummaryDto`, otherwise the LLM reading the results
  has no way to cite the source.

## T2. Automatic forget/supersede becomes destructive

**Where:** `src/MemoryMcp.Application/Memories/MemoryService.cs`
(`SaveAsync`, the `RelationType.Updates` branch; `ForgetAsync`), `src/MemoryMcp.Domain/Memory.cs`

This is the most delicate change, and it has to land **before** a space is opened to several people.

A save deactivates existing memories when the extractor classifies the relation as `Updates` and
similarity is ≥ `ForgetSimilarityThreshold` (0.8); a forget deactivates the top 3 above the same
threshold. With a single author that is reasonable — you are rewriting your own memory. With several, one
member's save **silently deactivates a colleague's memory**, with no record of who or when.

- `Memory.Forget()` must record `ForgottenByUserId` and `ForgottenAt`; today it only touches `IsActive`
  and `UpdatedAt`.
- Cross-author supersede policy: create the `Updates` edge but do **not** deactivate another author's
  memory — leave it active and surface both as a conflict, or mark it contested.
- Explicit forget of someone else's memory should need a higher access level (see T4) or an explicit
  confirmation step.
- Optimistic concurrency: two agents updating the same fact in parallel currently overwrite each other
  unnoticed. `Memory.Version` is the natural token — which decides item **3** above in favour of "keep
  it and make it mean something".

## T3. Identity: there is no user, so there is no onboarding

**Where:** `src/MemoryMcp.Domain/ApiKey.cs`, `ApiKeySpaceGrant.cs`,
`src/MemoryMcp.Infrastructure/Persistence/Repositories/ApiKeyRepository.cs`

The principal is the key, and grants hang off the key. Practical consequence: adding a person means
minting a key and hand-inserting one grant row per space; removing them means finding all their keys. It
does not scale past three or four people.

- Introduce `User`, `Team`, `TeamMembership(TeamId, UserId, Role)`; `Space.OwnerTeamId`.
- `ApiKey.UserId` — a key becomes a *credential of* a user, who may hold several (laptop, CI, agent).
- Move grants onto team/user; `FindActiveAccessByHashAsync` resolves permissions through membership in a
  single joined query.
- **Keep the shape of `SpaceGrant` and `ICurrentAccessContext` unchanged.** Only the construction of
  `ApiKeyAccessSnapshot` changes; every service above it keeps working untouched. This is what makes the
  item tractable.

## T4. `AccessLevel` is too coarse

**Where:** `src/MemoryMcp.Domain/AccessLevel.cs`,
`src/MemoryMcp.Infrastructure/Persistence/Configurations/ApiKeySpaceGrantConfiguration.cs`

`Read`/`ReadWrite` is not enough: today anyone who can write can also forget the entire space's
knowledge. Wanted, at minimum: `Read` < `Contribute` (adds but cannot forget) < `Curate` < `Admin`
(manages members and grants).

**Migration trap:** the enum is ordered and `Satisfies`/`HasAccess` compare with `>=`, but
`ApiKeySpaceGrantConfiguration` configures no conversion, so EF persists it as `int`. Inserting new values
mid-scale **renumbers rows already stored**. Either convert the column to a string in the same migration,
or append new values at the end and accept an unordered scale (which breaks the `>=` comparison and needs
an explicit comparer instead).

## T5. The "active space" is shared mutable state

**Where:** `src/MemoryMcp.Domain/ApiKeySpaceGrant.cs` (`IsDefault`),
`src/MemoryMcp.Application/Spaces/SpaceService.cs` (`SetActiveSpaceAsync`)

`setActiveSpace` writes to the database on every switch, and the state is per-credential rather than
per-session — with a shared key, whoever switches switches it for everybody. Wanted: an immutable
`User.DefaultSpaceId` plus a per-MCP-session override that is never persisted.

## T6. The profile context does not hold up in a team space

**Where:** `src/MemoryMcp.Application/Memories/MemoryService.cs` (`GetProfileAsync`),
`MemoryRepository.ListRecentActiveAsync`

`GetProfileAsync` returns the 5 most recently created active memories in the space. In a shared space
those five are "the last thing a colleague happened to write" — almost always noise relative to the
current question. The "stable and recent" distinction promised in [CLAUDE.md](CLAUDE.md) has no
counterpart in the code. Needs a stability flag (or a reserved category) for the team-level context, plus
*my* recent memories rather than anyone's.

## T7. Search covers exactly one space

**Where:** `src/MemoryMcp.Application/Abstractions/AccessContextExtensions.cs` (`RequireSpace`),
`MemoryRepository.SearchAsync`

`RequireSpace` resolves exactly one grant, so "search my personal space **plus** the team spaces I belong
to" — the normal team case — is not expressible.

The SQL side is trivial (`"SpaceId" = ANY({spaceIds})`; the HNSW index stays usable). What needs deciding
is how scores merge across spaces and how provenance is shown in the results. Note this interacts with the
under-return problem in *Smaller observations*: a multi-space filter is less selective, so it is actually
the friendlier case for the index.

## T8. Tenant isolation has no defence in depth

**Where:** `src/MemoryMcp.Infrastructure/Persistence/MemoryDbContext.cs`, all repositories

The application-layer checks are well centralized but they are the **only** barrier: no EF global query
filter, no Postgres RLS. Example: `DocumentRepository.GetByIdAsync` does not filter by space — the check
happens downstream in `DocumentService.GetDocumentAsync`, which is correct today but is a convention the
next method can forget. With several teams' data in one database this is the most severe risk class. A
global query filter on `SpaceId` is cheap and makes the omission harmless.

## T9. Provisioning does not exist

**Where:** `src/MemoryMcp.Api/Program.cs` (`SeedDevDataAsync`)

Spaces, keys and grants are only ever created by the `--seed` dev path, writing rows directly. A team
cannot onboard itself. Needs an admin API or CLI: create space, invite user, mint/revoke key, change
access level. Ties into item **1** — if key lookups get cached, revocation must evict the cache entry.

## T10. Operational items that become blocking

Already listed under *Smaller observations* as nice-to-haves; with N members they stop being optional:

- **Rate limiting on `/mcp`** — public HTTPS endpoint guarded only by an API key.
- **Two DB queries per request to authenticate** (item **1**) — multiplied by team traffic.
- **Structured logging of tool calls** with space *and user* — without it, in a team, "who did what" is
  unanswerable even in principle.
- **Per-space quotas** — every write pays for embedding plus LLM extraction, and that cost now multiplies
  by the number of members.
- **Graph-enrichment N+1** (item **2**) — 9 sequential round trips per search.

## Suggested phasing

1. **Make sharing safe** — T1, T2, T8. No new concepts, only columns and policy: attribution,
   `ForgottenBy*`, the cross-author supersede rule, `Version` as a concurrency token, the global query
   filter. One migration, changes confined to `MemoryService`. Best risk/benefit ratio, and it prejudges
   none of the later choices.
2. **Identity and provisioning** — T3, T4, T9. Without this a team cannot onboard at all.
3. **Team-quality recall** — T6, T7, plus provenance in results.
4. **Operations** — T10.

---

## Smaller observations

- ~~**e2e tests hit the real dev database.**~~ **Fixed.** The configuration ordering was the cause:
  `ConfigureAppConfiguration`'s in-memory source is applied *before* the app's own
  `appsettings.{Environment}.json`, so the dev connection string won and every run wrote into the
  developer's working database. `McpApiFactory` now overrides the connection string at the DI level
  (replacing the `DbContextOptions`/`IDbContextOptionsConfiguration` registrations), which is
  independent of source ordering; verified by row counts before/after a run — the dev database is left
  untouched. Still open: the test database needs `pgvector` itself, since the tests apply migrations
  automatically, and Testcontainers with `pgvector/pgvector:pg17` would give real per-run isolation
  (rows still accumulate in whatever database is named) if a container runtime ever becomes usable here.
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
