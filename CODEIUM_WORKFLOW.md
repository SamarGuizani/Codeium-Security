Traitement en masse (Batch) - Instructions

But
- Exécuter le pipeline OCR + parsing sur tous les fichiers présents dans Images/ et sauvegarder les résultats JSON dans TrainingData/RawResults/.

Endpoint
- POST /api/ocr/process-batch
- Retourne: { Count: n, Files: ["TrainingData/RawResults/foo.json", ...] }

Recommandations
- Pour un grand volume (>50 fichiers) préférer exécution hors HTTP (script PowerShell) car le traitement peut être long.
- Eviter d'exécuter 2 batches simultanément pour ne pas avoir de conflit d'écriture.
- Vérifier les permissions d'écriture sur TrainingData/RawResults/.

Script PowerShell minimal
```powershell
# Depuis la racine du projet
dotnet run --project "Codeium Security.csproj"
# puis appeler l'endpoint via curl ou Postman
curl -X POST http://localhost:5000/api/ocr/process-batch
```

Fichiers générés
- TrainingData/RawResults/<nom_de_fichier>.json
- En cas d'erreur: TrainingData/RawResults/<nom_de_fichier>.error.txt
