# Vector search with pgvector and `halfvec`

How semantic recall works after moving similarity scoring out of the application and into PostgreSQL.
Covers the theory the design rests on, the concrete implementation, and the operational consequences.

Companion documents: [come-funziona-la-ricerca.md](come-funziona-la-ricerca.md) for the end-to-end
search flow (semantic + keyword + category), [graph-memory-plan.md](graph-memory-plan.md) for the edge
traversal that enriches results.

---

## 1. The problem this replaces

Before this change, `MemoryRepository.SearchAsync` loaded **every** active memory in a space that had an
embedding, materialized them as tracked EF entities, and computed cosine similarity in C#:

```csharp
var candidates = await query.ToListAsync(cancellationToken);   // the whole space
return candidates
    .Select(m => new MemorySearchHit(m, CosineSimilarity(m.Embedding!, queryEmbedding)))
    .OrderByDescending(hit => hit.Score)
    .Take(topK)
    .ToList();
```

Three costs compounded:

1. **Bandwidth and memory.** At 3072 dimensions, a `real[]` embedding is 3072 × 4 = 12 KB. Ten thousand
   memories means ~120 MB pulled across the wire and held in the managed heap *per search*.
2. **Write path too.** `add_memory` calls the same method to find extraction candidates, so saving paid
   the full-scan cost as well.
3. **Change tracking.** Entities were loaded tracked on purpose — `ForgetAsync` mutates them and relies
   on EF to persist the soft-delete — which meant the read path carried tracking overhead for the entire
   space in order to serve one writer's needs.

Complexity was `O(n)` in the size of the space, with a large constant. The work below removes the
constant and changes the growth curve.

---

## 2. Theory

### 2.1 Embeddings and cosine similarity

An embedding maps text to a point in ℝⁿ such that semantically related texts land near one another.
"Nearness" here is **cosine similarity** — the cosine of the angle between two vectors, which ignores
magnitude and measures orientation only:

```
cos(a, b) = (a · b) / (‖a‖ ‖b‖)        ∈ [-1, 1]
```

Magnitude-invariance is what we want: a long document and a one-line note about the same topic should
match, and their vector lengths shouldn't interfere.

pgvector exposes the complementary quantity, **cosine distance**, via the `<=>` operator:

```
distance = 1 - cos(a, b)               ∈ [0, 2]
```

Distance is what an index can order by (smaller = nearer), which is why the SQL orders by `<=>` and the
repository converts back with `1.0 - distance` before returning — callers, and
`MemoryService.ForgetSimilarityThreshold`, are written in terms of similarity.

### 2.2 Exact KNN vs approximate nearest neighbour

Finding the true top-k nearest vectors requires comparing the query against all n stored vectors: exact,
but `O(n)`. That is precisely the old in-app loop, just relocated.

**ANN** (approximate nearest neighbour) trades a small, tunable amount of recall for sub-linear search.
For a memory system this is the right trade: the consumer is an LLM reading the top ~10 results, and
missing the 9th-best match occasionally costs far less than a query that scales linearly with the corpus.

### 2.3 HNSW

*Hierarchical Navigable Small World* builds a multi-layer proximity graph over the stored vectors. Each
node links to a bounded number of neighbours (`m`); upper layers are sparse and act as express lanes,
lower layers are dense and precise. A search enters at the top layer, greedily walks toward the query
vector, drops a layer, and repeats — a coarse-to-fine descent.

Search cost is roughly `O(log n)` instead of `O(n)`. Two parameters govern the trade-off:

| Parameter | When | Effect |
| --- | --- | --- |
| `m` | build | Neighbours per node. Higher = better recall, larger index, slower build. pgvector default 16 |
| `ef_construction` | build | Candidate list size while building. Higher = better graph quality, slower build. Default 64 |
| `hnsw.ef_search` | query | Candidate list size while searching. Higher = better recall, slower query. Default 40 |

We create the index with pgvector's defaults. `hnsw.ef_search` is a per-session GUC and can be raised
later without rebuilding anything, which makes recall tunable at runtime.

### 2.4 Why `halfvec` and not `vector`

This is the constraint that shapes the whole design.

