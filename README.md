# StockFinder

## Prerequisites
- Docker
- .NET 10 SDK (for the console analysis apps)

## Setup

1. Clone and enter the repo:
```
git clone https://github.com/somnath-git/stock-finder.git
cd stock-finder
```

2. Copy the config:
```
cp config/appsettings.sample.json config/appsettings.json
```

3. Start the stack (database, Ollama, API, UI):
```
docker compose up -d --build
```

4. Pull the models (one time, ~5 GB):
```
docker compose exec ollama ollama pull qwen2.5:7b
docker compose exec ollama ollama pull nomic-embed-text
```

## Analyze stocks (writes results to the database)

Single stock:
```
dotnet run --project src/TrendsTracker.Company -- AIMTRON
```

Batch over index constituents:
```
dotnet run --project src/TrendsTracker.Screen -- --index SMALLCA250 --limit 20
```

News-trend pipeline:
```
dotnet run --project src/TrendsTracker.Trends
```

## View results
```
http://localhost:3000
```

## Stop
```
docker compose down
```

## Host a shared demo (data + API + UI, no LLM)

The "Ask transcripts" feature needs the local LLM, so a shared demo hosts only the database, API, and UI. The Ask tab stays visible and shows an offline notice (the API's `/api/ask-status` reports the LLM is unreachable). All stored analysis, transcripts, and news stay live.

### 1. Export your local data
```
docker exec -t trendstracker-db pg_dump -U postgres -d trendstracker -Fc -f /tmp/trendstracker.dump
docker cp trendstracker-db:/tmp/trendstracker.dump ./trendstracker.dump
```

### 2. Create a managed Postgres with pgvector (Neon / Supabase / Railway)
Create a Postgres instance, then enable the extension (run in its SQL console):
```
CREATE EXTENSION IF NOT EXISTS vector;
```

### 3. Restore your data into it
Replace the connection values with the host's.
```
pg_restore --no-owner --clean --if-exists -d "postgresql://USER:PASSWORD@HOST:PORT/DBNAME?sslmode=require" ./trendstracker.dump
```

### 4. Deploy the API (Railway)
```
npm i -g @railway/cli
railway login
railway init
railway up --service api --dockerfile src/TrendsTracker.Api/Dockerfile
```
Set the API service variables (no `OLLAMA_BASEURL`, so Ask reports offline):
```
railway variables --service api --set "TRENDSTRACKER_DB=postgresql://USER:PASSWORD@HOST:PORT/DBNAME?sslmode=require"
```

### 5. Deploy the UI (Railway)
```
railway up --service ui --dockerfile src/trendstracker-ui/Dockerfile
```
Point the UI's nginx proxy at the API's public origin (no trailing slash), then open the UI's public URL:
```
railway variables --service ui --set "API_UPSTREAM=https://YOUR-API.up.railway.app"
```
