namespace Codeium_Security.Frontend.Models;

// Statuts utilises par les modeles frontend (documents, historique, resultats).
// Ne pas confondre avec les enums du backend : ce fichier est independant.
public enum ProcessingStatus
{
    EnAttente,
    EnCours,
    Termine,
    Erreur
}

public enum StepStatus
{
    Attente,
    EnCours,
    Termine
}