pgvector's `vector` type stores 4-byte floats and supports up to 16,000 dimensions **for storage** — but
its HNSW and IVFFlat indexes cap out at **2,000 dimensions**. Our embeddings are 3072-wide
(`gemini-embedding-001` native width), so:

```sql
CREATE TABLE t (emb vector(3072));
CREATE INDEX ON t USING hnsw (emb vector_cosine_ops);
-- ERROR: column cannot have more than 2000 dimensions for hnsw index
```

Verified against the actual installation, not assumed. A `vector(3072)` column would have accepted the
data and then failed at index creation — mid-migration.

`halfvec` stores IEEE 754 **half-precision** (2-byte) floats and indexes up to **4,000 dimensions**. It
gives us two things at once:

- **Indexability at 3072 dimensions**, which `vector` cannot provide.
- **Half the storage**: 6 KB per embedding instead of 12 KB, so the index and heap both shrink.

The cost is precision. A half float carries ~3 decimal significant digits versus ~7. For *ranking* by
cosine similarity this is immaterial: embedding components are small numbers whose relative ordering
survives the rounding, and the downstream consumer is a similarity ranking, not an exact numeric
result. Where exactness matters more than throughput, the standard pattern is to index `halfvec` and
re-rank the top candidates against full-precision vectors — not implemented here, and not currently
warranted.

The alternative was truncating embeddings to 1536 dimensions via Matryoshka representation learning
(which `gemini-embedding-001` supports). Rejected: it requires re-embedding every stored memory and
manual re-normalization, and it discards half the semantic signal — a real recall cost, to avoid a
rounding error that is not.

---

## 3. Implementation

### 3.1 Schema

`memories.Embedding` is `halfvec(3072)`, indexed with HNSW under the cosine operator class:

```sql
CREATE INDEX "IX_memories_Embedding"
  ON public.memories USING hnsw ("Embedding" halfvec_cosine_ops);
```

The operator class must match the column type — `halfvec_cosine_ops`, not `vector_cosine_ops`.

Both the extension and the index are declared **on the EF model** rather than hand-written into a
migration, so the model snapshot knows they exist and a later migration cannot silently drop them:

```csharp
// MemoryDbContext.OnModelCreating
modelBuilder.HasPostgresExtension("vector");

// MemoryConfiguration
builder.HasIndex(m => m.Embedding)
    .HasMethod("hnsw")
    .HasOperators("halfvec_cosine_ops");
```

### 3.2 Keeping pgvector out of the Domain

`Memory.Embedding` stays `float[]`. `Pgvector.HalfVector` is a persistence concern and does not leak
into `MemoryMcp.Domain`, which has no EF or database dependencies by design. The conversion lives in the
EF configuration:

```csharp
var embeddingConverter = new ValueConverter<float[]?, HalfVector?>(
    v => v == null ? null : new HalfVector(Array.ConvertAll(v, f => (Half)f)),
    v => v == null ? null : Array.ConvertAll(v.ToArray(), h => (float)h));

builder.Property(m => m.Embedding)
    .HasColumnType($"halfvec({VectorSettings.Dimensions})")
    .HasConversion(embeddingConverter);
```

The float↔half conversion runs on write and on the rare entity load. It never runs in the search hot
path, because the ranking query projects ids and distances and never materializes an embedding.

### 3.3 The query

Two deliberate steps:

```csharp
var ranked = await dbContext.Database.SqlQuery<RankedRow>(
    $"""
    SELECT "Id" AS "Id", ("Embedding" <=> {queryVector}::halfvec) AS "Distance"
    FROM memories
    WHERE "SpaceId" = {spaceId}
      AND "IsActive"
      AND "Embedding" IS NOT NULL
      AND ({categoryFilter}::text IS NULL OR "Category" = {categoryFilter})
    ORDER BY "Embedding" <=> {queryVector}::halfvec
    LIMIT {topK}
    """).ToListAsync(cancellationToken);
```

Then the winning ids are loaded as tracked entities. The split matters:

- **The KNN never transfers embeddings.** Only `(Guid, double)` pairs cross the wire.
- **Tracking is confined to `topK` rows** instead of the whole space, which is what let `ForgetAsync`
  keep working unchanged while the read path stopped paying for it.

