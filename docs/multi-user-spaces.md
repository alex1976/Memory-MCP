# Multi-user spaces: users, roles, and write attribution

Implemented 2026-08-29. Covers the first tranche of the "team memory" programme in
[TODO.md](../TODO.md): a space can now be read and written by several people, each identified, each
attributed. It answers five requirements:

1. A user is either a **Writer** (read/write) or a **Reader** (read only).
2. Reads and writes are isolated **by space**; no read or search ever crosses a space boundary.
3. A user reaches **N spaces** through their API key's grants.
4. A user reads **everyone's** information: nothing in search or listing is filtered by author.
5. **Memories and documents record who created and who last updated them.**

## The model

```
User (Email, DisplayName, Role, IsActive)
 └── ApiKey (UserId, KeyHash, Label)          one person, one or more credentials
      └── ApiKeySpaceGrant (SpaceId, AccessLevel, IsDefault)   one credential, N spaces
```

`User` is new; `ApiKey.UserId` is new and required. Everything below `ApiKey` — grants, `SpaceGrant`,
`ICurrentAccessContext`, `RequireSpace` — kept its shape, which is why this change stayed confined to
the construction of the access snapshot instead of rippling through every service.

`ApiKey.OwnerEmail` is gone: the owner's identity now lives on `User.Email`, and keeping a second,
unenforced copy of it on the credential would only invite the two to disagree. `ApiKey.Label` remains,
but now means "which credential" (laptop, CI, agent), not "whose".

## Two levels, one effective answer

There are deliberately two things called access, and they are not redundant:

| | Scope | Meaning |
| --- | --- | --- |
| `User.Role` | The person, everywhere | The ceiling they can never exceed |
| `ApiKeySpaceGrant.AccessLevel` | One credential on one space | What they were given there |

The **effective** level is the lower of the two. A Reader holding a `ReadWrite` grant is read-only; a
Writer holding a `Read` grant on one space is read-only *on that space*. Demoting someone to Reader
therefore removes write access everywhere at once, without touching a single grant row.

The capping happens in exactly one place — `ApiKeyRepository.FindActiveAccessByHashAsync`, where the
snapshot is built — and every consumer downstream reads one already-capped
`SpaceGrant.AccessLevel`:

- `RequireSpace(containerTag, AccessLevel.ReadWrite)` refuses a Reader without knowing roles exist.
- `listSpaces` and `whoAmI` report the effective level, so a Reader is never shown "ReadWrite" against
  a space they cannot write to.
- The `guided-save` and `upload-file` widgets offer only spaces whose level is `ReadWrite`, so a Reader
  is not invited to write a draft that would be refused on submit.

Nothing has to combine role and grant itself, which is what keeps the two levels from drifting into
disagreement.

`UserRole` is persisted **as a string**, unlike `AccessLevel`: its values are compared by identity, so
a future role can be added without renumbering rows. `AccessLevel` is still an `int` and still compared
with `>=`, so inserting a value into the middle of it remains the migration trap described in TODO T4.

## Deactivating a user

`User.IsActive` is joined into the authentication query. A deactivated user's keys all stop
authenticating immediately — the one offboarding step that does not require finding every credential
the person ever minted. Their *name* is still resolved for attribution: what they wrote while active
must not silently become anonymous.

## Isolation stays per-space (requirement 2)

Unchanged by design. `RequireSpace` resolves exactly one grant and every repository query filters on
that single `SpaceId`; there is no multi-space read path to leak through. A space a key holds no grant
on is indistinguishable from one that does not exist. `McpMultiUserEndToEndTests` pins this from the
outside: a memory seeded into an ungranted space is invisible to semantic search, invisible to keyword
search, and naming its space key explicitly returns a tool error.

Cross-space search — "my personal space *plus* my team's" — remains deliberately unimplemented
(TODO T7); it is a feature request, not a gap in this work.

## Attribution (requirement 5)

`Memory` and `Document` both carry `CreatedByUserId` and `UpdatedByUserId`, and both are exposed as
ids *and* resolved display names on the DTOs the tools return (`createdBy`, `updatedBy`), so a model
reading a search result can cite the source without a second call.

