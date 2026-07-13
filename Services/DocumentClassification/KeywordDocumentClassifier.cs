using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentClassification
{
    public class KeywordDocumentClassifier : IDocumentClassifier
    {
        public DocumentType Classify(string fullText)
        {
            string text = fullText.ToUpperInvariant();

            var scores = new Dictionary<DocumentType, int>
            {
                { DocumentType.Bank, 0 },
                { DocumentType.Invoice, 0 },
                { DocumentType.Receipt, 0 },
                { DocumentType.Academic, 0 }
            };

            // Chaque mot-clé trouvé ajoute 1 point à sa catégorie
            AddScoreIfContains(scores, text, DocumentType.Bank,
                "RELEVE", "COMPTE", "SOLDE", "RIB", "IBAN", "BANQUE");

            AddScoreIfContains(scores, text, DocumentType.Invoice,
                "FACTURE", "INVOICE", "TOTAL HT", "TOTAL TTC", "TVA", "FOURNISSEUR");

            AddScoreIfContains(scores, text, DocumentType.Receipt,
                "TICKET", "RECU", "REÇU", "CAISSE", "PAIEMENT", "MERCI DE VOTRE VISITE", "ESPECES");

            AddScoreIfContains(scores, text, DocumentType.Academic,
                "RELEVE DE NOTES", "UNIVERSITE", "FACULTE", "SEMESTRE", "MOYENNE",
                "MATIERE", "CREDIT ECTS", "ETUDIANT", "ISIMM", "INSCRIPTION");

            // On prend la catégorie avec le score le plus élevé (si > 0)
            var best = scores.OrderByDescending(s => s.Value).First();

            return best.Value > 0 ? best.Key : DocumentType.Unknown;
        }

        private void AddScoreIfContains(Dictionary<DocumentType, int> scores, string text, DocumentType type, params string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                if (text.Contains(keyword))
                    scores[type]++;
            }
        }
    }
}