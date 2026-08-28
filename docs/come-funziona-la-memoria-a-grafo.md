# Come funziona la memoria a grafo

Memory-MCP non salva il contenuto ricevuto come un blocco opaco da recuperare per similarità: lo
**scompone in fatti atomici** e **collega** ciascun fatto alle memorie già presenti nello spazio con
archi tipizzati (`Updates`, `Extends`, `Derives`). Il recupero poi non restituisce solo i top match
della ricerca, ma anche il loro vicinato nel grafo. Questo è ciò che distingue il sistema da un RAG
piatto: la storia di un fatto (cosa ha superato cosa, cosa aggiunge dettaglio a cosa) è
rappresentata esplicitamente e navigabile.

Il disegno originale della feature è in [graph-memory-plan.md](graph-memory-plan.md); le correzioni
applicate dopo la prima implementazione sono in
[graph-memory-code-review.md](graph-memory-code-review.md). Questo documento descrive **come si
comporta il codice oggi**.

## Modello dati

Due tabelle, entrambe in PostgreSQL, nessun graph engine dedicato:

| Tabella | Ruolo nel grafo |
| --- | --- |
| `memories` | i **nodi**: un fatto atomico per riga, con testo, `Category`, embedding `halfvec(3072)`, `Version`, `IsActive`, `SupersededBy` |
| `memory_edges` | gli **archi**: `Id`, `SpaceId`, `FromMemoryId`, `ToMemoryId`, `RelationType`, `Note?`, `CreatedAt` |

`MemoryEdge` ([MemoryEdge.cs](../src/MemoryMcp.Domain/MemoryEdge.cs)) è immutabile: nessun metodo di
modifica, nessuna operazione "dimentica un arco". Si dimentica una *memoria*
(`Memory.Forget()`), mai una relazione.

**Convenzione di direzione: `From` agisce su `To`.** `Updates` significa che la memoria `From`
supera la memoria `To`. Poiché gli archi vengono creati sempre dal fatto *nuovo* verso la memoria
*preesistente*, in pratica `From` è sempre il più recente dei due.

I tre tipi di relazione ([RelationType.cs](../src/MemoryMcp.Domain/RelationType.cs)):

- **`Updates`** — il fatto nuovo contraddice o sostituisce quello vecchio (la storia resta, per audit).
- **`Extends`** — il fatto nuovo aggiunge dettaglio senza invalidare il vecchio.
- **`Derives`** — il fatto è inferito combinando due o più memorie esistenti.

Configurazione EF in
[MemoryEdgeConfiguration.cs](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryEdgeConfiguration.cs)
(migrazione `AddMemoryEdges`): indici compositi `(SpaceId, FromMemoryId)` e `(SpaceId, ToMemoryId)`
— uno per ciascuna direzione di traversal — e FK verso `spaces` e due volte verso `memories`, tutte
con `DeleteBehavior.Cascade`.

### `Note`: la motivazione dell'arco

`Note` (`VARCHAR(500)`, nullable) contiene **la motivazione in linguaggio naturale del perché
l'arco esiste**: perché l'extractor ha classificato la relazione in quel modo. È un aiuto di
audit/debug, non un dato interrogabile — è l'autogiustificazione del modello, senza indice e in
forma libera. Serve soprattutto in tre casi:

- **Audit di un `Updates` distruttivo.** Un `Updates` sopra soglia disattiva una memoria: senza la
  nota resta traccia del *cosa* (`SupersededBy`, l'arco, `IsActive=false`) ma non del *perché*.
- **Distinguere `Updates` da `Extends`**, la classificazione più fragile: la motivazione è il
  segnale che serve a capire, sui dati reali, se il prompt sta sbagliando sistematicamente.
- **`Derives`**, dove dall'arco non si vede *quali* memorie sono state combinate per inferire il fatto.

Il prompt di sistema chiede una frase breve (al massimo ~300 caratteri, nella lingua del contenuto)
che dica cosa il fatto contraddice, quale dettaglio aggiunge o da quali candidati è stato inferito.
Nulla nel protocollo garantisce che il modello rispetti quel limite, quindi il **clamp vive
nell'entità**: `MemoryEdge` normalizza la nota nel costruttore (spazi tolti, stringhe vuote → `null`,
troncamento a `MemoryEdge.NoteMaxLength` con `…`). Il clamp c'è perché una nota troppo lunga
farebbe fallire l'intero `SaveChangesAsync` — cioè tutto il salvataggio, non solo quell'arco —
essendo fuori dal `try/catch` che copre l'estrazione.

