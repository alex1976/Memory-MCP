# Graph Memory (Phase 2) — Code Review and Corrections

**Date:** 2026-08-12
**Scope:** `git diff HEAD~2 HEAD` at review time — the "memory graph implementation" and "update
config parameters" commits (see [graph-memory-plan.md](graph-memory-plan.md) for the feature design).

Eight independent review passes (line-by-line scans, a cross-file tracer, a removed-behavior
audit, a reuse/simplification/efficiency scan, a CLAUDE.md conventions check, and an altitude/design
review) plus a manual verification pass against the actual code produced 12 confirmed findings.
All 12 were corrected in this pass; the corrections and rationale are below.

## Findings and corrections

### 1. Graph traversal only followed outgoing edges

`MemoryEdgeRepository.GetRelatedAsync` queried `WHERE "FromMemoryId" = rootMemoryId`, but every
edge is created new-fact → older-memory (`MemoryService.SaveAsync`). A search landing on the
*older* memory — the common case — found no relations at all.

**Fix:** `GetRelatedAsync` now runs the recursive CTE in both directions and tags each result with
a new `RelatedMemoryDirection` (`Outgoing`/`Incoming`), so relations surface regardless of which
end of the edge a search lands on.

### 2. Unhandled extraction failures aborted the save

`SaveAsync` only caught `ExtractorNotConfiguredException`. Any other failure (LLM timeout, rate
limit, malformed JSON, or an empty completion `Content` array indexing out of range) propagated
and failed what used to be an always-succeeding local write.

**Fix:** broadened the catch to any non-cancellation exception, falling back to the pre-graph
single-memory save. `LlmFactExtractor` now also guards against an empty `Content` array with a
clear exception instead of an `IndexOutOfRangeException`.

### 3. LLM-driven auto-forget had no confidence guard

An `Updates` relation deactivated the existing memory unconditionally, with none of the 0.8
similarity-score guard that gates the explicit `forget` action, and no disclosure in the response.

**Fix:** the `Updates` branch now requires the candidate's similarity score to meet
`ForgetSimilarityThreshold` (0.8) before forgetting; the edge is still created either way. The
save response now reports how many existing memories were superseded.

### 4. `appsettings.json` nullified code-level provider defaults

The config diff set `Extraction`/`Embeddings` `Provider`/`Model` to `""`, which overrides the C#
options-class defaults (`OpenAI`/`gpt-4o-mini`/`text-embedding-3-small`) via config binding — an
operator who only sets an API key no longer got a working default.

**Fix:** restored explicit non-empty `Provider`/`Model` values matching the C# defaults.

### 5. Relation-linking search ignored the caller's category

The candidate search used to link new facts to existing memories passed `category: null`
regardless of the category the caller saved under, allowing cross-category linking/forgetting.

**Fix:** passes the caller's `category` through to the candidate search.

### 6. `GetByIdsAsync` had no space scoping

Unlike every other query in `MemoryRepository`, `GetByIdsAsync` (used to hydrate related-memory
text) filtered on neither `SpaceId` nor `IsActive`.

**Fix:** added a `SpaceId` filter (closing the latent cross-space read). `IsActive` is deliberately
*not* filtered — a superseded memory is still useful context — but `RelatedMemoryDto` now carries
an `IsActive` flag so callers can tell it apart from a current one.

### 7. stdio startup never ran migrations

`RunStdioAsync` — the documented way Claude Desktop launches this server — never called
`Database.MigrateAsync()`, unlike the HTTP seed path, so a fresh/unmigrated database crashed the
process on first use with no actionable error.

**Fix:** added the migration call before the stdio loop starts.

### 8. Sequential per-fact embedding calls

`SaveAsync` embedded each extracted fact one at a time, even though `EmbedBatchAsync` existed
for exactly this and had zero real call sites.

**Fix:** facts are now batch-embedded in one call; a fact whose text matches the original content
verbatim reuses the already-computed content embedding instead of re-embedding it.

### 9. `AddMemoryResult.MemoryId` dropped all but the first fact's id

`AffectedCount` correctly reported every extracted fact, but `MemoryId` only ever returned the
first one, silently making the rest unaddressable by id.

**Fix:** added `MemoryIds` (all saved ids) alongside the existing `MemoryId` (kept as the first,
for backward compatibility).

### 10. `Lazy<T>` client registration cached failures permanently

`Lazy<EmbeddingClient>`/`Lazy<ChatClient>` used the default `ExecutionAndPublication` mode, so a
transient construction failure (e.g. bad config at first use) was rethrown for the rest of the
process's lifetime even after the underlying config was fixed.

**Fix:** switched both to `LazyThreadSafetyMode.PublicationOnly`.

### 11. `reset-db.ps1`'s `-Seed` didn't behave like a flag

`[bool]$Seed = $true` meant a bare `-Seed` (the natural reading given `-Force` works that way)
threw a parameter-binding error instead of toggling.

**Fix:** changed to `[switch]$Seed = $true`, which supports both bare `-Seed` and the already-
documented `-Seed:$false`.

## Deliberately not changed

One reported finding — that a successful save stores the LLM's paraphrased fact text rather than
the caller's verbatim `content` (contradicting the "saves the supplied content by default" wording
in the top-level `CLAUDE.md`) — was **not** changed here. Storing extracted, atomic facts instead
of the raw input is the intended behavior of the graph memory feature itself, not a bug; resolving
the discrepancy is a product decision (e.g. update the documented contract, or always additionally
persist the verbatim content) rather than a code fix, and was left for a separate decision.

## Verification

- `dotnet build` — 0 warnings, 0 errors across all projects.
- `dotnet test tests/MemoryMcp.Application.Tests` — 24/24 passing (includes new tests for the
  similarity-guard, category-scoping, and embedding-reuse fixes).
- `tests/MemoryMcp.Infrastructure.Tests` (Postgres/Testcontainers) and
  `tests/MemoryMcp.Api.Tests` were updated and compile, but could not be executed in this
  environment — Docker was not available.
