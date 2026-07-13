# Lance l'application et appelle l'endpoint batch
Write-Host "Lancer l'API (dotnet run) dans une autre console, puis exécuter ce script une fois l'API démarrée." 

$url = "http://localhost:5000/api/ocr/process-batch"
Write-Host "Appel POST $url"
try {
	$resp = Invoke-RestMethod -Method Post -Uri $url -UseBasicParsing
	$resp | ConvertTo-Json -Depth 5 | Out-File -FilePath ./scripts/last-batch-result.json -Encoding utf8
	Write-Host "Résultat sauvegardé dans scripts/last-batch-result.json"
}
catch {
	Write-Host "Erreur lors de l'appel : $_"
}
