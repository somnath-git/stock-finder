<#
.SYNOPSIS
  Refresh the hosted demo's data by running the analysis locally against Neon.

.DESCRIPTION
  The analysis pipeline needs the local LLM (Ollama), which isn't hosted. So you
  run it on your machine and write the results straight into the Neon database
  that the hosted API reads. No redeploy is needed — the site reflects new data
  on the next request.

  This script:
    1. Checks the Neon connection string and a reachable Ollama.
    2. Points the console apps at Neon (TRENDSTRACKER_DB).
    3. Runs the chosen analysis (a single stock, or an index batch).
    4. Prints row counts from Neon so you can confirm it landed.

.PARAMETER Stock
  Analyze a single stock, e.g. -Stock AIMTRON.

.PARAMETER Index
  Analyze an index batch, e.g. -Index SMALLCA250. Ignored if -Stock is given.

.PARAMETER Limit
  Max NEW analyses in an index batch (resumes from what's already stored).

.PARAMETER NeonConnection
  The Neon connection string. If omitted, the TRENDSTRACKER_DB environment
  variable is used. Never hard-code the secret in this file.

.EXAMPLE
  # One stock:
  $env:TRENDSTRACKER_DB = "postgresql://...neon..."
  ./refresh.ps1 -Stock AIMTRON

.EXAMPLE
  # Index batch of up to 20 new analyses:
  ./refresh.ps1 -Index SMALLCA250 -Limit 20 -NeonConnection "postgresql://...neon..."
#>

[CmdletBinding()]
param(
    [string]$Stock,
    [string]$Index = "SMALLCA250",
    [int]$Limit = 10,
    [string]$NeonConnection,
    [string]$OllamaUrl = "http://localhost:11434"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

# --- 1. Resolve the Neon connection string (never commit it) ---
$conn = if ($NeonConnection) { $NeonConnection } else { $env:TRENDSTRACKER_DB }
if ([string]::IsNullOrWhiteSpace($conn)) {
    Write-Error "No Neon connection string. Pass -NeonConnection or set `$env:TRENDSTRACKER_DB."
}
if ($conn -notmatch "neon|postgres") {
    Write-Warning "Connection string doesn't look like a Neon/Postgres URL. Continuing anyway."
}

# --- 2. Check Ollama is up (the analysis needs it for embeddings + verdicts) ---
Write-Host "Checking Ollama at $OllamaUrl ..." -ForegroundColor Cyan
try {
    Invoke-WebRequest -Uri "$OllamaUrl/api/tags" -UseBasicParsing -TimeoutSec 5 | Out-Null
    Write-Host "  Ollama is reachable." -ForegroundColor Green
}
catch {
    Write-Error "Ollama not reachable at $OllamaUrl. Start it (docker start ollama) and ensure the models are pulled."
}

# --- 3. Run the analysis against Neon ---
$env:TRENDSTRACKER_DB = $conn
Write-Host "Writing results to Neon." -ForegroundColor Cyan

if ($Stock) {
    Write-Host "Analyzing single stock: $Stock" -ForegroundColor Cyan
    dotnet run --project src/TrendsTracker.Company -- $Stock
}
else {
    Write-Host "Analyzing index batch: $Index (limit $Limit new)" -ForegroundColor Cyan
    dotnet run --project src/TrendsTracker.Screen -- --index $Index --limit $Limit
}

# --- 4. Confirm what's now stored (via the running local DB container's psql) ---
Write-Host "`nRow counts in Neon:" -ForegroundColor Cyan
$sql = 'SELECT ''companies'' AS tbl, count(*) AS n FROM public.companies UNION ALL SELECT ''transcript_chunks'', count(*) FROM public.transcript_chunks;'
try {
    $sql | docker exec -i trendstracker-db psql "$conn"
}
catch {
    Write-Warning "Could not run the count check via the local db container. The analysis above still wrote to Neon."
}

Write-Host "`nDone. The hosted site reflects the new data on its next request." -ForegroundColor Green
