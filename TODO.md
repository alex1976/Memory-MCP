# TODO — architectural follow-ups

Items identified during the architecture review of 2026-08-26 that are **not** implemented, because each
changes runtime behaviour, data, or infrastructure rather than just code structure. Ordered by impact.

Already done and on `main`'s working tree:

- **Structural fixes** — centralized access checks, `ValidationException` mapping, real DB health check,
  startup options validation, HTTP/stdio registration dedupe, embedding-batch guard.
- **pgvector migration** (was item 1, the largest item on this list) — `halfvec(3072)` column with an HNSW
  cosine index, KNN pushed into SQL, embedding width made schema-bound. See
  [docs/pgvector-halfvec-search.md](docs/pgvector-halfvec-search.md).
- **Multi-user spaces** (2026-08-29) — `User` + `Writer`/`Reader` roles, keys as credentials of a user,
  the role as a ceiling over per-space grants, and `CreatedBy`/`UpdatedBy` on memories and documents.
  Closes **T1** and the identity half of **T3**; see
  [docs/multi-user-spaces.md](docs/multi-user-spaces.md) and the per-item notes below.

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

**Update (2026-08-29): identity and attribution now exist too** — `User`, `Writer`/`Reader` roles, keys
as credentials of a user, and `CreatedBy`/`UpdatedBy` on memories and documents
([docs/multi-user-spaces.md](docs/multi-user-spaces.md)). Several people can share a space safely enough
to be attributed; what is still missing is the *policy* around cross-author writes (T2), defence in
depth (T8), and — narrowed on 2026-08-30, when onboarding became a CLI — any way to *change* an
existing user, key or grant without an UPDATE (T9). Item notes below are marked individually.

**Prerequisite decision — one key per person, not one key per team.** Everything below assumes it. A
shared team key makes attribution (T1) impossible and turns revocation into "rotate the key for
everyone". Recorded here because several items become meaningless if this is decided the other way.
**Decided and implemented this way on 2026-08-29**: `ApiKey.UserId` is required, so an unattributed
credential is no longer representable.

## T1. Attribution: writes have no author — **DONE (2026-08-29)**

`Memory` and `Document` carry `CreatedByUserId` and `UpdatedByUserId`, exposed on
`MemorySearchResultDto`, `MemorySummaryDto`, `DocumentSummaryDto` and `DocumentDetailDto` as both ids
and resolved display names (batched, one query per call).

Two deviations from the sketch above, both deliberate:

- **No `CreatedByApiKeyId`.** "The person" vs "their CI" is what `ApiKey.Label` already records, and a
  second attribution column on every row buys a distinction nothing currently consumes. Add it when
  something needs to act on it.
- **No attribution on `MemoryEdge`.** An edge is a derived artifact of the save that created it, and its
  `FromMemoryId` leads to a memory that already carries the author.

Still open: filtering "mine vs the team's" is possible now that the column exists, but no tool exposes
it — see T6, which is where it would belong.

## T2. Automatic forget/supersede becomes destructive

**Where:** `src/MemoryMcp.Application/Memories/MemoryService.cs`
(`SaveAsync`, the `RelationType.Updates` branch; `ForgetAsync`), `src/MemoryMcp.Domain/Memory.cs`

This is the most delicate change, and it has to land **before** a space is opened to several people.

A save deactivates existing memories when the extractor classifies the relation as `Updates` and
similarity is ≥ `ForgetSimilarityThreshold` (0.8); a forget deactivates the top 3 above the same
threshold. With a single author that is reasonable — you are rewriting your own memory. With several, one
member's save **silently deactivates a colleague's memory**, with no record of who or when.

- ~~`Memory.Forget()` must record `ForgottenByUserId` and `ForgottenAt`~~ **Done (2026-08-29):**
  `Forget(byUserId:)` stamps `UpdatedByUserId`/`UpdatedAt`, so a deactivation is now attributable —
  both for an explicit forget and for a save that supersedes someone else's memory. A separate
  `ForgottenAt` was not added: `UpdatedAt` already dates it, and a memory's text is never edited in
  place, so the two would always be the same value.
  **The deactivation itself is still silent** — the rest of this item is unchanged.
