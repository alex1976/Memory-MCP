# Come funziona la ricerca: semantica, per categoria e fuzzy sulle keyword

`search_memory` (`MemoryService.SearchMemoryAsync`, in
[MemoryService.cs](../src/MemoryMcp.Application/Memories/MemoryService.cs)) accetta tre criteri —
`query`, `keyword`, `category` — e ne richiede **almeno uno**; se sono tutti vuoti l'operazione
fallisce con `ArgumentException("Provide at least one of query, keyword, or category.")`. Il
comportamento non è un semplice AND tra i tre filtri: c'è una precedenza fissa tra i primi due, e
`category` si comporta diversamente da solo o in combinazione.

![Sketchnote della logica di ricerca: query → keyword → category, con priorità, filtri e ranking di ciascun percorso](search-logic-sketchnote.svg)

## Ordine di valutazione

```csharp
if (!string.IsNullOrWhiteSpace(query))       // 1. ricerca semantica (+ category opzionale)
else if (!string.IsNullOrWhiteSpace(keyword)) // 2. ricerca keyword/fuzzy (+ category opzionale)
else if (!string.IsNullOrWhiteSpace(category)) // 3. solo elenco per categoria
else throw new ArgumentException(...);
```

- Se è presente `query`, **`keyword` viene ignorato del tutto** — non c'è fusione dei due punteggi.
  Per fare ricerca solo per keyword bisogna omettere `query`.
- `category`, quando presente insieme a `query` o `keyword`, filtra i candidati (`AND`): riduce
  l'insieme su cui si calcola la similarità/il match, non è un criterio di ranking separato.
- Se `category` è l'unico criterio, si passa a un terzo percorso (`ListByCategoryAsync`) che non
  fa scoring: elenca semplicemente le memorie attive di quella categoria, ordinate per data di
  creazione decrescente.

Tutti e tre i percorsi filtrano su `SpaceId` (lo spazio attivo o quello indicato da `containerTag`)
e su `IsActive` (le memorie "dimenticate" o superate da un `Updates` non vengono mai restituite).
Il numero massimo di risultati è fisso: `SearchTopK = 10` (costante privata in `MemoryService`, non
configurabile per singola chiamata).

## 1. Ricerca semantica (`query`)

`MemoryRepository.SearchAsync` ([MemoryRepository.cs:25](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L25)):

1. `query` viene trasformato in un vettore da `IEmbeddingProvider.EmbedAsync` — provider
   collegabile (OpenAI, Azure OpenAI o Gemini, tutti tramite lo stesso `EmbeddingClient` dell'SDK
   OpenAI, puntato a un base URL diverso per Gemini). La larghezza è vincolata allo schema
   (`VectorSettings.Dimensions` = `3072`): `Embeddings:Dimensions` deve coincidere, o l'avvio fallisce.
2. L'embedding è salvato in una colonna `pgvector` di tipo `halfvec(3072)` sulla riga `memories`,
   con indice **HNSW** sulla distanza coseno.
3. `SearchAsync` esegue la KNN **dentro Postgres**: ordina per `Embedding <=> :query` filtrando su
   `SpaceId`, `IsActive`, `Embedding IS NOT NULL` e, se passato, `Category`, con `LIMIT topK`. La query
   proietta solo id e distanza — gli embedding non transitano mai sulla rete — e solo le righe vincenti
   vengono poi caricate come entità tracciate.

> `halfvec` e non `vector`: gli indici HNSW di pgvector si fermano a 2000 dimensioni sul tipo `vector`,
> mentre gli embedding qui sono a 3072. Dettagli, teoria e note operative in
> [pgvector-halfvec-search.md](pgvector-halfvec-search.md).

Il punteggio (`Score`) restituito in `MemorySearchResultDto` per questo percorso è la cosine
similarity (0–1), ricavata dalla distanza restituita da Postgres come `1 - distanza`.

## 2. Ricerca per keyword, con fuzzy matching (`keyword`)

`MemoryRepository.SearchByKeywordAsync` ([MemoryRepository.cs:46-67](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L46-L67))
non genera nessun embedding: combina due condizioni in OR sulla colonna `Text`:

