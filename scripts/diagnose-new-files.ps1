# Diagnostic (LECTURE SEULE) des nouveaux PDF/images avant traitement definitif.
#
# Ce script ne modifie JAMAIS :
#   - GenericDocumentMetadataExtractor.cs, BankDocumentParser.cs, ni aucun autre fichier de code.
#   - Le resultat extrait (CustomerName, banque, etc.) - il ne fait que le LIRE et le rapporter.
#
# Il n'introduit AUCUNE nouvelle regle de parsing : il appelle le vrai pipeline OCR de
# l'application (endpoint POST /api/Ocr, deja existant dans OcrController.cs) et lit
# uniquement des champs DEJA calcules par le pipeline (OcrConfidence, NeedsReview,
# Metadata.CustomerName, Metadata.BankName, DetectedType) pour classer chaque fichier en
# OK / SUSPECT / FAILED. NeedsReview et OcrConfidence sont des validations deja presentes
# dans DocumentProcessingService.EvaluateNeedsReview - ce script ne fait que les afficher.
#
# Prerequis : l'API doit deja tourner (autre console) :
#   dotnet run --project "Codeium Security.csproj"
#
# Usage :
#   .\scripts\diagnose-new-files.ps1 -InputFolder "C:\chemin\vers\nouveaux_pdf"
#   .\scripts\diagnose-new-files.ps1 -InputFolder "C:\...\nouveaux_pdf" -Port 5268 -ReportPath "C:\...\rapport.csv"

param(
    [Parameter(Mandatory = $true)]
    [string]$InputFolder,

    [int]$Port = 5268,

    [string]$ReportPath
)

$ErrorActionPreference = "Stop"

$SupportedExtensions = @(".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp")
$ApiBaseUrl = "http://localhost:$Port"
$ExtractUrl = "$ApiBaseUrl/api/Ocr"

# ----------------------------------------------------------------------------------------
# 1) Validation du dossier d'entree
# ----------------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $InputFolder -PathType Container)) {
    Write-Error "Dossier introuvable : $InputFolder"
    exit 1
}

