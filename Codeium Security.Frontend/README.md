# Codeium Security.Frontend

Interface frontend du projet **Codeium Security**, construite en **C# + Blazor WebAssembly (.NET 8)**.

Ce projet est **totalement indépendant** du backend (`Codeium Security.csproj`). Il ne le référence pas, ne l'appelle pas, et peut être lancé seul.

## Comment lancer le frontend

Depuis la racine de la solution :

```bash
cd "Codeium Security.Frontend"
dotnet run
```

Ou via Visual Studio : clic droit sur `Codeium Security.Frontend` → **Définir comme projet de démarrage** → F5.

L'application s'ouvre sur `http://localhost:5209` (voir `Properties/launchSettings.json`).

## Structure du projet

```
Codeium Security.Frontend/
├── Layout/            MainLayout, Sidebar, Header, SidebarLink (+ .razor.css scopés)
├── Components/         Icon, StatCard, StatusBadge, StepItem, ToastContainer,
│                        ConfirmDialog, EmptyState (composants réutilisables)
├── Pages/              Dashboard, Documents, Analyse, Resultats, Historique,
│                        Parametres (une page par route)
├── Models/              DocumentViewModel, TransactionViewModel, DashboardStats,
│                        AnalysisStepModel, HistoryItem, BankResultViewModel, Enums
├── Services/            MockDocumentService, MockAnalysisService, MockHistoryService,
│                        AppStateService, ToastService, ThemeService
└── wwwroot/css/app.css  Design system (tokens couleur, composants, responsive)
```

## Pages disponibles

| Route | Page | Contenu |
|---|---|---|
| `/` | Dashboard | Statistiques mock + documents récents |
| `/documents` | Documents | Upload (drag & drop ou clic), simulation d'analyse |
| `/analyse` | Analyse | Progression simulée du pipeline (6 étapes animées) |
| `/resultats` | Résultats | Résultat bancaire mock : infos + tableau de transactions (recherche, filtre, pagination) |
| `/historique` | Historique | Liste fictive de documents traités (voir / télécharger / supprimer) |
| `/parametres` | Paramètres | Langue, thème clair/sombre, notifications, densité d'affichage |

## Données MOCK

Toutes les données affichées sont fictives et générées par les services du dossier `Services/` :

- `MockDocumentService` → statistiques du dashboard, liste de documents récents.
- `MockAnalysisService` → étapes d'analyse simulées (avec délais artificiels via `Task.Delay`) et génération d'un relevé bancaire fictif avec transactions.
- `MockHistoryService` → historique fictif conservé en mémoire pour la session.
- `AppStateService` → état partagé en mémoire entre les pages (document sélectionné, résultat courant) — aucune persistance, aucun réseau.

## Où se trouve la simulation

- **Upload** (`Pages/Documents.razor`) : le fichier est lu localement via `InputFile` (métadonnées nom/taille/type uniquement, aucun envoi). Le bouton *Analyser* redirige vers `/analyse` sans appel réseau.
- **Analyse** (`Pages/Analyse.razor`) : `MockAnalysisService.RunAsync` fait progresser 6 étapes avec des délais simulés (~650-900ms chacune).
- **Export Excel** (`Pages/Resultats.razor`) : le bouton affiche une notification *« Export simulé — connexion backend à implémenter »* au lieu d'appeler une API.
- **Téléchargement** (`Pages/Historique.razor`) : idem, notification simulée.

## Comment ce projet est séparé du backend

- Projet `.csproj` distinct (`Microsoft.NET.Sdk.BlazorWebAssembly`), sans référence de projet vers `Codeium Security.csproj`.
- Aucun `HttpClient` configuré pour appeler une API.
- Aucune URL, endpoint ou modèle backend référencé.
- Les modèles frontend (`Models/`) sont des ViewModels propres au frontend, distincts des modèles backend (`BankDocument`, `Transaction`, etc.).
- Le backend (`Codeium Security.csproj`) a dû être légèrement modifié : une exclusion de glob a été ajoutée (`Codeium Security.Frontend\**`) pour empêcher le SDK Web du backend de tenter de compiler les fichiers du frontend — identique au pattern déjà utilisé pour `Codeium Security.Tests`. Aucune logique métier, Controller, Model, Parser ou OCR n'a été touché.

---

## TODO — Backend Integration

La connexion réelle au backend n'est **pas** implémentée volontairement. Voici, à titre indicatif seulement, les fichiers à modifier pour brancher le frontend sur l'API existante (`OcrController`) :

1. **`Program.cs`** — enregistrer un `HttpClient` nommé pointant vers l'URL du backend (ex. `builder.Services.AddHttpClient("Api", c => c.BaseAddress = new Uri("https://localhost:xxxx"))`).
2. **Créer un nouveau service** (ex. `Services/ApiDocumentService.cs`) qui utilise ce `HttpClient` pour :
   - envoyer le fichier sélectionné (`multipart/form-data`) vers l'endpoint du `OcrController` ;
   - désérialiser la réponse (`DocumentProcessingResult` / `BankDocument` côté backend) vers les ViewModels frontend (`BankResultViewModel`, `TransactionViewModel`).
3. **`Pages/Documents.razor`** — remplacer la redirection simulée du bouton *Analyser* par un appel réel à ce nouveau service.
4. **`Pages/Analyse.razor`** — remplacer `MockAnalysisService.RunAsync` par un suivi réel de la progression (polling ou réponse unique selon ce que propose l'API).
5. **`Pages/Resultats.razor`** — remplacer le bouton *Exporter Excel* simulé par un appel à l'endpoint d'export réel (probablement un téléchargement de fichier via `HttpClient.GetByteArrayAsync` + déclenchement du téléchargement navigateur).
6. **CORS côté backend** — si le frontend et le backend tournent sur des ports différents, il faudra activer CORS dans `Program.cs` du backend pour autoriser les requêtes du frontend.

Architecture cible :

```
Frontend → HTTP API (OcrController) → OCR → BankDocumentParser → Excel
```