Raw parameterized SQL rather than LINQ follows the precedent already set by
`MemoryEdgeRepository.GetRelatedAsync`, which uses `Database.SqlQuery<T>` for its recursive CTE. String
interpolation here is EF Core's `FormattableString` overload: every `{…}` becomes a bound parameter, not
concatenated text.

### 3.4 One authoritative dimension

EF migrations are generated at design time, so a fixed-width column cannot take its width from runtime
configuration. `VectorSettings.Dimensions` is therefore the single source of truth, and
`EmbeddingOptionsValidator` fails startup if `Embeddings:Dimensions` disagrees with it:

```
Embeddings:Dimensions is 1536 but the schema stores halfvec(3072). Changing the width requires
a migration and a re-embed of every stored memory — see VectorSettings.
```

This closes a real defect. The database previously held **mixed-width embeddings** (153 rows at 1536,
84 at 3072) left over from a provider switch. The old in-app loop iterated `a.Length` while indexing
into `b`, so a 1536-wide stored vector scored against a 3072-wide query produced a silent, meaningless
similarity computed on half the query — no exception, just wrong rankings. A fixed-width column makes
that state unrepresentable, and the validator catches the misconfiguration before it can produce data.

### 3.5 Centralized provider configuration

`UseVector()` registers Npgsql's pgvector type handlers. Without it the model fails validation outright
(*"could not be mapped to the database type 'halfvec(3072)'"*), so every path that constructs a
`MemoryDbContext` must apply it. Rather than repeat it, there is one extension:

```csharp
public static DbContextOptionsBuilder UseMemoryMcpNpgsql(
    this DbContextOptionsBuilder builder, string connectionString) =>
    builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
```

used by both the DI registration and the integration-test fixture.

---

## 4. Verification

**The index is used when it should be.** With the current dataset (377 rows) the planner correctly
prefers a sequential scan — at that size it genuinely is cheaper. Disabling `enable_seqscan` confirms
the index is valid and usable, and the planner will switch to it as the corpus grows:

```
=== default ===                          === enable_seqscan=off ===
Limit                                    Limit
  ->  Sort                                 ->  Index Scan using "IX_memories_Embedding" on memories
        ->  Seq Scan on memories                 Filter: ("IsActive" AND ("Embedding" IS NOT NULL))
```

An HNSW index that exists but is never chosen would be worthless, so this was checked rather than
assumed.

**The `real[]` → `halfvec` conversion needed no `USING` clause.** pgvector supplies an assignment cast,
verified on a scratch table before writing the migration, which kept it free of hand-written SQL.

**Test suite:** 76 tests green (32 unit, 22 integration, 22 end-to-end). The integration tests exercise
the pgvector path directly against a real PostgreSQL.

---

## 5. Operational notes

### Requirements

- **PostgreSQL with pgvector ≥ 0.7.0.** `halfvec` was introduced in 0.7.0; the development environment
  runs 0.8.6 on PostgreSQL 17.11.
- **A stable PostgreSQL release.** pgvector binaries are built against the released ABI. A `17rc1`
  release candidate rejected the extension with *"The specified procedure could not be found"* — the
  build was fine, the server was pre-GA.
- **Superuser on the target database.** pgvector is not a *trusted* extension, so `CREATE EXTENSION
  vector` — which the migration now runs — requires superuser. **Verify this on managed Postgres before
  the first deploy**, or the Fly.io `release_command` will fail and block it.

### Changing the embedding width

No longer a configuration edit. It requires, in order: a new EF migration altering the column type, a
re-embed of every stored memory at the new width, an index rebuild, and updating
`VectorSettings.Dimensions`. Treat it as a data migration.

### Tuning recall

`hnsw.ef_search` (default 40) is a session GUC — raise it for better recall at some latency cost without
touching the index. `m` and `ef_construction` are build-time and require a rebuild.

---

## 6. Limits and possible next steps

- **No full-precision re-ranking.** Ranking is done entirely on half-precision vectors. If measured
  recall ever proves insufficient, the standard fix is to over-fetch from the index and re-rank the
  candidates against full-precision copies.
- **Filtered search.** `SpaceId`, `IsActive`, and `Category` are applied as post-index filters. On a
  highly selective filter over a large corpus, HNSW can return fewer than `topK` rows after filtering.
  pgvector's iterative index scans (0.8.0+) address this and are not enabled here.
