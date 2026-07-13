# Lance l'API puis appelle l'endpoint batch
# Usage: demarrer l'API dans une autre console avec:
#   dotnet run --project "Codeium Security.csproj"
# Puis executer ce script depuis le dossier du projet.

$port = 5268
$url = "http://localhost:$port/api/Ocr/process-batch"
Write-Host "Appel POST $url"

try {
    $resp = Invoke-RestMethod -Method Post -Uri $url -UseBasicParsing
    $resp | ConvertTo-Json -Depth 5 | Out-File -FilePath ./scripts/last-batch-result.json -Encoding utf8
    Write-Host "Traitement termine: $($resp.Count) fichier(s) sauvegarde(s)."
    Write-Host "Resultats dans TrainingData/RawResults/"
    Write-Host "Details dans scripts/last-batch-result.json"
}
catch {
    Write-Host "Erreur lors de l'appel : $_"
    Write-Host "Verifiez que l'API tourne sur http://localhost:$port"
    exit 1
}
