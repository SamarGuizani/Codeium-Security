using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentClassification
{
    public class KeywordDocumentClassifier : IDocumentClassifier
    {
        public DocumentType Classify(string fullText)
        {
            string text = fullText.ToUpperInvariant();

            bool looksLikeBank =
                text.Contains("RELEVE") || text.Contains("COMPTE") ||
                text.Contains("SOLDE") || text.Contains("RIB") ||
                (text.Contains("DEBIT") && text.Contains("CREDIT"));

            bool looksLikeInvoice =
                text.Contains("FACTURE") || text.Contains("INVOICE") ||
                text.Contains("TOTAL HT") || text.Contains("TOTAL TTC") ||
                text.Contains("TVA");

            bool looksLikeReceipt =
                text.Contains("TICKET") || text.Contains("RECU") ||
                text.Contains("REÇU") || text.Contains("CAISSE");

            if (looksLikeBank) return DocumentType.Bank;
            if (looksLikeInvoice) return DocumentType.Invoice;
            if (looksLikeReceipt) return DocumentType.Receipt;

            return DocumentType.Unknown;
        }
    }
}