```csharp
.Where(m => m.SpaceId == spaceId && m.IsActive &&
    (EF.Functions.ILike(m.Text, $"%{keyword}%") ||
     EF.Functions.TrigramsAreWordSimilar(keyword, m.Text)))
```

- **Sottostringa esatta**, case-insensitive: `ILIKE '%keyword%'`.
- **Fuzzy/tipo-tolerante**: l'estensione Postgres `pg_trgm`, tramite `word_similarity(keyword,
  text) >= pg_trgm.word_similarity_threshold` (soglia di default dell'estensione, `0.6`, non
  configurata a livello applicativo). `pg_trgm` scompone le stringhe in trigrammi di caratteri e
  misura la sovrapposizione — per questo tollera errori di battitura, non solo forme diverse della
  stessa parola.
- Entrambe le condizioni sono servite dallo stesso indice **GIN trigram** su `memories.text`
  (`gin_trgm_ops`, definito in
  [MemoryConfiguration.cs:32-34](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryConfiguration.cs#L32-L34)),
  creato dalla migrazione `AddTrigramKeywordSearch`. L'estensione `pg_trgm` è registrata in
  `MemoryDbContext.OnModelCreating` (`modelBuilder.HasPostgresExtension("pg_trgm")`) — a differenza
  di `pgvector`, non richiede permessi di amministratore, quindi funziona anche in questo ambiente
  vincolato.
- I risultati sono ordinati per `word_similarity` decrescente (match esatti/vicini prima di quelli
  puramente fuzzy), poi per data di creazione decrescente a parità di punteggio.
- `category`, se passato, filtra ulteriormente (`AND`), esattamente come nel percorso semantico.

Perché la soglia di default (`0.6`) non viene abbassata per "prendere più typo": con keyword corte
(3-5 lettere) la trigram similarity è rumorosa — `word_similarity('plan', 'plant')` vale `0.8` e
`word_similarity('sky', 'skip')` vale `0.5` — quindi abbassare la soglia farebbe entrare molte
parole solo vagamente somiglianti. Anche a `0.6`, un typo importante come `word_similarity('recieve',
'receive')` (`0.375`) non viene comunque catturato dalla parte fuzzy: quel caso, se serve, va
trovato via `query` (ricerca semantica) invece che via `keyword`.

Nota sul punteggio restituito all'agente: `MemorySearchResultDto.Score` per questo percorso è
sempre `1.0` — il ranking interno per `word_similarity` esiste, ma il valore numerico non è
esposto nel risultato (a differenza della ricerca semantica, dove `Score` è la cosine similarity
reale).

## 3. Filtro/elenco per categoria (`category`)

`category` è una colonna opzionale su `memories` (`VARCHAR(100)`, vedi
[MemoryConfiguration.cs:16](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryConfiguration.cs#L16)),
assegnabile al momento del salvataggio con `add_memory`. Ha due ruoli diversi:

- **Come filtro** (insieme a `query` o `keyword`): restringe l'insieme dei candidati prima dello
  scoring, tramite `.Where(m => m.Category == category)` in entrambi i repository.
- **Come unico criterio**: `MemoryRepository.ListByCategoryAsync`
  ([MemoryRepository.cs:69-78](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L69-L78))
  elenca le memorie attive di quella categoria ordinate per `CreatedAt` decrescente — nessun
  punteggio di rilevanza, è un semplice elenco filtrato e paginato dal `topK` (`Score` in questo
  caso è anch'esso fisso a `1.0`).

C'è anche un indice composito `(SpaceId, Category)` (non GIN) per rendere efficiente sia il filtro
combinato che l'elenco puro per categoria.

## Riepilogo dei tre percorsi

| Criterio | Genera embedding? | Motore di match | Ranking | `category` |
| --- | --- | --- | --- | --- |
| `query` | Sì (`IEmbeddingProvider`) | cosine distance in Postgres (`<=>`, indice HNSW su `halfvec`) | per similarità (score reale) | filtro `AND` nella query |
| `keyword` (`query` assente) | No | `ILIKE` substring **OR** `pg_trgm` word similarity, via indice GIN trigram | per `word_similarity` decrescente poi `CreatedAt` | filtro `AND` sui candidati |
| `category` (unico criterio) | No | uguaglianza esatta su `Category` | nessuno, solo `CreatedAt` decrescente | è il criterio stesso |

## Nota collegata: correlazioni via graph memory

Indipendentemente dal percorso usato, per i primi `RelatedMemoriesTopMatches` (3) risultati
`SearchMemoryAsync` allega anche `RelatedMemories` — memorie collegate via `MemoryEdge`
(`Updates`/`Extends`/`Derives`) recuperate con una traversal a profondità massima 2
(`RelatedMemoriesMaxHops`). Questo è ortogonale ai tre criteri di ricerca sopra: si applica sempre,
a valle dello scoring/filtro scelto. Approfondimento in
[graph-memory-plan.md](graph-memory-plan.md).

---

# How search works: semantic, category, and fuzzy keyword matching

*(English translation of the section above.)*

`search_memory` (`MemoryService.SearchMemoryAsync`, in
[MemoryService.cs](../src/MemoryMcp.Application/Memories/MemoryService.cs)) accepts three
criteria — `query`, `keyword`, `category` — and requires **at least one**; if all three are blank
the call fails with `ArgumentException("Provide at least one of query, keyword, or category.")`.
The behavior is not a simple AND across the three filters: there's a fixed precedence between the
first two, and `category` behaves differently depending on whether it's used alone or combined.

![Sketchnote of the search logic: query → keyword → category, with precedence, filters, and ranking for each path](search-logic-sketchnote.svg)

## Evaluation order

```csharp
if (!string.IsNullOrWhiteSpace(query))        // 1. semantic search (+ optional category)
else if (!string.IsNullOrWhiteSpace(keyword)) // 2. keyword/fuzzy search (+ optional category)
else if (!string.IsNullOrWhiteSpace(category)) // 3. category listing only
else throw new ArgumentException(...);
```

- If `query` is present, **`keyword` is ignored entirely** — there's no merging of the two scores.
  To search by keyword only, `query` must be omitted.
- `category`, when present alongside `query` or `keyword`, filters the candidates (`AND`): it
  narrows the set over which similarity/matching is computed, it isn't a separate ranking
  criterion.
- If `category` is the only criterion given, a third path (`ListByCategoryAsync`) is used, which
  does no scoring at all: it simply lists that category's active memories, ordered by creation
  date descending.

All three paths filter on `SpaceId` (the active space, or the one named by `containerTag`) and on
`IsActive` (memories that were "forgotten" or superseded by an `Updates` relation are never
returned). The maximum number of results is fixed: `SearchTopK = 10` (a private constant in
`MemoryService`, not configurable per call).

## 1. Semantic search (`query`)

`MemoryRepository.SearchAsync` ([MemoryRepository.cs:25](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L25)):

1. `query` is turned into a vector by `IEmbeddingProvider.EmbedAsync` — a pluggable provider
   (OpenAI, Azure OpenAI, or Gemini, all through the same OpenAI SDK `EmbeddingClient`, pointed at
   a different base URL for Gemini). The width is schema-bound (`VectorSettings.Dimensions` = `3072`):
   `Embeddings:Dimensions` must match it, or startup fails.
2. The embedding is stored in a `pgvector` `halfvec(3072)` column on the `memories` row, with an
   **HNSW** index on cosine distance.
3. `SearchAsync` runs the KNN **inside Postgres**: it orders by `Embedding <=> :query` while filtering
   on `SpaceId`, `IsActive`, `Embedding IS NOT NULL` and, if given, `Category`, with `LIMIT topK`. The
   query projects only ids and distances — embeddings never cross the wire — and only the winning rows
   are then loaded as tracked entities.

> `halfvec` rather than `vector`: pgvector's HNSW indexes cap the `vector` type at 2000 dimensions,
> and these embeddings are 3072-wide. Theory, implementation and operational notes in
> [pgvector-halfvec-search.md](pgvector-halfvec-search.md).

The `Score` returned in `MemorySearchResultDto` for this path is cosine similarity (0–1), derived from
the distance Postgres returns as `1 - distance`.

## 2. Keyword search with fuzzy matching (`keyword`)

`MemoryRepository.SearchByKeywordAsync` ([MemoryRepository.cs:46-67](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L46-L67))
generates no embedding: it combines two OR'd conditions on the `Text` column:

```csharp
.Where(m => m.SpaceId == spaceId && m.IsActive &&
    (EF.Functions.ILike(m.Text, $"%{keyword}%") ||
     EF.Functions.TrigramsAreWordSimilar(keyword, m.Text)))
