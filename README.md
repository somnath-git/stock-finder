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
