using Codeium_Security.Frontend.Models;

namespace Codeium_Security.Frontend.Services;

// Simule le pipeline OCR -> Parser -> Export cote frontend uniquement,
// avec des delais artificiels. Aucune donnee reelle n'est traitee ici et
// aucun appel n'est fait vers le backend / OCR / BankDocumentParser reels.
public class MockAnalysisService
{
    private static readonly string[] Banques = { "Attijari Bank", "BIAT", "BH Bank", "Al Baraka", "Amen Bank", "UBCI", "Wifak Bank" };
    private readonly Random _random = new();

    public List<AnalysisStepModel> CreateSteps() => new()
    {
        new AnalysisStepModel { Nom = "Document reçu", Description = "Le fichier a été chargé côté frontend." },
        new AnalysisStepModel { Nom = "OCR", Description = "Simulation de la reconnaissance optique de caractères." },
        new AnalysisStepModel { Nom = "Détection du document", Description = "Identification simulée de la banque et du type de relevé." },
        new AnalysisStepModel { Nom = "Extraction des transactions", Description = "Simulation de l'extraction des lignes de transactions." },
        new AnalysisStepModel { Nom = "Validation", Description = "Vérification simulée de la cohérence des soldes." },
        new AnalysisStepModel { Nom = "Export Excel", Description = "Préparation simulée du fichier Excel." },
    };

    public async Task RunAsync(List<AnalysisStepModel> steps, Action onStepChanged)
    {
        foreach (var step in steps)
        {
            step.Status = StepStatus.EnCours;
            onStepChanged();
            await Task.Delay(650 + _random.Next(250));
            step.Status = StepStatus.Termine;
            onStepChanged();
        }
    }

    public BankResultViewModel GenerateMockResult(DocumentViewModel? document)
    {
        var banque = document?.Banque ?? Banques[_random.Next(Banques.Length)];
        var transactions = GenerateMockTransactions(12 + _random.Next(10));

        return new BankResultViewModel
        {
            Banque = banque,
            NumeroCompteMasque = $"TN59 **** **** **** {_random.Next(1000, 9999)}",
            Periode = "01/07/2026 – 31/07/2026",
            Statut = ProcessingStatus.Termine,
            Transactions = transactions
        };
    }

    private static readonly (string Libelle, bool IsDebit)[] LibellesMouvements =
    {
        ("Virement salaire", false),
        ("Paiement carte - Carrefour", true),
        ("Prélèvement STEG", true),
        ("Retrait GAB", true),
        ("Virement reçu", false),
        ("Paiement carte - Restaurant", true),
        ("Frais de tenue de compte", true),
        ("Prélèvement Topnet", true),
        ("Chèque n°00214", true),
        ("Paiement carte - Station essence", true),
        ("Virement loyer", false),
        ("Dépôt espèces", false),
    };

    private List<TransactionViewModel> GenerateMockTransactions(int count)
    {
        decimal solde = 4200m + _random.Next(-500, 500);
        var date = DateTime.Now.AddDays(-count);
        var list = new List<TransactionViewModel>();

        for (int i = 0; i < count; i++)
        {
            date = date.AddDays(1);
            var (libelle, isDebit) = LibellesMouvements[_random.Next(LibellesMouvements.Length)];
            decimal montant = Math.Round((decimal)(_random.NextDouble() * 400 + 10), 3);

            if (isDebit) solde -= montant; else solde += montant;

            list.Add(new TransactionViewModel
            {
                Date = date,
                Libelle = libelle,
                Debit = isDebit ? montant : null,
                Credit = isDebit ? null : montant,
                Solde = Math.Round(solde, 3)
            });
        }

        return list;
    }
}