```

- **Exact substring**, case-insensitive: `ILIKE '%keyword%'`.
- **Fuzzy/typo-tolerant**: the Postgres `pg_trgm` extension, via `word_similarity(keyword, text)
  >= pg_trgm.word_similarity_threshold` (the extension's default threshold, `0.6`, not overridden
  at the application level). `pg_trgm` breaks strings into character trigrams and measures their
  overlap — which is why it tolerates typos, not just alternate word forms.
- Both conditions are served by the same **GIN trigram** index on `memories.text`
  (`gin_trgm_ops`, defined in
  [MemoryConfiguration.cs:32-34](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryConfiguration.cs#L32-L34)),
  created by the `AddTrigramKeywordSearch` migration. The `pg_trgm` extension is registered in
  `MemoryDbContext.OnModelCreating` (`modelBuilder.HasPostgresExtension("pg_trgm")`) — unlike
  `pgvector`, it needs no admin-restricted native extension, so it works even in this constrained
  environment.
- Results are ordered by `word_similarity` descending (exact/close matches surface before purely
  fuzzy ones), then by creation date descending as a tiebreaker.
- `category`, if given, filters further (`AND`), exactly as in the semantic path.

Why the default threshold (`0.6`) isn't lowered to "catch more typos": for short keywords (3-5
letters) trigram similarity is noisy — `word_similarity('plan', 'plant')` is `0.8` and
`word_similarity('sky', 'skip')` is `0.5` — so lowering the threshold would let in many words that
are only vaguely similar. Even at `0.6`, a meaningful typo like `word_similarity('recieve',
'receive')` (`0.375`) still isn't caught by the fuzzy branch: if that case matters, it should be
found via `query` (semantic search) rather than `keyword`.

Note on the score returned to the agent: `MemorySearchResultDto.Score` for this path is always
`1.0` — the internal `word_similarity` ranking exists, but the numeric value isn't exposed in the
result (unlike semantic search, where `Score` is the real cosine similarity).

## 3. Category filter/listing (`category`)

`category` is an optional column on `memories` (`VARCHAR(100)`, see
[MemoryConfiguration.cs:16](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryConfiguration.cs#L16)),
assignable at save time via `add_memory`. It plays two different roles:

- **As a filter** (alongside `query` or `keyword`): narrows the candidate set before scoring, via
  `.Where(m => m.Category == category)` in both repository methods.
- **As the sole criterion**: `MemoryRepository.ListByCategoryAsync`
  ([MemoryRepository.cs:69-78](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryRepository.cs#L69-L78))
  lists that category's active memories ordered by `CreatedAt` descending — no relevance score,
  just a filtered, `topK`-capped list (`Score` here is also hardcoded to `1.0`).

There's also a composite `(SpaceId, Category)` index (not GIN) to keep both the combined filter
and the plain category listing efficient.

## Summary of the three paths

| Criterion | Generates an embedding? | Matching engine | Ranking | `category` |
| --- | --- | --- | --- | --- |
| `query` | Yes (`IEmbeddingProvider`) | cosine distance in Postgres (`<=>`, HNSW index on `halfvec`) | by similarity (real score) | `AND` filter in the query |
| `keyword` (`query` absent) | No | `ILIKE` substring **OR** `pg_trgm` word similarity, via GIN trigram index | by `word_similarity` descending, then `CreatedAt` | `AND` filter on candidates |
| `category` (sole criterion) | No | exact equality on `Category` | none, just `CreatedAt` descending | is the criterion itself |

## Related note: graph-memory correlations

Regardless of the path used, for the first `RelatedMemoriesTopMatches` (3) results,
`SearchMemoryAsync` also attaches `RelatedMemories` — memories linked via `MemoryEdge`
(`Updates`/`Extends`/`Derives`) fetched with a traversal bounded at depth 2
(`RelatedMemoriesMaxHops`). This is orthogonal to the three search criteria above: it's always
applied, downstream of whichever scoring/filter path was chosen. See
[graph-memory-plan.md](graph-memory-plan.md) for more detail.