- Cross-author supersede policy: create the `Updates` edge but do **not** deactivate another author's
  memory — leave it active and surface both as a conflict, or mark it contested. This is now
  *expressible* (the save knows both the caller and the target's author) but not yet implemented.
- Explicit forget of someone else's memory should need a higher access level (see T4) or an explicit
  confirmation step.
- Optimistic concurrency: two agents updating the same fact in parallel currently overwrite each other
  unnoticed. `Memory.Version` is the natural token — which decides item **3** above in favour of "keep
  it and make it mean something".

## T3. Identity — **PARTLY DONE (2026-08-29)**

**Where:** `src/MemoryMcp.Domain/User.cs`, `ApiKey.cs`, `ApiKeySpaceGrant.cs`,
`src/MemoryMcp.Infrastructure/Persistence/Repositories/ApiKeyRepository.cs`

Done:

- `User` (email, display name, role, `IsActive`) and `ApiKey.UserId` — a key is now a *credential of* a
  user, who may hold several. `ApiKey.OwnerEmail` was dropped, since `users.email` supersedes it.
- `FindActiveAccessByHashAsync` resolves key → user → grants, joins `users.IsActive` (so deactivating
  one person invalidates all their credentials), and caps each grant by the user's role.
- The shape of `SpaceGrant` and `ICurrentAccessContext` was kept: only `ApiKeyAccessSnapshot`'s
  construction changed (plus an additive `User` member on the context), so every service above it kept
  working. This is what made the item tractable, as predicted.

Deliberately **not** done, because nothing yet needs it:

- **No `Team`/`TeamMembership`/`Space.OwnerTeamId`.** With grants still hanging off the key, sharing a
  space means minting keys with the same grant — fine for a handful of people, and a team layer would
  have doubled the surface of this change. Add it when onboarding volume, not sharing, becomes the
  problem.
- **Grants were not moved onto the user.** Consequence: adding a space for a person who holds three
  keys is still three grant rows. This is the practical scaling limit called out above, and it is what
  a `Team` (or at least user-level grants) would fix.

Still open, and now the binding constraint: **onboarding** — see T9.

## T4. `AccessLevel` is too coarse

**Where:** `src/MemoryMcp.Domain/AccessLevel.cs`,
`src/MemoryMcp.Infrastructure/Persistence/Configurations/ApiKeySpaceGrantConfiguration.cs`

`Read`/`ReadWrite` is not enough: today anyone who can write can also forget the entire space's
knowledge. Wanted, at minimum: `Read` < `Contribute` (adds but cannot forget) < `Curate` < `Admin`
(manages members and grants).

**Unchanged by the 2026-08-29 multi-user work, on purpose.** `UserRole` (`Writer`/`Reader`) was added
*alongside* `AccessLevel`, not in place of it: the role is a per-person ceiling, the grant is per-space,
and the effective level is the lower of the two. Neither scale gained a value, so no stored row was
renumbered. Note the two are persisted differently and the reason matters here: `UserRole` is a **string**
column precisely so it can gain values freely, while `AccessLevel` is still an ordered `int` — so the
migration trap below applies to `AccessLevel` alone.

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

The "*my* recent memories" half is now cheap: `memories.CreatedByUserId` exists and is indexed, and
`ICurrentAccessContext.User.Id` is available in `MemoryService` — it needs a
`ListRecentActiveByAuthorAsync` and a decision about how the two halves are mixed in one profile. Note
this is the one place a *deliberate* filter by author belongs; search itself must stay unfiltered.

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

**Unchanged by the multi-user work** — a global query filter needs an ambient space on the `DbContext`,
which fights the legitimately multi-space queries (`SpaceRepository.GetCountsAsync`, `ApiKeyRepository`),
so it stays a decision of its own. What was added instead is *external* evidence that today's single
barrier holds: `McpMultiUserEndToEndTests` proves over HTTP that a memory in an ungranted space is
unreachable by semantic search, by keyword search, and by naming its space key. That is a regression
test, not defence in depth — this item is still open and still the most severe class.

## T9. Provisioning does not exist

**Where:** `src/MemoryMcp.Api/Program.cs` (`SeedDevDataAsync`),
`src/MemoryMcp.Api/ProvisioningCommands.cs`

Spaces, users, keys and grants are only ever created by the `--seed` dev path, writing rows directly
(it now seeds two users and two spaces, but it is still a dev fixture, not provisioning). A team cannot
onboard itself. Needs an admin API or CLI: create space, invite user, mint/revoke key, change role,
change access level, deactivate user. Ties into item **1** — if key lookups get cached, revocation and
user deactivation must evict the cache entry.

With identity now in place (T3) this is the binding constraint on actually using multi-user spaces:
everything the runtime needs exists, but the only way to add a person is a hand-written INSERT.

**Update (2026-08-30): the onboarding half exists as a CLI.** `--create-user` and `--create-api-key`
(`ProvisioningCommands`, wrapped by [scripts/create-user.ps1](scripts/create-user.ps1) and
[scripts/create-api-key.ps1](scripts/create-api-key.ps1)) create a person and mint a credential with
per-space grants, so adding someone is no longer an INSERT and the key hash is no longer computed
outside the code that verifies it. Two verbs rather than one, because a person outlives any single
credential they hold.

**Update (2026-08-31): creating a space landed too.** `--create-space`
([scripts/create-space.ps1](scripts/create-space.ps1)) creates a space and grants existing keys on it,
targeting a key by owner email (fanning out to every credential that person holds), key id, or printed
prefix; `-AllowExisting` opens an already-existing space to one more credential, which is a grant
mutation rather than a creation and closes the gap that made `--create-api-key` the only way to hand
out access. Grants are resolved before anything is saved, so a typo cannot leave a space created with
half its grants applied.

Still missing, and still T9: **revoke key**, **change role**, **change access level**, **deactivate
user** — every one of them a mutation of something that already exists, which is the harder half
(revocation is what item **1**'s cache would have to evict). And it is a CLI, so it only serves whoever
can reach the database host; a team still cannot onboard itself.

## T10. Operational items that become blocking

Already listed under *Smaller observations* as nice-to-haves; with N members they stop being optional:

- **Rate limiting on `/mcp`** — public HTTPS endpoint guarded only by an API key.
- **Two DB queries per request to authenticate** (item **1**) — multiplied by team traffic.
- **Structured logging of tool calls** with space *and user* — without it, in a team, "who did what" is
  unanswerable even in principle. The user is now available to log two ways: `ICurrentAccessContext.User`
  inside the services, and the `user_id`/name/role claims the authentication handler puts on the
  principal (added so logging need not reach into the access context). Only the logging itself is left.
- **Per-space quotas** — every write pays for embedding plus LLM extraction, and that cost now multiplies
  by the number of members.
- **Graph-enrichment N+1** (item **2**) — 9 sequential round trips per search.

## Suggested phasing

1. ~~**Make sharing safe**~~ — **partly done (2026-08-29).** Attribution (T1) and the identity it needs
   (T3) landed together, since a `CreatedBy` with nothing to point at is not worth a migration; the
   provenance intended for step 3 came with them. What remains of this step: the cross-author supersede
   rule and `Version` as a concurrency token (T2), and the global query filter (T8).
2. **Provisioning** — T9. Onboarding landed as a CLI on 2026-08-30 (`--create-user`,
   `--create-api-key`) and space creation with grants on 2026-08-31 (`--create-space`); what remains
   binding is the mutations — revoke, change role, change access level, deactivate. T4 (finer access
   levels) is independent and can wait.
3. **Team-quality recall** — T6, T7. Provenance in results is done.
4. **Operations** — T10. Structured logging of tool calls now has a user id to log
   (`ICurrentAccessContext.User`, and the `user_id`/name/role claims on the principal).

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
