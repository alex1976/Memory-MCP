# Perché Memory-MCP: il risparmio di token

Memory-MCP non è solo "un posto dove salvare testo": l'architettura è pensata esplicitamente per
ridurre quanti token un agente AI deve inviare/ricevere per mantenere memoria a lungo termine,
sostituendo il pattern "manda sempre tutto il contesto" con "manda solo ciò che serve".

## I meccanismi

### 1. Retrieval selettivo invece di ripetere tutto il contesto
Invece di incollare ogni volta l'intera cronologia della conversazione o documenti interi nel
prompt, l'agente chiama `search_memory` con una query specifica e riceve solo i frammenti
pertinenti (di default max 10 risultati, `SearchTopK`). Il costo in token è quello della query più
pochi risultati mirati, non quello di "tutto ciò che sai su questo argomento".

### 2. Graph memory: fatti atomici, non blocchi di testo
Quando salvi contenuto con `add_memory`, `IFactExtractor` lo scompone in fatti atomici (frasi brevi
e autonome) invece di conservarlo come un unico blocco (vedi
[graph-memory-plan.md](graph-memory-plan.md)). Quando poi lo recuperi, ottieni la singola
informazione rilevante ("Alex ha lasciato Stripe"), non il paragrafo intero da cui proveniva —
molti meno token per la stessa informazione utile.

### 3. Le relazioni (Updates/Extends/Derives) evitano di ricostruire la storia
Se un fatto aggiorna o estende un fatto precedente, viene creato un collegamento (`MemoryEdge`) al
momento del salvataggio. Quando fai una ricerca, `relatedMemories` restituisce già il contesto
collegato (es. "questo aggiorna quello") senza che l'agente debba rileggere/riassumere manualmente
tutta la cronologia per capire cosa è cambiato — il collegamento è precalcolato una sola volta al
salvataggio, non ricostruito ad ogni query.

### 4. Filtri per categoria e keyword riducono il rumore
`category` e `keyword` permettono di restringere la ricerca a ciò che serve per quel task
specifico, invece di un mix generico che l'LLM dovrebbe poi filtrare da solo — sprecando token nel
ragionamento invece che nella risposta.

### 5. `forget` mantiene la memoria pulita
Le informazioni obsolete possono essere rimosse esplicitamente, oppure vengono disattivate
automaticamente da una relazione `Updates` (con la stessa soglia di confidenza usata da `forget`).
Le memorie superate non vengono più recuperate, quindi non occupano più spazio nel contesto ad ogni
ricerca successiva.

### 6. Il prompt `context` sostituisce il riassunto manuale
Invece di far scrivere all'agente un riassunto della situazione ad ogni nuova conversazione (costoso
in token e rifatto da zero ogni volta), si può agganciare direttamente il messaggio già pronto
restituito dal prompt `context` (vedi [resources-and-prompt-usage.md](resources-and-prompt-usage.md))
— profilo dello spazio attivo più eventuali altri spazi recenti, già formattato in poche righe.

### 7. Gli spazi multi-tenant evitano il "bleed" cross-dominio
Organizzando le memorie per spazio (progetto, cliente, dominio), una ricerca non riporta
informazioni irrilevanti di altri contesti che finirebbero comunque nel prompt "per sicurezza",
gonfiandolo senza motivo.

## Come sfruttarlo al meglio

- Usa `search_memory` con query mirate invece di lasciare `includeProfile=true` sempre attivo, se
  il profilo non serve per quella richiesta.
- Tagga con `category` fin dal salvataggio, per poter filtrare in modo chirurgico più avanti.
- Lascia che sia il graph memory a collegare i fatti, invece di ripetere manualmente "come
  aggiornamento di quanto detto prima" nel testo salvato.
- Usa `forget` periodicamente per rimuovere ciò che non serve più.
- All'inizio di una nuova conversazione, usa il prompt `context` invece di far generare un
  riepilogo all'LLM da zero.

## Il punto

Il risparmio reale dipende da quanto testo ridondante si stava mandando prima (cronologie intere,
documenti ripetuti, riassunti rigenerati ogni volta): l'architettura di Memory-MCP è pensata
esattamente per sostituire "manda tutto" con "manda solo ciò che serve".