- **Recall is unmeasured.** No benchmark compares HNSW results against exact KNN on this corpus. At the
  current data volume the planner isn't using the index anyway, so the question is not yet live.
- **The graph enrichment N+1 is unchanged.** `SearchMemoryAsync` still issues sequential round trips to
  enrich the top matches — see [TODO.md](../TODO.md).

---

# Ricerca vettoriale con pgvector e `halfvec`

*(Traduzione italiana della sezione precedente.)*

Come funziona il recall semantico dopo aver spostato il calcolo della similarità dall'applicazione a
PostgreSQL. Copre la teoria su cui poggia il design, l'implementazione concreta e le conseguenze
operative.

Documenti collegati: [come-funziona-la-ricerca.md](come-funziona-la-ricerca.md) per il flusso di
ricerca end-to-end (semantica + keyword + categoria), [graph-memory-plan.md](graph-memory-plan.md) per
la traversal degli archi che arricchisce i risultati.

---

## 1. Il problema che questo sostituisce

Prima di questa modifica, `MemoryRepository.SearchAsync` caricava **tutte** le memorie attive di uno
spazio che avessero un embedding, le materializzava come entità EF tracciate e calcolava la similarità
del coseno in C#:

```csharp
var candidates = await query.ToListAsync(cancellationToken);   // l'intero spazio
return candidates
    .Select(m => new MemorySearchHit(m, CosineSimilarity(m.Embedding!, queryEmbedding)))
    .OrderByDescending(hit => hit.Score)
    .Take(topK)
    .ToList();
```

Si sommavano tre costi:

1. **Banda e memoria.** A 3072 dimensioni un embedding `real[]` occupa 3072 × 4 = 12 KB. Diecimila
   memorie significano ~120 MB trasferiti sulla rete e tenuti nell'heap gestito *per ogni ricerca*.
2. **Anche il percorso di scrittura.** `add_memory` chiama lo stesso metodo per trovare i candidati di
   estrazione, quindi anche il salvataggio pagava il costo della scansione completa.
3. **Change tracking.** Le entità venivano caricate tracciate di proposito — `ForgetAsync` le muta e si
   affida a EF per persistere la soft-delete — il che significava che il percorso di lettura sosteneva
   l'overhead di tracking per l'intero spazio pur di servire le esigenze di un solo scrittore.

La complessità era `O(n)` nella dimensione dello spazio, con una costante elevata. Il lavoro descritto
sotto rimuove la costante e cambia la curva di crescita.

---

## 2. Teoria

### 2.1 Embedding e similarità del coseno

Un embedding mappa il testo in un punto di ℝⁿ in modo che testi semanticamente affini finiscano vicini
tra loro. Qui "vicinanza" significa **similarità del coseno** — il coseno dell'angolo tra due vettori,
che ignora la magnitudine e misura solo l'orientamento:

```
cos(a, b) = (a · b) / (‖a‖ ‖b‖)        ∈ [-1, 1]
```

L'invarianza rispetto alla magnitudine è proprio ciò che vogliamo: un documento lungo e una nota di una
riga sullo stesso argomento devono corrispondere, senza che la lunghezza dei rispettivi vettori
interferisca.

pgvector espone la quantità complementare, la **distanza del coseno**, tramite l'operatore `<=>`:

```
distanza = 1 - cos(a, b)               ∈ [0, 2]
```

La distanza è ciò su cui un indice può ordinare (più piccola = più vicina), ed è per questo che la SQL
ordina per `<=>` e il repository riconverte con `1.0 - distance` prima di restituire i risultati: i
chiamanti, e `MemoryService.ForgetSimilarityThreshold`, sono scritti in termini di similarità.

### 2.2 KNN esatto vs nearest neighbour approssimato

Trovare i veri top-k vettori più vicini richiede di confrontare la query con tutti gli n vettori
memorizzati: esatto, ma `O(n)`. È esattamente il vecchio ciclo in-app, solo spostato altrove.