The two ids stay separate on purpose. `UpdatedByUserId` is the only record of **who deactivated a
memory**, and in a shared space that is frequently not its author:

- an explicit `add_memory action=forget` stamps the caller onto every memory it deactivates;
- a save whose extracted fact `Updates` an existing memory above the similarity threshold stamps the
  caller onto the superseded memory, leaving `CreatedByUserId` — the original author — untouched.

Both are covered by unit tests asserting exactly that split.

The names are resolved in one batched lookup per call (`UserAttribution.LoadAsync`) over the distinct
author ids present in the result set, so listing a page of memories costs one extra query, not one per
row. A result set with no attributed rows costs no query at all.

### What is deliberately *not* attributed

- **`MemoryEdge`** has no author. An edge is a derived artifact of the save that created it, and its
  `FromMemoryId` already leads to a memory that carries one.
- **Pre-existing rows.** The migration leaves `CreatedByUserId` NULL on memories and documents written
  before users existed. There is no record of who wrote them, and inventing one would be worse than
  admitting it. Every consumer treats a missing name as "unattributed" rather than an error.

## The migration

`20260829100411_AddUsersAndWriteAttribution`. The scaffolded version added `api_keys.UserId` as NOT
NULL with an all-zeros default, which would have pointed every existing key at a user that does not
exist and failed its own foreign key. The `Up` was therefore hand-written in this order:

1. create `users` (+ the unique index on `Email`, which the backfill's `ON CONFLICT` relies on);
2. add `api_keys.UserId` **nullable**;
3. derive one user per distinct owner from the data already in `api_keys` — grouping on
   `lower(OwnerEmail)` so a person's laptop key and CI key collapse into *one* user, and synthesizing
   `<keyprefix>@legacy.memory-mcp.local` for keys that carried no email. Backfilled users are created
   as `Writer`, so a key that could write yesterday can still write today;
4. `SET NOT NULL`, then drop `OwnerEmail`;
5. add the attribution columns, indexes, and foreign keys.

Keys that carried no email are keyed on their prefix, which is effectively unique for app-generated
keys but not enforced to be — hand-made or test-fixture keys sharing a prefix collapse into one
synthetic user. Accepted, not worked around: a pre-users key has by definition authored no row that
names it, so over-merging those identities loses nothing, whereas one-user-per-key-id would invent a
separate person for every credential a real user held. (Observed for real: the dev database held 39
keys across 19 distinct prefixes, the duplicates being test fixtures left behind by the old
test-isolation bug.)

Three details worth keeping in mind if this is ever edited:

- `AlterColumn(nullable: false)` alone does **nothing**. The generator diffs the operation against the
  old column it is told about, so without `oldNullable: true` the column silently stays nullable —
  which is exactly what the first version of this migration did.
- The user foreign keys on `memories`/`documents` are `ON DELETE SET NULL`, emphatically not
  `CASCADE`. Deleting a user must never delete the knowledge they contributed to a shared space.
  Losing an author's name is acceptable; losing the team's memories because someone left is not.

Verified by round-tripping (`up` → `down` → `up`) against a scratch database pre-loaded with
pre-users rows: three keys, two of them sharing an owner email in different casing, collapsed into two
users, no null `UserId`, existing memories and documents left unattributed. Then applied for real to
the development database (39 keys, 113 memories, 29 documents): the working `dev-seed` key kept its
`ReadWrite` grant on `default` as a `Writer`, and every pre-existing row stayed unattributed.

## Provisioning

`dotnet run --project src/MemoryMcp.Api -- --seed` now creates two spaces and two users — a Writer with
grants on both spaces and a Reader with a grant on one — and prints both plaintext keys. The Reader's
grant is deliberately seeded as `ReadWrite` so that the role ceiling is what makes them read-only,
which is the behaviour worth exercising by hand.

A real admin API or CLI (create space, invite user, mint/revoke key, change role) is still missing;
that is TODO T9, and it is the next thing a team that wants to onboard itself will need.
