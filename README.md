# StockFinder

Finds listed Indian companies whose own management guidance suggests the business
could roughly **double in 3-5 years**, using a local LLM (via Ollama) and a
**RAG (Retrieval-Augmented Generation)** pipeline over their concall transcripts and
investor presentations. Built to be **free** to run — no paid APIs required.

> ⚠️ Research assistant output, **NOT investment advice**. Management guidance and
> media attention can be wrong. Do your own research.

## Projects

```
src/
├── TrendsTracker.Core      Shared library: config, models, services (Ollama/Gemini
│                           clients, PDF fetch/extract, Screener document finder),
│                           RAG (chunker + in-process vector store), pipeline stages.
├── TrendsTracker.Trends    Console (Project 1): news → trending themes → companies
│                           → growth confirmation → report.
├── TrendsTracker.Company   Console (Project 2): give a stock, get the growth verdict.
│                           Saves results to the database; shows cached results instantly.
├── TrendsTracker.Data      Postgres persistence (EF Core + Npgsql): companies + news.
├── TrendsTracker.Api        ASP.NET minimal API (Project 3): read-only JSON over the DB.
└── trendstracker-ui         React + Vite UI (Project 4): browse companies & news.
```

## How the growth check works (RAG)

1. **Find documents** — Screener.in links the company's concall transcripts /
   investor-presentation PDFs (free, no API key).
2. **Ingest** — download the PDFs, extract text, split into overlapping chunks.
3. **Embed + store** — embed each chunk into a small in-process vector store.
4. **Hybrid retrieve** — blend semantic similarity with a growth-signal score
   (CAGR, YoY %, "double", order book) so the real guidance outranks boilerplate.
5. **Reason** — compute what a stated CAGR compounds to over 3-5 years and ask the
   LLM for a grounded verdict with quotes.

## Prerequisites

- **.NET 10 SDK**
- **Docker** (for Postgres, and optionally Ollama)
- **Node 18+** (for the UI)
- **Ollama** running locally with models pulled:
  ```
  ollama pull qwen2.5:7b
  ollama pull nomic-embed-text
  ```

## Run locally

**1. Database (Postgres in Docker):**
```powershell
docker run -d --name stockfinder-db -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=trendstracker -p 5432:5432 -v stockfinder_pgdata:/var/lib/postgresql/data postgres:16
```

**2. Analyze a stock (writes to the DB):**
```powershell
dotnet run --project src/TrendsTracker.Company -- AIMTRON
# re-analyze (ignore cache):  dotnet run --project src/TrendsTracker.Company -- AIMTRON --force
```

**3. API:**
```powershell
dotnet run --project src/TrendsTracker.Api --urls http://localhost:5080
```

**4. UI:**
```powershell
cd src/trendstracker-ui
npm install
npm run dev      # http://localhost:5173
```

## Configuration

Each console/API project reads `appsettings.json` (ignored by git — holds any real
keys). Copy `appsettings.sample.json` → `appsettings.json` to start. Default LLM
provider is **Ollama** (local, free); a Gemini provider exists but is rate-limited on
its free tier.

Database connection string resolution: `TRENDSTRACKER_DB` env var → local default
(`Host=localhost;Port=5432;Database=trendstracker;Username=postgres;Password=postgres`).

## Notes

- **Secrets:** `appsettings.json` is git-ignored. Never commit real API keys.
- **Hosting:** the UI and API host free (Vercel + Render/Neon). The LLM analysis is
  too heavy for free cloud tiers, so it runs locally and writes results to the DB that
  the hosted API serves.
