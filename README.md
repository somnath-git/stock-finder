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

The "Ask transcripts" feature needs the local LLM, so a shared demo hosts only the database (Neon), API, and UI (Render). The Ask tab stays visible and shows an offline notice (the API's `/api/ask-status` reports the LLM is unreachable). All stored analysis, transcripts, and news stay live. Both free tiers have no time limit; Render web services sleep after 15 min idle and wake on the next request (~30-60s cold start).

### 1. Export your local data
```
docker exec -t trendstracker-db pg_dump -U postgres -d trendstracker -Fc -f /tmp/trendstracker.dump
docker cp trendstracker-db:/tmp/trendstracker.dump ./trendstracker.dump
```

### 2. Create the database on Neon
- Create a free project at https://neon.tech and copy its connection string.
- In Neon's SQL editor:
```
CREATE EXTENSION IF NOT EXISTS vector;
```

### 3. Restore your data into Neon
```
docker cp ./trendstracker.dump trendstracker-db:/tmp/trendstracker.dump
docker exec -i trendstracker-db pg_restore --no-owner --clean --if-exists -d "postgresql://USER:PASSWORD@HOST/DBNAME?sslmode=require" /tmp/trendstracker.dump
```

### 4. Deploy on Render (uses render.yaml)
- Push this repo to GitHub.
- At https://render.com, New > Blueprint, connect the repo. Render reads `render.yaml` and creates the `stockfinder-api` and `stockfinder-ui` services.
- Set the two values marked `sync: false` in the Render dashboard:
  - `stockfinder-api` > `TRENDSTRACKER_DB` = the Neon connection string from step 2.
  - `stockfinder-ui` > `API_UPSTREAM` = the API's public URL (e.g. `https://stockfinder-api.onrender.com`, no trailing slash).
- Leave `OLLAMA_BASEURL` unset so the Ask tab shows its offline notice.

### 5. Open the UI
Open the `stockfinder-ui` public URL. First load after idle takes ~30-60s to wake.