$filesToCheck = Get-ChildItem -LiteralPath $InputFolder -File |
    Where-Object { $SupportedExtensions -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object Name

if ($filesToCheck.Count -eq 0) {
    Write-Error "Aucun fichier supporte dans $InputFolder (extensions attendues : $($SupportedExtensions -join ', '))."
    exit 1
}

Write-Host "Fichiers a diagnostiquer : $($filesToCheck.Count)" -ForegroundColor Cyan
if ($filesToCheck.Count -gt 30) {
    Write-Host "Attention : lot volumineux ($($filesToCheck.Count) fichiers), le traitement OCR peut prendre plusieurs minutes." -ForegroundColor Yellow
}

# ----------------------------------------------------------------------------------------
# 2) Verification que l'API tourne deja (ce script ne la demarre jamais lui-meme)
# ----------------------------------------------------------------------------------------
try {
    Invoke-WebRequest -Uri "$ApiBaseUrl/swagger/index.html" -UseBasicParsing -TimeoutSec 5 | Out-Null
}
catch {
    Write-Error "L'API ne repond pas sur $ApiBaseUrl. Demarrez-la d'abord dans une autre console : dotnet run --project `"Codeium Security.csproj`""
    exit 1
}

# ----------------------------------------------------------------------------------------
# 3) Appel du vrai pipeline OCR (POST /api/Ocr) - un seul appel, multipart, scope strictement
#    limite aux fichiers de -InputFolder (aucun autre fichier du projet n'est touche/relance).
# ----------------------------------------------------------------------------------------
Add-Type -AssemblyName System.Net.Http

Write-Host "Traitement OCR en cours (pipeline reel de l'application)..." -ForegroundColor Cyan

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(30)
$content = New-Object System.Net.Http.MultipartFormDataContent
$openStreams = @()

try {
    foreach ($file in $filesToCheck) {
        $stream = [System.IO.File]::OpenRead($file.FullName)
        $openStreams += $stream
        $fileContent = New-Object System.Net.Http.StreamContent($stream)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")
        $content.Add($fileContent, "files", $file.Name)
    }

    $response = $client.PostAsync($ExtractUrl, $content).GetAwaiter().GetResult()
    $rawBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    if (-not $response.IsSuccessStatusCode) {
        Write-Error "L'API a repondu $($response.StatusCode) : $rawBody"
        exit 1
    }
}
finally {
    foreach ($s in $openStreams) { $s.Dispose() }
    $content.Dispose()
    $client.Dispose()
}

$results = $rawBody | ConvertFrom-Json

# ----------------------------------------------------------------------------------------
# 4) Construction du rapport - classification basee UNIQUEMENT sur des champs deja calcules
#    par le pipeline (aucune nouvelle regle de parsing) :
#      FAILED  : fichier absent de la reponse (exception pendant ProcessFileAsync, voir
#                console du serveur), CustomerName vide, ou DetectedType = Unknown.
#      SUSPECT : NeedsReview = true (deja calcule par EvaluateNeedsReview) - la raison
#                affichee reprend simplement les champs qui composent ce calcul
#                (OcrConfidence, BankName, comptes/transactions deja presents dans le JSON).
#      OK      : ni FAILED ni SUSPECT.
# ----------------------------------------------------------------------------------------
$report = @()

foreach ($file in $filesToCheck) {
    $result = $results | Where-Object { $_.fileName -eq $file.Name } | Select-Object -First 1

    if (-not $result) {
        $report += [PSCustomObject]@{
            FileName     = $file.Name
            BankDetected = ""
            CustomerName = ""
            Status       = "FAILED"
            Reason       = "Exception pendant le traitement (fichier absent de la reponse API - voir la console du serveur dotnet run)"
        }
        continue
    }

    $customerName = $result.metadata.customerName
    $bankName     = $result.metadata.bankName
    $detectedType = $result.detectedType
    $confidence   = $result.ocrConfidence
    $needsReview  = $result.needsReview

    $status  = "OK"
    $reasons = @()

    if ($detectedType -eq "Unknown") {
        $status = "FAILED"
        $reasons += "DetectedType = Unknown (document non reconnu par le classifieur)"
    }

    if ([string]::IsNullOrWhiteSpace($customerName)) {
        $status = "FAILED"
        $reasons += "CustomerName vide (non extrait)"
    }

    if ($status -ne "FAILED" -and $needsReview -eq $true) {
        $status = "SUSPECT"

        if ($confidence -lt 85) {
            $reasons += "OcrConfidence faible ($([math]::Round([double]$confidence, 1))%)"
        }
        if ([string]::IsNullOrWhiteSpace($bankName)) {
            $reasons += "BankName non detecte"
        }
        if ($result.document -and ($result.document.PSObject.Properties.Name -contains "accounts")) {
            $accounts = @($result.document.accounts)
            if ($accounts.Count -eq 0) {
                $reasons += "Aucun compte detecte dans le document"
            }
            foreach ($acc in $accounts) {
                if ([string]::IsNullOrWhiteSpace($acc.accountNumber)) {
                    $reasons += "Numero de compte vide sur un des comptes"
                }
                $txCount = @($acc.transactions).Count
                if ($acc.soldeFinal -eq 0 -and $txCount -eq 0) {
                    $reasons += "Compte vide (solde final = 0 et 0 transaction)"
                }
            }
        }
        if ($reasons.Count -eq 0) {
            $reasons += "NeedsReview=true (raison precise non deductible des champs exposes par l'API)"
        }
    }

    $report += [PSCustomObject]@{
        FileName     = $file.Name
        BankDetected = $bankName
        CustomerName = $customerName
        Status       = $status
        Reason       = ($reasons -join " | ")
    }
}

# ----------------------------------------------------------------------------------------
# 5) Affichage console (couleurs) + export CSV
# ----------------------------------------------------------------------------------------
Write-Host ""
Write-Host "===== Rapport de diagnostic =====" -ForegroundColor Cyan
foreach ($row in $report) {
    $color = switch ($row.Status) {
        "OK"      { "Green" }
        "SUSPECT" { "Yellow" }
        "FAILED"  { "Red" }
        default   { "White" }
    }
    Write-Host ("[{0,-7}] {1,-45} Banque={2,-12} CustomerName='{3}'" -f $row.Status, $row.FileName, $row.BankDetected, $row.CustomerName) -ForegroundColor $color
    if ($row.Reason) {
        Write-Host ("           -> $($row.Reason)") -ForegroundColor DarkGray
    }
}

$okCount      = @($report | Where-Object Status -eq "OK").Count
$suspectCount = @($report | Where-Object Status -eq "SUSPECT").Count
$failedCount  = @($report | Where-Object Status -eq "FAILED").Count
Write-Host ""
Write-Host "Total : $($report.Count) | OK=$okCount | SUSPECT=$suspectCount | FAILED=$failedCount" -ForegroundColor Cyan

if (-not $ReportPath) {
    New-Item -ItemType Directory -Force -Path "TrainingData/RawResults" | Out-Null
    $timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $ReportPath = "TrainingData/RawResults/diagnostic_$timestamp.csv"
}

$report | Export-Csv -Path $ReportPath -Delimiter ";" -NoTypeInformation -Encoding UTF8
Write-Host "Rapport CSV enregistre : $ReportPath" -ForegroundColor Cyan
