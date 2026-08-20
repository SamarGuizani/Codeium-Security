using Codeium_Security.Frontend.Models;

namespace Codeium_Security.Frontend.Services;

// Etat partage entre les pages du frontend (document en cours de simulation,
// resultat courant). Stockage en memoire uniquement, rien n'est persiste
// ni envoye a un serveur.
public class AppStateService
{
    public DocumentViewModel? SelectedDocument { get; set; }
    public List<AnalysisStepModel> AnalysisSteps { get; set; } = new();
    public BankResultViewModel? CurrentResult { get; set; }
    public bool AnalysisRunning { get; set; }
    public bool AnalysisCompleted { get; set; }

    public event Action? OnChange;
    public void NotifyChange() => OnChange?.Invoke();
}