Cosa **non** va messo in `Note`: dati strutturati su cui si vuole filtrare (lo score di similarità
al momento della creazione, un flag "ha causato il forget", il nome del modello). Quelli
richiederebbero colonne dedicate.

### Rapporto con `SupersededBy`/`IsActive`/`Version`

I campi preesistenti su `Memory` non sono stati rimossi: `memory_edges` li **generalizza**. Quando
un salvataggio produce una relazione `Updates` (e supera la soglia di confidenza, vedi sotto),
`MemoryService` fa entrambe le cose — inserisce l'arco *e* chiama
`Memory.Forget(supersededBy: nuovoId)` sulla memoria vecchia. Così tutti i filtri `IsActive` già
esistenti in `SearchAsync`/`SearchByKeywordAsync`/`ListByCategoryAsync` continuano a funzionare senza
modifiche, e `SupersededBy` resta un puntatore di comodo per il caso singolo più comune.

## Scrittura: cosa succede a ogni `add_memory` (save)

`MemoryService.SaveAsync`
([MemoryService.cs:112-192](../src/MemoryMcp.Application/Memories/MemoryService.cs#L112-L192)):

1. **Documento sorgente**: viene creato un `Document` (`docType: "note"`) con il contenuto grezzo,
   subito marcato come processato. Tutti i fatti estratti punteranno a questo `DocumentId`, quindi
   il contenuto verbatim non va perso anche se le memorie salvate sono riformulate.
2. **Embedding del contenuto**: una sola chiamata a `IEmbeddingProvider.EmbedAsync(content)`, usata
   sia per cercare i candidati che (riusata) per l'eventuale fatto identico al contenuto.
3. **Candidati per il collegamento**: `memoryRepository.SearchAsync` con
   `ExtractionCandidateTopK = 5`, **filtrato sulla `category` passata dal chiamante** — le memorie
   attive più simili semanticamente. Sono le uniche memorie a cui i nuovi fatti potranno essere
   collegati: il grafo si costruisce solo dentro il vicinato semantico, non su tutto lo spazio.
4. **Estrazione dei fatti**: `IFactExtractor.ExtractAsync(content, candidates)`. In
   [LlmFactExtractor.cs](../src/MemoryMcp.Infrastructure/Extraction/LlmFactExtractor.cs) è una
   chiamata chat con **structured output (JSON Schema, `strict: true`)** che restituisce
   `{ facts: [{ text, category, relations: [{ existingMemoryId, relationType }] }] }` — schema
   vincolato invece di parsing testuale fragile. Il prompt di sistema istruisce a produrre
   affermazioni atomiche e autoconsistenti, a classificare la relazione con ciascun candidato e a
   **non inventare id**. Gli id non parsabili o i `relationType` fuori enum vengono scartati
   silenziosamente in `ToExtractedFact`.
5. **Embedding dei fatti**: `EmbedFactTextsAsync` fa **una sola** chiamata batch
   (`EmbedBatchAsync`) per tutti i fatti, e riusa l'embedding del punto 2 per il fatto il cui testo
   coincide esattamente col contenuto. Se il provider restituisce un numero di vettori diverso dal
   numero di testi, si lancia un errore esplicito invece di accoppiare per posizione vettori
   sbagliati (che avvelenerebbe silenziosamente la ricerca).
6. **Nodi e archi**: per ogni fatto si crea una `Memory` (categoria del fatto, con fallback su
   quella del chiamante) e per ogni relazione un `MemoryEdge(spaceId, fattoNuovo, memoriaEsistente,
   tipo, nota)` — dove `nota` è la motivazione restituita dall'extractor per quella relazione. Una
   relazione che punta a un id **non presente tra i candidati** (tipicamente un'allucinazione) viene
   ignorata, per non rischiare una violazione di foreign key.
7. **Auto-forget con guardia di confidenza**: solo se `RelationType == Updates` **e** il punteggio
   di similarità del candidato è `>= ForgetSimilarityThreshold` (`0.8`, la stessa soglia richiesta a
   un `forget` esplicito) la memoria vecchia viene disattivata. Sotto soglia **l'arco viene creato
   comunque**, ma la memoria resta attiva: una classificazione errata dell'LLM non può cancellare
   silenziosamente nulla. Il messaggio di risposta riporta quante memorie sono state superate.
8. **Un solo `SaveChangesAsync`** in coda: nodi, archi e disattivazioni sono atomici.

### Degradazione quando l'estrazione non è disponibile

Il blocco dei punti 3-4 è dentro un `try/catch` che intercetta **qualsiasi** eccezione non di
cancellazione: extractor non configurato (`ExtractorNotConfiguredException`, sollevata quando
`Extraction:ApiKey` è vuoto), timeout, rate limit, JSON malformato, risposta filtrata. In quel caso
— e anche quando l'estrazione riesce ma restituisce zero fatti — si ricade sul comportamento
pre-grafo: **il contenuto intero salvato come singola memoria, zero archi**. Una chiamata che prima
riusciva sempre non può iniziare a fallire per colpa del grafo.

Configurazione (sezione `Extraction`, vedi
[ExtractionOptions.cs](../src/MemoryMcp.Infrastructure/Extraction/ExtractionOptions.cs)):
`Provider` (`OpenAI` di default, oppure `AzureOpenAI` o `Gemini`), `ApiKey`, `Endpoint` (obbligatorio
per Azure, opzionale per Gemini o per un modello self-hosted OpenAI-compatibile tipo Ollama / vLLM /
LM Studio), `Model` (`gpt-4o-mini` di default). Il `ChatClient` è registrato come
`Lazy<ChatClient>` in modalità `PublicationOnly`: viene costruito solo al primo uso reale e un
fallimento transitorio non viene messo in cache per tutta la vita del processo.

> `add_memory` con `action: forget` **non** crea archi: `ForgetAsync` disattiva le memorie sopra la
> soglia `0.8` con `Memory.Forget()` senza `supersededBy`, esattamente come prima del grafo.

## Lettura: `relatedMemories` sui risultati di ricerca

Qualunque sia il percorso di ricerca scelto (semantico, keyword o categoria — vedi
[come-funziona-la-ricerca.md](come-funziona-la-ricerca.md)), a valle dello scoring
`SearchMemoryAsync` arricchisce i **primi `RelatedMemoriesTopMatches` (3)** risultati con una
traversal a `RelatedMemoriesMaxHops` (**2**) salti
([MemoryService.cs:56-63](../src/MemoryMcp.Application/Memories/MemoryService.cs#L56-L63)). I match
successivi al terzo non vengono arricchiti: il costo (due query ricorsive + un'idratazione per
match) è pagato solo dove serve.

`MemoryGraphService.GetRelatedAsync`
([MemoryGraphService.cs:7-23](../src/MemoryMcp.Application/Memories/MemoryGraphService.cs#L7-L23))
fa due cose:

1. chiama `IMemoryEdgeRepository.GetRelatedAsync` per ottenere gli **id** raggiungibili con il tipo
   di relazione, il numero di salti e la direzione;
2. idrata il testo con `memoryRepository.GetByIdsAsync(spaceId, ids)` — **filtrato per `SpaceId`**,
   quindi un nodo di un altro spazio raggiunto dagli archi verrebbe scartato dal successivo
   `Where(byId.ContainsKey(...))`; è la barriera di isolamento tra spazi del percorso di lettura del
   grafo.

`GetByIdsAsync` **non** filtra su `IsActive`, deliberatamente: una memoria superata è ancora
contesto utile ("prima era così"). Per questo `RelatedMemoryDto` porta un flag `IsActive`, così il
client distingue una relazione corrente da una storica.

### La traversal: CTE ricorsiva bidirezionale

In questo ambiente non è disponibile nessuna estensione di grafo (stesso vincolo che ha portato a
`halfvec` per i vettori), quindi la traversal è **SQL Postgres puro**:
`MemoryEdgeRepository.GetRelatedAsync`
([MemoryEdgeRepository.cs](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryEdgeRepository.cs))
esegue due `WITH RECURSIVE` — una in avanti, una a specchio con `From`/`To` invertiti — tramite
`Database.SqlQuery<T>` (interpolazione parametrizzata di EF Core, non concatenazione di stringhe).

```sql
WITH RECURSIVE graph(to_id, relation_type, note, hops, path) AS (
    SELECT "ToMemoryId", "RelationType", "Note", 1, ARRAY["FromMemoryId", "ToMemoryId"]
    FROM memory_edges WHERE "FromMemoryId" = {rootMemoryId}
    UNION ALL
    SELECT e."ToMemoryId", e."RelationType", e."Note", g.hops + 1, g.path || e."ToMemoryId"
    FROM memory_edges e
    JOIN graph g ON e."FromMemoryId" = g.to_id
    WHERE g.hops < {maxHops} AND e."ToMemoryId" <> ALL(g.path)
)
SELECT to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops",
       MIN(note) FILTER (WHERE hops = 1) AS "Note"
FROM graph GROUP BY to_id, relation_type
```

Quattro dettagli che contano:

- **`path` come array di nodi visitati**: `e."ToMemoryId" <> ALL(g.path)` garantisce la terminazione
  su cicli, indipendentemente dal limite di salti.
- **`MIN(hops)` con `GROUP BY`**: se lo stesso nodo è raggiungibile per più cammini con la stessa
  relazione, si tiene la distanza minima e si deduplica.
- **Due direzioni, non una.** Gli archi puntano sempre dal fatto nuovo alla memoria vecchia, quindi
  una ricerca che atterra sulla memoria *vecchia* — il caso più frequente, perché è quella con più
  storia — seguendo solo gli archi uscenti non troverebbe **nulla**. La versione a specchio
  (`TraverseIncomingAsync`) risolve questo (era il primo finding della code review).
- **La nota solo a un salto** (`MIN(note) FILTER (WHERE hops = 1)`): una nota descrive *un* arco,
  mentre un risultato a due salti è una catena di archi collassata sul cammino più corto. Oltre il
  primo salto non esiste un singolo arco a cui attribuirla, quindi resta `null` invece di mostrare
  arbitrariamente la motivazione dell'ultimo arco percorso.

Ogni risultato è etichettato con `RelatedMemoryDirection`:

| Direzione | Significato con `Updates` |
| --- | --- |
| `Outgoing` | la radice **aggiorna** questa memoria (la radice è la più recente) |
| `Incoming` | questa memoria **aggiorna** la radice (la radice è quella superata) |

La distinzione è necessaria per presentare correttamente la relazione: lo stesso `RelationType`
richiede frasi opposte a seconda del verso da cui lo si guarda.

## Vista d'insieme: il grafo dello spazio

Accanto al vicinato di una singola memoria esiste una vista sull'intero spazio,
`MemoryGraphService.GetSpaceGraphAsync`
([MemoryGraphService.cs:25-37](../src/MemoryMcp.Application/Memories/MemoryGraphService.cs#L25-L37)):

1. prende le **`maxNodes` (default 50) memorie più recenti** dello spazio, **di qualsiasi stato**
   (`ListAsync` non filtra `IsActive`, così le memorie superate restano visibili nel grafo);
2. carica **tutti** gli archi dello spazio (`ListEdgesAsync`) e ne tiene solo quelli con **entrambi**
   gli estremi nella finestra dei nodi caricati.

Conseguenza da tenere presente: un arco verso una memoria più vecchia della cinquantesima **non
viene mostrato**, perché non avrebbe un nodo da cui/verso cui essere disegnato. La vista d'insieme è
una finestra recente, non il grafo completo.

È esposta come risorsa MCP `memory-mcp://graph`
([MemoryResources.cs:33-37](../src/MemoryMcp.Api/Resources/MemoryResources.cs#L33-L37)) e consumata
dal widget `memory-graph` ([memory-graph.html](../src/MemoryMcp.Api/Apps/ui/memory-graph.html)), che
disegna i nodi su canvas e colora gli archi per tipo di relazione: `Updates` rosso, `Extends` blu,
`Derives` verde acqua. Passando il mouse su un arco (hit-test per distanza punto-segmento, non per
raggio come sui nodi) il tooltip mostra tipo di relazione e `Note`, cioè il motivo per cui l'arco
esiste; se la nota manca — archi creati prima che venisse popolata, o extractor che non l'ha
prodotta — lo dice esplicitamente.

## Limiti attuali e comportamenti da conoscere

- **Il testo salvato è quello riformulato dall'LLM**, non il contenuto verbatim del chiamante (il
  verbatim resta nel `Document` sorgente). È il comportamento voluto della feature, ma è in tensione
  con la formulazione "saves the supplied `content`" del `CLAUDE.md` di progetto: scelta di prodotto
  lasciata aperta nella code review.
- **`Derives` è best-effort.** Inferire un fatto combinando due memorie non correlate è il caso più
  difficile ed è chiesto al modello come opzionale nel prompt: aspettarsi che compaia raramente.
- **Il grafo si costruisce solo tra vicini semantici**: i candidati sono i top 5 per similarità
  (filtrati per categoria). Due memorie correlate ma lontane nello spazio degli embedding non
  verranno mai collegate.
- **Nessuna gestione degli archi post-creazione**: non si possono correggere, ritipizzare o
  cancellare. Un arco sbagliato resta (la cancellazione avviene solo in cascata, se si elimina una
  delle due memorie o lo spazio).
- **`relatedMemories` non ha un punteggio**: porta `RelationType`, `Hops`, `Direction`, `IsActive` e
  `Note` (solo a un salto), ma nessuna misura di rilevanza — l'ordinamento è quello restituito dalle
  due traversal (uscenti poi entranti).
- **La nota è un'autogiustificazione del modello**, non una prova di correttezza: utile per il debug
  del prompt e per l'audit, non affidabile come verità sul perché due fatti sono davvero collegati.
  E gli archi creati prima di questa modifica hanno `Note` a `null`: non è ricostruibile a posteriori.
- **Le due query di traversal non filtrano su `SpaceId`**: partono dall'id di radice e l'isolamento
  è garantito a valle, in fase di idratazione del testo. Gli indici compositi includono `SpaceId`,
  quindi il filtro esplicito sarebbe anche più efficiente: nota architetturale, non un bug (le
  memorie di spazi diversi non sono comunque mai collegate da archi, dato che `SaveAsync` sceglie i
  candidati dentro un solo spazio).

## Costanti in gioco

| Costante | Valore | Dove | Effetto |
| --- | --- | --- | --- |
| `ExtractionCandidateTopK` | 5 | `MemoryService` | quante memorie esistenti l'extractor può collegare |
| `ForgetSimilarityThreshold` | 0.8 | `MemoryService` | confidenza minima perché un `Updates` disattivi la memoria vecchia |
| `RelatedMemoriesTopMatches` | 3 | `MemoryService` | quanti risultati di ricerca vengono arricchiti |
| `RelatedMemoriesMaxHops` | 2 | `MemoryService` | profondità massima della traversal |
| `maxNodes` | 50 | `MemoryGraphService.GetSpaceGraphAsync` | ampiezza della finestra nella vista d'insieme |
| `NoteMaxLength` | 500 | `MemoryEdge` | larghezza della colonna `note`, a cui il costruttore tronca |

Nessuna di queste è configurabile per chiamata o da `appsettings`.

---

# How graph memory works

*(English translation of the section above.)*

Memory-MCP doesn't store incoming content as an opaque blob to be retrieved by similarity: it
**decomposes it into atomic facts** and **links** each fact to the memories already in the space
with typed edges (`Updates`, `Extends`, `Derives`). Retrieval then returns not just the search's top
matches but also their neighborhood in the graph. That's what sets this apart from flat RAG: a
fact's history (what superseded what, what adds detail to what) is explicit and navigable.

The original feature design is in [graph-memory-plan.md](graph-memory-plan.md); the corrections
applied after the first implementation are in
[graph-memory-code-review.md](graph-memory-code-review.md). This document describes **how the code
behaves today**.

## Data model

Two tables, both in PostgreSQL, no dedicated graph engine:

| Table | Role in the graph |
| --- | --- |
| `memories` | the **nodes**: one atomic fact per row, with text, `Category`, a `halfvec(3072)` embedding, `Version`, `IsActive`, `SupersededBy` |
| `memory_edges` | the **edges**: `Id`, `SpaceId`, `FromMemoryId`, `ToMemoryId`, `RelationType`, `Note?`, `CreatedAt` |

`MemoryEdge` ([MemoryEdge.cs](../src/MemoryMcp.Domain/MemoryEdge.cs)) is immutable: no mutators, no
"forget an edge" operation. You forget a *memory* (`Memory.Forget()`), never a relation.

**Direction convention: `From` acts on `To`.** `Updates` means the `From` memory supersedes the `To`
memory. Since edges are always created from the *new* fact towards the *pre-existing* memory, in
practice `From` is always the newer of the two.

The three relation types ([RelationType.cs](../src/MemoryMcp.Domain/RelationType.cs)):

- **`Updates`** — the new fact contradicts or replaces the old one (history is kept, for audit).
- **`Extends`** — the new fact adds detail without invalidating the old one.
- **`Derives`** — the fact is inferred by combining two or more existing memories.

EF configuration in
[MemoryEdgeConfiguration.cs](../src/MemoryMcp.Infrastructure/Persistence/Configurations/MemoryEdgeConfiguration.cs)
(migration `AddMemoryEdges`): composite indexes on `(SpaceId, FromMemoryId)` and
`(SpaceId, ToMemoryId)` — one per traversal direction — and FKs to `spaces` and twice to `memories`,
all with `DeleteBehavior.Cascade`.

### `Note`: the edge's rationale

`Note` (nullable `VARCHAR(500)`) holds **a natural-language rationale for why the edge exists**: why
the extractor classified the relation the way it did. It's an audit/debugging aid, not queryable
data — it's the model's own justification, unindexed and free-form. It matters in three cases above all:

- **Auditing a destructive `Updates`.** An above-threshold `Updates` deactivates a memory: without
  the note there's a record of *what* (`SupersededBy`, the edge, `IsActive=false`) but not *why*.
- **Telling `Updates` from `Extends`**, the most fragile classification: the rationale is the signal
  needed to see, on real data, whether the prompt is systematically wrong.
- **`Derives`**, where the edge doesn't show *which* memories were combined to infer the fact.

The system prompt asks for one short sentence (at most ~300 characters, in the content's language)
stating what the fact contradicts, what detail it adds, or which candidates it was inferred from.
Nothing in the protocol enforces that limit, so the **clamp lives in the entity**: `MemoryEdge`
normalizes the note in its constructor (trimmed, blank → `null`, truncated to
`MemoryEdge.NoteMaxLength` with `…`). The clamp exists because an over-long note would fail the
entire `SaveChangesAsync` — the whole save, not just that edge — since it's outside the `try/catch`
that covers extraction.

What **doesn't** belong in `Note`: structured data you'd want to filter on (the similarity score at
creation time, a "caused the forget" flag, the model name). Those would need dedicated columns.

### Relationship to `SupersededBy`/`IsActive`/`Version`

The pre-existing `Memory` fields weren't removed — `memory_edges` **generalizes** them. When a save
produces an `Updates` relation (and clears the confidence threshold, see below), `MemoryService`
does both: it inserts the edge *and* calls `Memory.Forget(supersededBy: newId)` on the old memory.
That way every existing `IsActive` filter in
`SearchAsync`/`SearchByKeywordAsync`/`ListByCategoryAsync` keeps working unchanged, and
`SupersededBy` remains a convenience pointer for the single most common case.

## Writing: what happens on every `add_memory` (save)

`MemoryService.SaveAsync`
([MemoryService.cs:112-192](../src/MemoryMcp.Application/Memories/MemoryService.cs#L112-L192)):

1. **Source document**: a `Document` (`docType: "note"`) is created with the raw content and
   immediately marked processed. Every extracted fact points at that `DocumentId`, so the verbatim
   content isn't lost even though the saved memories are rephrased.
2. **Content embedding**: a single `IEmbeddingProvider.EmbedAsync(content)` call, used both to find
   candidates and (reused) for a fact whose text equals the content.
3. **Candidates for linking**: `memoryRepository.SearchAsync` with `ExtractionCandidateTopK = 5`,
   **filtered by the caller's `category`** — the most semantically similar active memories. These
   are the only memories the new facts can be linked to: the graph is built inside the semantic
   neighborhood, not across the whole space.
4. **Fact extraction**: `IFactExtractor.ExtractAsync(content, candidates)`. In
   [LlmFactExtractor.cs](../src/MemoryMcp.Infrastructure/Extraction/LlmFactExtractor.cs) this is a
   chat call with **structured output (JSON Schema, `strict: true`)** returning
   `{ facts: [{ text, category, relations: [{ existingMemoryId, relationType }] }] }` — a bound
   schema instead of brittle text parsing. The system prompt asks for atomic, self-contained
   statements, a relation classification against each candidate, and **never inventing ids**.
   Unparsable ids or out-of-enum `relationType`s are dropped silently in `ToExtractedFact`.
5. **Fact embeddings**: `EmbedFactTextsAsync` makes **one** batch call (`EmbedBatchAsync`) for all
   facts, reusing step 2's embedding for the fact whose text matches the content exactly. If the
   provider returns a vector count different from the text count, an explicit error is raised rather
   than positionally pairing the wrong vectors (which would silently poison search).
6. **Nodes and edges**: for each fact a `Memory` is created (fact's category, falling back to the
   caller's), and for each relation a `MemoryEdge(spaceId, newFact, existingMemory, type, note)` —
   where `note` is the rationale the extractor returned for that relation. A relation pointing at an
   id **not among the candidates** (typically a hallucination) is skipped, to avoid a foreign key
   violation.
7. **Auto-forget behind a confidence guard**: only if `RelationType == Updates` **and** the
   candidate's similarity score is `>= ForgetSimilarityThreshold` (`0.8`, the same bar an explicit
   `forget` must clear) is the old memory deactivated. Below the threshold **the edge is still
   created** but the memory stays active: a misclassification by the LLM can't silently erase
   anything. The response message reports how many memories were superseded.
8. **A single trailing `SaveChangesAsync`**: nodes, edges, and deactivations are atomic.

### Degradation when extraction is unavailable

Steps 3-4 sit inside a `try/catch` that swallows **any** non-cancellation exception: extractor not
configured (`ExtractorNotConfiguredException`, raised when `Extraction:ApiKey` is blank), timeout,
rate limit, malformed JSON, filtered response. In that case — and also when extraction succeeds but
returns zero facts — it falls back to pre-graph behavior: **the whole content saved as a single
memory, zero edges**. A call that always used to succeed can't start failing because of the graph.

Configuration (`Extraction` section, see
[ExtractionOptions.cs](../src/MemoryMcp.Infrastructure/Extraction/ExtractionOptions.cs)):
`Provider` (`OpenAI` by default, or `AzureOpenAI` or `Gemini`), `ApiKey`, `Endpoint` (required for
Azure, optional for Gemini or a self-hosted OpenAI-compatible model such as Ollama / vLLM /
LM Studio), `Model` (`gpt-4o-mini` by default). The `ChatClient` is registered as a
`Lazy<ChatClient>` in `PublicationOnly` mode: constructed only on first real use, and a transient
failure isn't cached for the process's lifetime.

> `add_memory` with `action: forget` creates **no** edges: `ForgetAsync` deactivates memories above
> the `0.8` threshold with `Memory.Forget()` and no `supersededBy`, exactly as before the graph.

## Reading: `relatedMemories` on search results

Whichever search path was taken (semantic, keyword, or category — see
[come-funziona-la-ricerca.md](come-funziona-la-ricerca.md)), downstream of scoring
`SearchMemoryAsync` enriches the **first `RelatedMemoriesTopMatches` (3)** results with a traversal
bounded at `RelatedMemoriesMaxHops` (**2**) hops
([MemoryService.cs:56-63](../src/MemoryMcp.Application/Memories/MemoryService.cs#L56-L63)). Matches
beyond the third aren't enriched: the cost (two recursive queries plus a hydration per match) is
only paid where it matters.

`MemoryGraphService.GetRelatedAsync`
([MemoryGraphService.cs:7-23](../src/MemoryMcp.Application/Memories/MemoryGraphService.cs#L7-L23))
does two things:

1. calls `IMemoryEdgeRepository.GetRelatedAsync` to get reachable **ids** with their relation type,
   hop count, and direction;
2. hydrates the text via `memoryRepository.GetByIdsAsync(spaceId, ids)` — **filtered by `SpaceId`**,
   so a node from another space reached through edges would be dropped by the following
   `Where(byId.ContainsKey(...))`; that's the space-isolation barrier on the graph read path.

`GetByIdsAsync` deliberately does **not** filter on `IsActive`: a superseded memory is still useful
context ("it used to be this"). That's why `RelatedMemoryDto` carries an `IsActive` flag, so clients
can tell a current relation from a historical one.

### The traversal: a bidirectional recursive CTE

No graph extension is available in this environment (the same constraint that led to `halfvec` for
vectors), so traversal is **plain Postgres SQL**: `MemoryEdgeRepository.GetRelatedAsync`
([MemoryEdgeRepository.cs](../src/MemoryMcp.Infrastructure/Persistence/Repositories/MemoryEdgeRepository.cs))
runs two `WITH RECURSIVE` queries — one forward, one mirrored with `From`/`To` swapped — through
`Database.SqlQuery<T>` (EF Core's parameterized interpolation, not string concatenation).

```sql
WITH RECURSIVE graph(to_id, relation_type, note, hops, path) AS (
    SELECT "ToMemoryId", "RelationType", "Note", 1, ARRAY["FromMemoryId", "ToMemoryId"]
    FROM memory_edges WHERE "FromMemoryId" = {rootMemoryId}
    UNION ALL
    SELECT e."ToMemoryId", e."RelationType", e."Note", g.hops + 1, g.path || e."ToMemoryId"
    FROM memory_edges e
    JOIN graph g ON e."FromMemoryId" = g.to_id
    WHERE g.hops < {maxHops} AND e."ToMemoryId" <> ALL(g.path)
)
SELECT to_id AS "ToId", relation_type AS "RelationType", MIN(hops) AS "Hops",
       MIN(note) FILTER (WHERE hops = 1) AS "Note"
FROM graph GROUP BY to_id, relation_type
```

Four details that matter:

- **`path` as a visited-node array**: `e."ToMemoryId" <> ALL(g.path)` guarantees termination on
  cycles, independently of the hop limit.
- **`MIN(hops)` with `GROUP BY`**: if the same node is reachable via several paths with the same
  relation, the shortest distance wins and duplicates collapse.
- **Two directions, not one.** Edges always point from the new fact to the older memory, so a search
  landing on the *older* memory — the common case, since that's the one with history — would find
  **nothing** by following outgoing edges alone. The mirrored query (`TraverseIncomingAsync`) fixes
  that (it was the code review's first finding).
- **The note only at one hop** (`MIN(note) FILTER (WHERE hops = 1)`): a note describes *one* edge,
  whereas a two-hop result is a chain of edges collapsed to its shortest path. Beyond the first hop
  there's no single edge to attribute it to, so it stays `null` instead of arbitrarily surfacing the
  last traversed edge's rationale.

Each result is tagged with a `RelatedMemoryDirection`:

| Direction | Meaning with `Updates` |
| --- | --- |
| `Outgoing` | the root **updates** this memory (the root is the newer one) |
| `Incoming` | this memory **updates** the root (the root is the superseded one) |

The distinction is needed to phrase the relation correctly: the same `RelationType` reads in
opposite directions depending on which end you're standing on.

## The whole-space view

Alongside a single memory's neighborhood there's a whole-space view,
`MemoryGraphService.GetSpaceGraphAsync`
([MemoryGraphService.cs:25-37](../src/MemoryMcp.Application/Memories/MemoryGraphService.cs#L25-L37)):

1. it takes the space's **`maxNodes` (default 50) most recent memories, in any state**
   (`ListAsync` doesn't filter `IsActive`, so superseded memories stay visible in the graph);
2. it loads **all** the space's edges (`ListEdgesAsync`) and keeps only those with **both** endpoints
   inside the loaded node window.

A consequence worth knowing: an edge to a memory older than the fiftieth **isn't shown**, because it
would have no node to be drawn from/to. The whole-space view is a recent window, not the complete
graph.

It's exposed as the MCP resource `memory-mcp://graph`
([MemoryResources.cs:33-37](../src/MemoryMcp.Api/Resources/MemoryResources.cs#L33-L37)) and consumed
by the `memory-graph` widget
([memory-graph.html](../src/MemoryMcp.Api/Apps/ui/memory-graph.html)), which draws nodes on a canvas
and colors edges by relation type: `Updates` red, `Extends` blue, `Derives` teal. Hovering an edge
(hit-tested by point-to-segment distance, not by radius as nodes are) shows a tooltip with the
relation type and its `Note` — why that edge exists; when there's no note (edges created before it
was populated, or an extractor that produced none) the tooltip says so explicitly.

## Current limits and behaviors to be aware of

- **The stored text is the LLM's rephrasing**, not the caller's verbatim content (the verbatim stays
  in the source `Document`). That's the feature's intended behavior, but it sits in tension with the
  project `CLAUDE.md`'s "saves the supplied `content`" wording — a product decision left open in the
  code review.
- **`Derives` is best-effort.** Inferring a fact by combining two unrelated memories is the hardest
  case and is asked of the model as optional in the prompt: expect it to appear rarely.
- **The graph is only built between semantic neighbors**: candidates are the top 5 by similarity
  (category-filtered). Two related memories that sit far apart in embedding space will never be
  linked.
- **No post-creation edge management**: edges can't be corrected, retyped, or deleted. A wrong edge
  stays (deletion only happens by cascade, when one of the two memories or the space is removed).
- **`relatedMemories` carries no score**: it has `RelationType`, `Hops`, `Direction`, `IsActive`, and
  `Note` (direct relations only), but no relevance measure — ordering is whatever the two traversals
  return (outgoing then incoming).
- **The note is the model's own justification**, not proof of correctness: useful for prompt debugging
  and auditing, not trustworthy as ground truth about why two facts are genuinely linked. And edges
  created before this change have a `null` `Note`: it can't be reconstructed after the fact.
- **The two traversal queries don't filter on `SpaceId`**: they start from the root id and isolation
  is enforced downstream, at text hydration. The composite indexes include `SpaceId`, so an explicit
  filter would also be more efficient: an architectural note, not a bug (memories in different
  spaces are never linked by edges anyway, since `SaveAsync` picks candidates within one space).

## Constants in play

| Constant | Value | Where | Effect |
| --- | --- | --- | --- |
| `ExtractionCandidateTopK` | 5 | `MemoryService` | how many existing memories the extractor may link to |
| `ForgetSimilarityThreshold` | 0.8 | `MemoryService` | minimum confidence for an `Updates` to deactivate the old memory |
| `RelatedMemoriesTopMatches` | 3 | `MemoryService` | how many search results get enriched |
| `RelatedMemoriesMaxHops` | 2 | `MemoryService` | maximum traversal depth |
| `maxNodes` | 50 | `MemoryGraphService.GetSpaceGraphAsync` | window width in the whole-space view |
| `NoteMaxLength` | 500 | `MemoryEdge` | width of the `note` column, which the constructor truncates to |

None of these is configurable per call or via `appsettings`.
