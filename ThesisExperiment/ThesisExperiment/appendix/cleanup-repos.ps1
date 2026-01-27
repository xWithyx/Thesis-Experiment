<#
.SYNOPSIS
    Loescht alle Repo-Ordner unter repos/, die NICHT in project_list_filtered.csv stehen.
.DESCRIPTION
    Standardmaessig laeuft das Skript im Dry-Run-Modus und zeigt nur an,
    welche Ordner behalten und welche geloescht wuerden.
    Mit dem Parameter -Execute wird die tatsaechliche Loeschung aktiviert.
.EXAMPLE
    .\cleanup-repos.ps1
    .\cleanup-repos.ps1 -Execute
#>

param(
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Variablen ---
$appendixDir = "appendix"
$reposDir    = "repos"
$csvPath     = Join-Path $appendixDir "project_list_filtered.csv"
$logPath     = Join-Path $appendixDir "repo_cleanup_log.txt"

# --- Sicherheitschecks ---
if (-not (Test-Path $csvPath)) {
    Write-Error "CSV nicht gefunden: $csvPath  Bitte pruefen, ob die Datei existiert."
    exit 1
}

if (-not (Test-Path $reposDir)) {
    Write-Error "Repos-Ordner nicht gefunden: $reposDir  Bitte pruefen, ob der Pfad stimmt."
    exit 1
}

$allRepoDirs = Get-ChildItem $reposDir -Directory
if ($allRepoDirs.Count -eq 0) {
    Write-Warning "Der Ordner '$reposDir' enthaelt keine Unterordner. Nichts zu tun."
    exit 0
}

# --- CSV einlesen ---
$csv = Import-Csv $csvPath

# Spalte clone_path ermitteln (die CSV hat keinen project_name)
$columns = $csv[0].PSObject.Properties.Name
if ("clone_path" -notin $columns) {
    Write-Error ("Spalte 'clone_path' nicht gefunden. Verfuegbare Spalten: " + ($columns -join ", "))
    exit 1
}

# Aus clone_path (z.B. "repos\owner__name") den Ordnernamen extrahieren
$keepNames = $csv | ForEach-Object {
    $raw = $_.clone_path.Trim()
    # Letztes Pfadsegment nehmen (nach \ oder /)
    $name = Split-Path $raw -Leaf
    $name.Trim()
} | Where-Object { $_ -ne "" }

if ($keepNames.Count -eq 0) {
    Write-Error "Keine gueltige Eintraege in clone_path gefunden. CSV pruefen."
    exit 1
}

Write-Host ""
Write-Host "=== Repo Cleanup ===" -ForegroundColor Cyan
Write-Host "CSV:        $csvPath"
Write-Host "Repos:      $reposDir"
Write-Host "Eintraege in CSV (keep): $($keepNames.Count)"
Write-Host "Ordner in repos/:        $($allRepoDirs.Count)"
if (-not $Execute) {
    Write-Host "[DRY RUN]   Es wird nichts geloescht." -ForegroundColor Yellow
}
Write-Host ""

# --- Kategorisierung ---
$toKeep   = @()
$toDelete = @()

foreach ($dir in $allRepoDirs) {
    $dirName = $dir.Name.Trim()
    # Case-insensitive Vergleich
    $found = $keepNames | Where-Object { $_ -ieq $dirName }
    if ($found) {
        $toKeep += $dir
        Write-Host "  KEEP:   $dirName" -ForegroundColor Green
    }
    else {
        $toDelete += $dir
        Write-Host "  DELETE: $dirName" -ForegroundColor Red
    }
}

# --- Zusammenfassung ---
Write-Host ""
Write-Host "--- Zusammenfassung ---" -ForegroundColor Cyan
Write-Host "  Behalten:  $($toKeep.Count)"
Write-Host "  Loeschen:  $($toDelete.Count)"
Write-Host ""

if ($toDelete.Count -eq 0) {
    Write-Host "Nichts zu loeschen. Fertig." -ForegroundColor Green
    exit 0
}

if (-not $Execute) {
    Write-Host "Um tatsaechlich zu loeschen, starte mit:  .\cleanup-repos.ps1 -Execute" -ForegroundColor Yellow
    exit 0
}

# --- Bestaetigung ---
Write-Host "Folgende $($toDelete.Count) Ordner werden UNWIDERRUFLICH geloescht:" -ForegroundColor Red
foreach ($d in $toDelete) {
    Write-Host "    $($d.Name)" -ForegroundColor Red
}
Write-Host ""
$answer = Read-Host "Fortfahren? (y/n)"
if ($answer -ne "y") {
    Write-Host "Abgebrochen. Nichts wurde geloescht." -ForegroundColor Yellow
    exit 0
}

# --- Loeschen ---
$deletedCount = 0
$skippedCount = 0
$logLines     = @()
$logLines    += "Repo Cleanup Log  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$logLines    += "CSV: $csvPath"
$logLines    += "Mode: EXECUTE"
$logLines    += ""

foreach ($d in $toDelete) {
    $fullPath = $d.FullName
    try {
        Write-Host "  Loesche: $($d.Name) ..." -NoNewline
        Remove-Item $fullPath -Recurse -Force
        Write-Host " OK" -ForegroundColor Green
        $deletedCount++
        $logLines += "DELETED  $($d.Name)"
    }
    catch {
        Write-Host " FEHLER: $_" -ForegroundColor Red
        $skippedCount++
        $logLines += "SKIPPED  $($d.Name)  Error: $_"
    }
}

# --- Statistik ---
$logLines += ""
$logLines += "Kept:    $($toKeep.Count)"
$logLines += "Deleted: $deletedCount"
$logLines += "Skipped: $skippedCount"

Write-Host ""
Write-Host "=== Ergebnis ===" -ForegroundColor Cyan
Write-Host "  Behalten:    $($toKeep.Count)"
Write-Host "  Geloescht:   $deletedCount"
Write-Host "  Uebersprungen (Fehler): $skippedCount"

# --- Logdatei ---
$logLines | Out-File -FilePath $logPath -Encoding utf8
Write-Host ""
Write-Host "Log geschrieben: $logPath" -ForegroundColor Gray
