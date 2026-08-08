---
name: memory-mcp
description: 'Usa questa skill quando è disponibile il server MCP "Memory-MCP" (tool search_memory, add_memory, listMemories, listDocuments, getDocument, listSpaces, whoAmI) e l''utente chiede di ricordare, salvare, richiamare, cercare o dimenticare informazioni tra conversazioni diverse, oppure di gestire spazi/categorie di memoria.'
---

# Memory-MCP — guida all'uso per l'agente

Memory-MCP è un server MCP remoto che dà a un agente AI una memoria persistente tra conversazioni,
organizzata in **spazi** (multi-tenant) e protetta da API Key. Questa skill descrive come un agente deve
usare i suoi 7 tool per salvare, recuperare, cercare e rimuovere memorie in modo efficace, senza sprecare
contesto e senza duplicare informazioni già note.

## Tool disponibili

| Tool | Accesso | Parametri | Quando usarlo |
| --- | --- | --- | --- |
| `whoAmI` | — | nessuno | All'inizio di una conversazione se non sai quale spazio è attivo o quali permessi hai |
| `listSpaces` | — | nessuno | Per scoprire tutti gli spazi accessibili con questa API Key prima di salvare/cercare in uno spazio specifico |
| `search_memory` | Read | `query`, `keyword`, `category` (almeno uno), `includeProfile` (default `true`), `containerTag` | Per recuperare memorie esistenti prima di rispondere o prima di salvarne una nuova |
| `add_memory` | ReadWrite | `content` (richiesto), `action` (`save`/`forget`, default `save`), `category` (solo su `save`), `containerTag` | Per salvare un fatto nuovo o rimuovere un fatto obsoleto/sbagliato |
| `listMemories` | Read | `page`, `limit` (max 50), `containerTag` | Per sfogliare tutte le memorie estratte in uno spazio (non per una ricerca puntuale) |
| `listDocuments` | Read | `page`, `limit` (max 50), `containerTag` | Per scoprire i documenti sorgente disponibili in uno spazio |
| `getDocument` | Read | `documentId` (richiesto) | Per leggere il contenuto completo di un documento già individuato |

Se `containerTag` viene omesso, tutti i tool operano sullo spazio "attivo" della API Key corrente
(`listSpaces`/`whoAmI` indicano quale è).

## Modalità di ricerca in `search_memory`

`search_memory` richiede **almeno uno** tra `query`, `keyword`, `category`; sono combinabili tra loro.

- **`query`** — ricerca semantica (similarità coseno sugli embedding). Usala per domande in linguaggio
  naturale o quando non conosci le parole esatte usate nella memoria originale (es. `"preferenze di
  formattazione del codice"`).
- **`keyword`** — match letterale case-insensitive sul testo della memoria, nessun embedding generato.
  Usala quando devi trovare un termine esatto, un nome proprio, un identificatore (es. `"INGEST-142"`).
- **`category`** — filtra per l'etichetta assegnata in `add_memory`. Usala da sola per elencare tutte le
  memorie di un certo tipo (es. tutte quelle con `category: "preferenze-utente"`), oppure insieme a
  `query`/`keyword` per restringere il campo di ricerca.
- **`includeProfile`** — se `true` (default), la risposta include anche il profilo stabile/recente dello
  spazio: utile per farsi un'idea generale di contesto anche quando i match diretti sono pochi. Mettilo a
  `false` quando ti serve solo la risposta puntuale alla ricerca.

## Flusso operativo consigliato

1. **All'avvio di una conversazione** dove la memoria persistente è rilevante, valuta se chiamare
   `whoAmI`/`listSpaces` per capire lo spazio attivo e i permessi, soprattutto se l'utente lavora su più
   progetti/spazi.
2. **Prima di salvare qualcosa di nuovo**, esegui sempre un `search_memory` (con `query` e/o `category`)
   per verificare che l'informazione non sia già presente o in conflitto con una memoria esistente. Se
   trovi un fatto obsoleto in contraddizione, usa `add_memory` con `action: "forget"` prima (o al posto)
   di salvare il nuovo fatto.
3. **Quando salvi** (`add_memory`, `action: "save"`):
   - Scrivi `content` come un fatto atomico e autosufficiente (una frase o poche righe), non un intero
     transcript o un blocco di testo non filtrato.
   - Assegna una `category` coerente quando l'informazione appartiene a un raggruppamento riutilizzabile
     (es. `preferenze-utente`, `progetto-x`, `credenziali-config`, `contatti`) così potrà essere ritrovata
     per categoria in futuro. Se non emerge una categoria naturale, puoi omettere il parametro.
   - Non salvare segreti, credenziali in chiaro o dati sensibili a meno che l'utente non lo richieda
     esplicitamente e lo spazio sia quello corretto per quel tipo di dato.
4. **Quando recuperi** (`search_memory`), scegli la modalità più adatta (vedi sopra) invece di usare
   sempre la ricerca semantica: una `keyword` o una `category` sono più precise e più economiche quando il
   termine o il raggruppamento sono noti.
5. **Quando un'informazione è superata o sbagliata**, usa `add_memory` con `action: "forget"` passando in
   `content` un testo che descriva/richiami la memoria da rimuovere (il matching è per similarità, non per
   ID) — non lasciare che coesistano versioni contraddittorie della stessa informazione.
6. **Per esplorare cosa è stato salvato** senza una query precisa, usa `listMemories`/`listDocuments`
   (paginati) invece di `search_memory`, che è pensato per il recupero mirato.

## Gestione errori

Gli errori di autorizzazione o di spazio non trovato arrivano come risultato del tool con `isError: true` e
un messaggio leggibile (es. spazio inesistente, permessi insufficienti, nessuno tra `query`/`keyword`/
`category` fornito a `search_memory`). Riporta il messaggio all'utente invece di ritentare alla cieca o di
inventare un fallback silenzioso.
