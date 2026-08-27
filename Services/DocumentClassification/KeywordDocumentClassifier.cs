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
            // "RELEV" (sans le "E"/"É" final) plutôt que "RELEVE" : l'OCR d'un "Relevé" accentué
            // perd parfois l'accent d'une façon qui casse la comparaison exacte (ex. lu tel quel,
            // "RELEVÉ" en majuscules reste "RELEVÉ", pas "RELEVE") - le radical sans dernière
            // lettre matche les deux formes. "DEBIT" ajouté : un en-tête de colonne bancaire
            // standard, jamais un faux positif pour Facture/Reçu/Relevé de notes, qui aide à
            // départager les relevés dont le vocabulaire des libellés de transaction (paiement,
            // caisse, espèces, reçu) chevauche par ailleurs les mots-clés Reçu ci-dessous.
            AddScoreIfContains(scores, text, DocumentType.Bank,
                "RELEV", "COMPTE", "SOLDE", "RIB", "IBAN", "BANQUE", "DEBIT");

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