L'**ANN** (approximate nearest neighbour) scambia una quantità piccola e regolabile di recall con una
ricerca sub-lineare. Per un sistema di memoria è il compromesso giusto: il consumatore è un LLM che
legge i primi ~10 risultati, e perdere occasionalmente il nono match migliore costa molto meno di una
query che scala linearmente con il corpus.

### 2.3 HNSW

*Hierarchical Navigable Small World* costruisce un grafo di prossimità multi-livello sui vettori
memorizzati. Ogni nodo si collega a un numero limitato di vicini (`m`); i livelli superiori sono radi e
fanno da corsie preferenziali, quelli inferiori sono densi e precisi. Una ricerca entra dal livello più
alto, cammina in modo greedy verso il vettore di query, scende di un livello e ripete — una discesa dal
grossolano al fine.

Il costo di ricerca è all'incirca `O(log n)` invece di `O(n)`. Due parametri governano il compromesso:

| Parametro | Quando | Effetto |
| --- | --- | --- |
| `m` | build | Vicini per nodo. Più alto = recall migliore, indice più grande, build più lenta. Default pgvector 16 |
| `ef_construction` | build | Dimensione della lista di candidati durante la costruzione. Più alto = grafo di qualità migliore, build più lenta. Default 64 |
| `hnsw.ef_search` | query | Dimensione della lista di candidati durante la ricerca. Più alto = recall migliore, query più lenta. Default 40 |

Creiamo l'indice con i default di pgvector. `hnsw.ef_search` è una GUC di sessione e può essere alzata
in seguito senza ricostruire nulla, il che rende il recall regolabile a runtime.

### 2.4 Perché `halfvec` e non `vector`

È il vincolo che dà forma all'intero design.

Il tipo `vector` di pgvector memorizza float da 4 byte e supporta fino a 16.000 dimensioni **in
archiviazione** — ma i suoi indici HNSW e IVFFlat si fermano a **2.000 dimensioni**. I nostri embedding
sono larghi 3072 (larghezza nativa di `gemini-embedding-001`), quindi:

```sql
CREATE TABLE t (emb vector(3072));
CREATE INDEX ON t USING hnsw (emb vector_cosine_ops);
-- ERROR: column cannot have more than 2000 dimensions for hnsw index
```

Verificato sull'installazione reale, non dato per scontato. Una colonna `vector(3072)` avrebbe accettato
i dati per poi fallire alla creazione dell'indice — a migrazione già in corso.

`halfvec` memorizza float IEEE 754 a **mezza precisione** (2 byte) e indicizza fino a **4.000
dimensioni**. Ci dà due cose in una volta:

- **Indicizzabilità a 3072 dimensioni**, che `vector` non può offrire.
- **Metà dello spazio**: 6 KB per embedding invece di 12 KB, quindi si riducono sia l'indice sia
  l'heap.

Il costo è la precisione. Un half float porta ~3 cifre decimali significative contro ~7. Per il
*ranking* per similarità del coseno questo è irrilevante: le componenti degli embedding sono numeri
piccoli il cui ordinamento relativo sopravvive all'arrotondamento, e il consumatore a valle è un
ranking di similarità, non un risultato numerico esatto. Dove l'esattezza conta più del throughput, il
pattern standard è indicizzare `halfvec` e fare re-ranking dei primi candidati contro i vettori a
precisione piena — non implementato qui, e al momento non necessario.

L'alternativa era troncare gli embedding a 1536 dimensioni tramite Matryoshka representation learning
(che `gemini-embedding-001` supporta). Scartata: richiede di ri-embeddare ogni memoria salvata e una
ri-normalizzazione manuale, e butta via metà del segnale semantico — un costo reale in recall, per
evitare un errore di arrotondamento che reale non è.

---

## 3. Implementazione

### 3.1 Schema

`memories.Embedding` è `halfvec(3072)`, indicizzato con HNSW sotto la operator class del coseno:

```sql
CREATE INDEX "IX_memories_Embedding"
  ON public.memories USING hnsw ("Embedding" halfvec_cosine_ops);
```

La operator class deve corrispondere al tipo della colonna — `halfvec_cosine_ops`, non
`vector_cosine_ops`.

Sia l'estensione sia l'indice sono dichiarati **sul modello EF** invece che scritti a mano in una
migrazione, così lo snapshot del modello sa che esistono e una migrazione successiva non può
eliminarli silenziosamente:

```csharp
// MemoryDbContext.OnModelCreating
modelBuilder.HasPostgresExtension("vector");

// MemoryConfiguration
builder.HasIndex(m => m.Embedding)
    .HasMethod("hnsw")
    .HasOperators("halfvec_cosine_ops");
```

### 3.2 Tenere pgvector fuori dal Domain

`Memory.Embedding` resta `float[]`. `Pgvector.HalfVector` è una preoccupazione di persistenza e non
trapela in `MemoryMcp.Domain`, che per scelta di design non ha dipendenze da EF o dal database. La
conversione vive nella configurazione EF:

```csharp
var embeddingConverter = new ValueConverter<float[]?, HalfVector?>(
    v => v == null ? null : new HalfVector(Array.ConvertAll(v, f => (Half)f)),
    v => v == null ? null : Array.ConvertAll(v.ToArray(), h => (float)h));

builder.Property(m => m.Embedding)
    .HasColumnType($"halfvec({VectorSettings.Dimensions})")
    .HasConversion(embeddingConverter);
```

La conversione float↔half viene eseguita in scrittura e nel raro caricamento dell'entità. Non viene mai
eseguita nel percorso caldo della ricerca, perché la query di ranking proietta id e distanze e non
materializza mai un embedding.

### 3.3 La query

Due passaggi deliberati:

```csharp
var ranked = await dbContext.Database.SqlQuery<RankedRow>(
    $"""
    SELECT "Id" AS "Id", ("Embedding" <=> {queryVector}::halfvec) AS "Distance"
    FROM memories
    WHERE "SpaceId" = {spaceId}
      AND "IsActive"
      AND "Embedding" IS NOT NULL
      AND ({categoryFilter}::text IS NULL OR "Category" = {categoryFilter})
    ORDER BY "Embedding" <=> {queryVector}::halfvec
    LIMIT {topK}
    """).ToListAsync(cancellationToken);
```

Poi gli id vincenti vengono caricati come entità tracciate. La separazione conta:

- **Il KNN non trasferisce mai embedding.** Sulla rete passano solo coppie `(Guid, double)`.
- **Il tracking è confinato a `topK` righe** invece che all'intero spazio, ed è ciò che ha permesso a
  `ForgetAsync` di continuare a funzionare invariato mentre il percorso di lettura smetteva di pagarne
  il prezzo.

La SQL parametrizzata grezza al posto di LINQ segue il precedente già stabilito da
`MemoryEdgeRepository.GetRelatedAsync`, che usa `Database.SqlQuery<T>` per la sua CTE ricorsiva.
L'interpolazione di stringa qui è l'overload `FormattableString` di EF Core: ogni `{…}` diventa un
parametro bound, non testo concatenato.

### 3.4 Una sola dimensione autorevole

Le migrazioni EF sono generate a design time, quindi una colonna a larghezza fissa non può prendere la
propria larghezza dalla configurazione a runtime. `VectorSettings.Dimensions` è perciò l'unica fonte di
verità, e `EmbeddingOptionsValidator` fa fallire lo startup se `Embeddings:Dimensions` non concorda con
essa:

```
Embeddings:Dimensions is 1536 but the schema stores halfvec(3072). Changing the width requires
a migration and a re-embed of every stored memory — see VectorSettings.
```

Questo chiude un difetto reale. Il database conteneva in precedenza **embedding di larghezza mista**
(153 righe a 1536, 84 a 3072) rimasti da un cambio di provider. Il vecchio ciclo in-app iterava su
`a.Length` mentre indicizzava dentro `b`, quindi un vettore memorizzato largo 1536 confrontato con una
query larga 3072 produceva una similarità silenziosa e priva di significato calcolata su metà della
query — nessuna eccezione, solo ranking sbagliati. Una colonna a larghezza fissa rende quello stato
non rappresentabile, e il validator intercetta la misconfigurazione prima che possa produrre dati.

### 3.5 Configurazione centralizzata del provider

`UseVector()` registra i type handler pgvector di Npgsql. Senza di esso il modello fallisce del tutto la
validazione (*"could not be mapped to the database type 'halfvec(3072)'"*), quindi ogni percorso che
costruisce un `MemoryDbContext` deve applicarlo. Invece di ripeterlo, c'è un'unica extension:

