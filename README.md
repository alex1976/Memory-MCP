# Memory-MCP

Server MCP (Model Context Protocol) remoto per la memorizzazione, il recupero e la ricerca semantica di
"memorie" da parte di agenti AI, organizzate in **spazi** (spaces) multi-tenant e protette da API Key.

Specifica funzionale completa: [CLAUDE.md](CLAUDE.md).

## Indice

- [Architettura](#architettura)
- [Tecnologia](#tecnologia)
- [Modello dati](#modello-dati)
- [Tool MCP disponibili](#tool-mcp-disponibili)
- [Fasi del progetto](#fasi-del-progetto)
- [Setup e avvio](#setup-e-avvio)
- [Test](#test)
- [Docker](#docker)

## Architettura

Clean Architecture a 4 livelli, con dipendenze a una sola direzione (`Api` → `Infrastructure`/`Application` → `Domain`):

```
Memory-MCP/
├── src/
│   ├── MemoryMcp.Domain/          # Entità pure (Space, ApiKey, ApiKeySpaceGrant, Document, Memory)
│   │                              # Nessuna dipendenza da EF/HTTP/librerie esterne.
│   ├── MemoryMcp.Application/     # Casi d'uso: interfacce (IMemoryService, IDocumentService,
│   │                              # ISpaceService, IEmbeddingProvider, repository, ICurrentAccessContext)
│   │                              # + implementazioni dei servizi applicativi + DTO.
│   ├── MemoryMcp.Infrastructure/   # EF Core (MemoryDbContext, migrations, repository), provider
│   │                              # di embedding (OpenAI/Azure OpenAI).
│   └── MemoryMcp.Api/             # Host ASP.NET Core: hosting MCP, autenticazione API Key,
│                                  # i 7 tool MCP (thin adapter senza logica di business).
└── tests/
    ├── MemoryMcp.Application.Tests/    # Unit test dei servizi applicativi (mock/NSubstitute)
    ├── MemoryMcp.Infrastructure.Tests/ # Integration test dei repository contro un Postgres reale
    └── MemoryMcp.Api.Tests/            # Test end-to-end sui 7 tool via client MCP + WebApplicationFactory
```

**Perché Clean Architecture e non Vertical Slice**: tutti i tool condividono lo stesso modello dati
(Space/ApiKey/Memory/Document) e le stesse regole di autorizzazione per-spazio; isolare la persistenza EF
e il provider di embedding dietro interfacce in `Application` permette di estendere il progetto (resource,
prompt, widget MCP Apps — Fase 2) senza toccare `Domain`/`Application`.

Ogni classe in `Api/Tools` è un **thin adapter**: risolve il contesto di accesso (`ICurrentAccessContext`),
chiama il servizio applicativo, formatta l'output — nessuna logica di business nel livello Api.

### Autenticazione e multi-tenancy

- Ogni **API Key** è associata a uno o più **spazi**, con un livello di accesso (`Read` o `ReadWrite`) per
  spazio e uno spazio marcato come "attivo" (`IsDefault`).
- `ApiKeyAuthenticationHandler` (`src/MemoryMcp.Api/Auth`) legge la chiave dall'header `X-Api-Key` (o
  `Authorization: Bearer <key>`), la valida contro il database (hash SHA-256, mai la chiave in chiaro) e
  popola `CurrentAccessContext`, uno scoped service iniettato nei servizi applicativi.
- Il parametro `containerTag` dei tool corrisponde alla colonna `spaces.key`; se omesso, viene usato lo
  spazio "attivo" della chiave corrente.
- Errori di autorizzazione/spazio non trovato vengono tradotti in risultati di tool MCP con `isError=true`
  (mai eccezioni non gestite o 500).

### Ricerca semantica

`IEmbeddingProvider` è pluggable (OpenAI o Azure OpenAI, stesso client `EmbeddingClient` sotto le due
implementazioni). Gli embedding sono salvati come colonna Postgres nativa `real[]` (via Npgsql) e la
similarità coseno viene calcolata **in-app** in `MemoryRepository.SearchAsync`.

> Nota: la specifica originale prevedeva PostgreSQL + estensione `pgvector` con indice HNSW. In questo
> ambiente Docker Desktop è bloccato da policy aziendale e il Postgres locale disponibile non ha
> `pgvector` installato (né è possibile installarlo senza permessi di amministratore locale), quindi la
> ricerca è stata implementata senza l'estensione. Funziona correttamente ma non scala quanto un indice
> vettoriale nativo su volumi molto grandi — se in futuro `pgvector` diventa disponibile, la migrazione a
> un indice HNSW richiede di rivedere `MemoryConfiguration` e `MemoryRepository.SearchAsync`.

## Tecnologia

| Livello | Tecnologia |
| --- | --- |
| Runtime | .NET 10 (pinnato in [global.json](global.json)) |
| Server MCP | `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` 2.1.0 |
| Web host | ASP.NET Core Minimal API |
| Persistenza | PostgreSQL + EF Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`) |
| Embedding | `OpenAI` / `Azure.AI.OpenAI` (stesso `EmbeddingClient`, selezionato via configurazione) |
| Autenticazione | Scheme custom `AuthenticationHandler<T>` basato su API Key (hash SHA-256) |
| Test | xUnit, NSubstitute, AwesomeAssertions (fork MIT di FluentAssertions), `Microsoft.AspNetCore.Mvc.Testing` |
| Container | Dockerfile multi-stage + docker-compose (per ambienti dove Docker è disponibile) |

## Modello dati

| Tabella | Descrizione |
| --- | --- |
| `spaces` | Spazio logico (`key` univoca = `containerTag`, `name`, `description`) |
| `api_keys` | Chiave API (solo hash salvato, mai il valore in chiaro) |
| `api_key_space_grants` | Permesso `Read`/`ReadWrite` di una API Key su uno spazio + flag "spazio attivo" |
| `documents` | Documento sorgente (titolo, tipo, stato, summary, contenuto raw) |
| `memories` | Memoria estratta (testo, embedding `real[]`, versione, `is_active` per soft-delete/"forget") |

## Tool MCP disponibili

Tutti i 7 tool richiesti dalla specifica sono implementati in `src/MemoryMcp.Api/Tools`:

| Tool | File | Accesso richiesto | Descrizione |
| --- | --- | --- | --- |
| `search_memory` | `MemoryTools.cs` | Read | Ricerca semantica (similarità coseno) tra le memorie di uno spazio, con profilo opzionale |
| `add_memory` | `MemoryTools.cs` | ReadWrite | Salva (`action=save`) o rimuove (`action=forget`) una memoria |
| `listDocuments` | `DocumentTools.cs` | Read | Elenco paginato dei documenti sorgente di uno spazio |
| `getDocument` | `DocumentTools.cs` | Read | Metadati e contenuto di un documento |
| `listMemories` | `MemoryTools.cs` | Read | Elenco paginato delle memorie estratte |
| `listSpaces` | `AccessTools.cs` | — | Spazi accessibili dalla API Key corrente, con conteggi |
| `whoAmI` | `AccessTools.cs` | — | Identità corrente, spazi accessibili, spazio attivo |

## Fasi del progetto

### Fase 1 — Completata

- [x] Le entità Domain e il modello dati relazionale (Space, ApiKey, ApiKeySpaceGrant, Document, Memory)
- [x] Persistenza EF Core + migration iniziale
- [x] Autenticazione via API Key con permessi per-spazio
- [x] `IEmbeddingProvider` pluggable (OpenAI / Azure OpenAI)
- [x] I 7 tool MCP core, esposti via `ModelContextProtocol.AspNetCore` su endpoint HTTP `/mcp`
- [x] Suite di test (unit, integration, end-to-end)

### Fase 2 — Non implementata (per non bloccare l'estensibilità futura)

- [ ] Resource MCP: `memory-mcp://profile`, `memory-mcp://spaces`
- [ ] Prompt MCP: `context`
- [ ] Widget interattivi MCP Apps: `select-space`, `guided-save`, `upload-file`, `memory-graph`
      (richiedono il package `ModelContextProtocol.Extensions.Apps` e UI iframe-based)
- [ ] Reintroduzione di `pgvector`/indice HNSW se e quando l'estensione sarà disponibile sull'ambiente Postgres di riferimento

Questi elementi si aggiungono come nuove classi (`[McpServerResourceType]`, `[McpServerPromptType]`) nel
progetto `Api`, riusando i servizi `Application` esistenti — senza modifiche a `Domain`/`Application`.

## Setup e avvio

### Prerequisiti

- [.NET SDK 10](https://dotnet.microsoft.com/download) (versione pinnata in [global.json](global.json))
- Un'istanza PostgreSQL raggiungibile (locale o remota). **Non serve l'estensione `pgvector`.**
- (Opzionale) una API Key OpenAI o Azure OpenAI, necessaria solo per i tool `add_memory` e `search_memory`

### 1. Configurare la connection string

Crea/aggiorna `src/MemoryMcp.Api/appsettings.Development.json` (file **non versionato**, vedi
[.gitignore](.gitignore)) con la connection string del tuo Postgres:

```json
{
  "ConnectionStrings": {
    "Default": "Host=localhost;Port=5432;Username=<user>;Password=<password>;Database=<database>"
  }
}
```

In alternativa, puoi impostare la variabile d'ambiente `ConnectionStrings__Default` senza toccare i file
di configurazione.

### 2. Applicare le migration

```bash
dotnet tool restore # se non hai già dotnet-ef installato: dotnet tool install --global dotnet-ef
dotnet ef database update --project src/MemoryMcp.Infrastructure --startup-project src/MemoryMcp.Api
```

### 3. (Opzionale) Configurare l'embedding provider

Per usare `add_memory`/`search_memory`, in `appsettings.Development.json`:

```json
{
  "Embeddings": {
    "Provider": "OpenAI",
    "ApiKey": "sk-...",
    "Model": "text-embedding-3-small"
  }
}
```

Per Azure OpenAI: `"Provider": "AzureOpenAI"`, `"Endpoint": "https://<resource>.openai.azure.com"`,
`"Model"` = nome della deployment. Senza queste impostazioni il server parte comunque e tutti gli altri
tool funzionano normalmente: solo `add_memory`/`search_memory` restituiranno un errore di tool.

### 4. Creare uno spazio e una API Key di test

Non esiste ancora un'API di amministrazione (fuori scope Fase 1): un comando da riga di comando crea uno
spazio "default" e una API Key con accesso `ReadWrite`, stampando la chiave in chiaro una sola volta:

```bash
dotnet run --project src/MemoryMcp.Api -- --seed
# Seeded space 'default' with API key: mmcp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
```

### 5. Avviare il server

```bash
dotnet run --project src/MemoryMcp.Api
```

Il server espone:
- `GET /health` — health check
- `POST /mcp` — endpoint MCP (Streamable HTTP), protetto: richiede l'header `X-Api-Key: <chiave>` (o
  `Authorization: Bearer <chiave>`)

Porta predefinita in sviluppo: `http://localhost:5004` (vedi
`src/MemoryMcp.Api/Properties/launchSettings.json`).

Puoi collegare un client MCP (es. [MCP Inspector](https://modelcontextprotocol.io/docs/tools/inspector))
all'endpoint `http://localhost:5004/mcp` passando l'header `X-Api-Key` con la chiave generata al passo 4.

### 6. (Opzionale) Collegare Claude Desktop

Un file di esempio è disponibile in [claude_desktop_config.example.json](claude_desktop_config.example.json):

```json
{
  "mcpServers": {
    "memory-mcp": {
      "url": "http://localhost:5004/mcp",
      "headers": {
        "X-Api-Key": "mmcp_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
      }
    }
  }
}
```

Copia il contenuto (sostituendo la chiave con quella generata al passo 4) nel file di configurazione reale
di Claude Desktop, poi riavvia l'applicazione:

- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

Se esiste già una sezione `mcpServers` con altri server, aggiungi semplicemente la voce `memory-mcp` senza
sovrascrivere le altre. Al riavvio, i 7 tool di Memory-MCP saranno disponibili nella conversazione.

## Test

```bash
# Unit test (nessuna dipendenza esterna)
dotnet test tests/MemoryMcp.Application.Tests/MemoryMcp.Application.Tests.csproj

# Integration test ed end-to-end: richiedono un Postgres reale raggiungibile
export MEMORYMCP_TEST_CONNECTION_STRING="Host=localhost;Port=5432;Username=<user>;Password=<password>;Database=<database>"
dotnet test
```

> I test di integrazione/E2E si connettono direttamente al Postgres indicato da
> `MEMORYMCP_TEST_CONNECTION_STRING` (niente Testcontainers/Docker, per coerenza con l'ambiente aziendale).
> Applicano le migration automaticamente e ogni test usa chiavi/spazi con GUID casuali, quindi è sicuro
> puntare anche al database di sviluppo.

## Docker

`Dockerfile` e `docker-compose.yml` sono pronti per ambienti dove Docker è disponibile (es. CI/CD o
deployment), ma **non sono stati verificati in questo ambiente di sviluppo** (Docker Desktop bloccato da
policy aziendale):

```bash
docker compose up --build
```

Avvia un Postgres standard (`postgres:17`, senza `pgvector`) e il servizio `api`, esposto su
`http://localhost:8080`.
