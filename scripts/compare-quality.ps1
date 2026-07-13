# Compare les resultats JSON du batch avec ground_truth.csv
# Usage: .\scripts\compare-quality.ps1

$projectRoot = Split-Path -Parent $PSScriptRoot
$rawResultsFolder = Join-Path $projectRoot "TrainingData\RawResults"
$groundTruthPath = Join-Path $projectRoot "TrainingData\ground_truth.csv"
$outputPath = Join-Path $projectRoot "TrainingData\quality_report.csv"

if (-not (Test-Path $groundTruthPath)) {
    Write-Error "Fichier introuvable: $groundTruthPath"
    exit 1
}

$groundTruth = Import-Csv $groundTruthPath
$jsonFiles = Get-ChildItem -Path $rawResultsFolder -Filter "*.json" -ErrorAction SilentlyContinue

if (-not $jsonFiles -or $jsonFiles.Count -eq 0) {
    Write-Error "Aucun fichier JSON dans $rawResultsFolder. Lancez d'abord run-batch.ps1"
    exit 1
}

function Normalize-String($value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return "" }
    return ($value -replace '\s', '').ToUpperInvariant()
}

function Normalize-Decimal($value) {
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) { return $null }
    $s = [string]$value -replace '\s', '' -replace ',', '.'
    [decimal]$result = 0
    if ([decimal]::TryParse($s, [ref]$result)) { return $result }
    return $null
}

$reportRows = @()

foreach ($gt in $groundTruth) {
    $jsonPath = Join-Path $rawResultsFolder ($gt.FileName -replace '\.[^.]+$', '.json')
    if (-not (Test-Path $jsonPath)) {
        $reportRows += [PSCustomObject]@{
            FileName = $gt.FileName
            BankMatch = "MISSING_JSON"
            AccountMatch = "MISSING_JSON"
            BalanceMatch = "MISSING_JSON"
            TransactionCountMatch = "MISSING_JSON"
            OcrConfidence = ""
            NeedsReview = ""
            Notes = "JSON non trouve pour ce fichier"
        }
        continue
    }

    $json = Get-Content $jsonPath -Raw | ConvertFrom-Json
    $doc = $json.Document

    $bankMatch = if ([string]::IsNullOrWhiteSpace($gt.BankName)) { "SKIP" }
                 elseif ($doc -and (Normalize-String $doc.BankName) -eq (Normalize-String $gt.BankName)) { "OK" }
                 else { "FAIL" }

    $accountMatch = if ([string]::IsNullOrWhiteSpace($gt.AccountNumber)) { "SKIP" }
                    elseif ($doc -and (Normalize-String $doc.AccountNumber) -eq (Normalize-String $gt.AccountNumber)) { "OK" }
                    else { "FAIL" }

    $expectedBalance = Normalize-Decimal $gt.Balance
    $actualBalance = if ($doc) { Normalize-Decimal $doc.Balance } else { $null }
    $balanceMatch = if ($null -eq $expectedBalance) { "SKIP" }
                    elseif ($null -ne $actualBalance -and $actualBalance -eq $expectedBalance) { "OK" }
                    else { "FAIL" }

    $expectedTxCount = if ([string]::IsNullOrWhiteSpace($gt.TransactionCount)) { $null } else { [int]$gt.TransactionCount }
    $actualTxCount = if ($doc -and $doc.Transactions) { $doc.Transactions.Count } else { 0 }
    $txMatch = if ($null -eq $expectedTxCount) { "SKIP" }
                 elseif ([Math]::Abs($actualTxCount - $expectedTxCount) -le 1) { "OK" }
                 else { "FAIL" }

    $reportRows += [PSCustomObject]@{
        FileName = $gt.FileName
        BankMatch = $bankMatch
        AccountMatch = $accountMatch
        BalanceMatch = $balanceMatch
        TransactionCountMatch = $txMatch
        OcrConfidence = $json.OcrConfidence
        NeedsReview = $json.NeedsReview
        Notes = $gt.Notes
    }
}

$reportRows | Export-Csv -Path $outputPath -NoTypeInformation -Encoding UTF8

function Get-Rate($field) {
    $evaluated = $reportRows | Where-Object { $_.$field -ne "SKIP" -and $_.$field -ne "MISSING_JSON" }
    if (-not $evaluated -or $evaluated.Count -eq 0) { return "N/A" }
    $ok = ($evaluated | Where-Object { $_.$field -eq "OK" }).Count
    return "{0:P0}" -f ($ok / $evaluated.Count)
}

Write-Host "Rapport genere: $outputPath"
Write-Host ""
Write-Host "Taux de reussite:"
Write-Host "  Banque       : $(Get-Rate 'BankMatch')"
Write-Host "  Compte       : $(Get-Rate 'AccountMatch')"
Write-Host "  Solde        : $(Get-Rate 'BalanceMatch')"
Write-Host "  Transactions : $(Get-Rate 'TransactionCountMatch')"