```csharp
public static DbContextOptionsBuilder UseMemoryMcpNpgsql(
    this DbContextOptionsBuilder builder, string connectionString) =>
    builder.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
```

usata sia dalla registrazione DI sia dalla fixture dei test di integrazione.

---

## 4. Verifica

**L'indice viene usato quando deve.** Con il dataset attuale (377 righe) il planner preferisce
correttamente una scansione sequenziale — a quella dimensione è davvero più economica. Disabilitare
`enable_seqscan` conferma che l'indice è valido e utilizzabile, e il planner passerà a usarlo man mano
che il corpus cresce:

```
=== default ===                          === enable_seqscan=off ===
Limit                                    Limit
  ->  Sort                                 ->  Index Scan using "IX_memories_Embedding" on memories
        ->  Seq Scan on memories                 Filter: ("IsActive" AND ("Embedding" IS NOT NULL))
```

Un indice HNSW che esiste ma non viene mai scelto sarebbe inutile, perciò questo è stato verificato
invece che dato per scontato.

**La conversione `real[]` → `halfvec` non ha richiesto alcuna clausola `USING`.** pgvector fornisce un
cast di assegnazione, verificato su una tabella di prova prima di scrivere la migrazione, il che l'ha
mantenuta libera da SQL scritta a mano.

**Suite di test:** 76 test verdi (32 unit, 22 di integrazione, 22 end-to-end). I test di integrazione
esercitano il percorso pgvector direttamente contro un PostgreSQL reale.

---

## 5. Note operative

### Requisiti

- **PostgreSQL con pgvector ≥ 0.7.0.** `halfvec` è stato introdotto nella 0.7.0; l'ambiente di sviluppo
  usa la 0.8.6 su PostgreSQL 17.11.
- **Una release stabile di PostgreSQL.** I binari di pgvector sono compilati contro l'ABI rilasciata.
  Una release candidate `17rc1` ha rifiutato l'estensione con *"The specified procedure could not be
  found"* — la build era corretta, il server era pre-GA.
- **Superuser sul database di destinazione.** pgvector non è un'estensione *trusted*, quindi `CREATE
  EXTENSION vector` — che ora la migrazione esegue — richiede il superuser. **Verificarlo su Postgres
  gestito prima del primo deploy**, altrimenti il `release_command` di Fly.io fallirà bloccandolo.

### Cambiare la larghezza degli embedding

Non è più una modifica di configurazione. Richiede, nell'ordine: una nuova migrazione EF che alteri il
tipo della colonna, un re-embed di ogni memoria salvata alla nuova larghezza, una ricostruzione
dell'indice e l'aggiornamento di `VectorSettings.Dimensions`. Va trattata come una migrazione di dati.

### Regolare il recall

`hnsw.ef_search` (default 40) è una GUC di sessione — alzarla per un recall migliore a fronte di un
certo costo di latenza, senza toccare l'indice. `m` ed `ef_construction` sono di build time e
richiedono una ricostruzione.

---

## 6. Limiti e possibili passi successivi

- **Nessun re-ranking a precisione piena.** Il ranking avviene interamente su vettori a mezza
  precisione. Se il recall misurato dovesse mai rivelarsi insufficiente, la correzione standard è
  sovra-recuperare dall'indice e fare re-ranking dei candidati contro copie a precisione piena.
- **Ricerca filtrata.** `SpaceId`, `IsActive` e `Category` sono applicati come filtri post-indice. Con
  un filtro molto selettivo su un corpus grande, HNSW può restituire meno di `topK` righe dopo il
  filtraggio. Gli iterative index scan di pgvector (0.8.0+) risolvono questo problema e qui non sono
  abilitati.
- **Il recall non è misurato.** Nessun benchmark confronta i risultati HNSW con il KNN esatto su questo
  corpus. All'attuale volume di dati il planner non sta comunque usando l'indice, quindi la questione
  non è ancora attuale.
- **L'N+1 dell'arricchimento a grafo è invariato.** `SearchMemoryAsync` continua a emettere round trip
  sequenziali per arricchire i match migliori — vedi [TODO.md](../TODO.md).
