using Codeium_Security.Models;

namespace Codeium_Security.Tests
{
    // Reproduit la forme du JSON ecrit par OcrController.SaveResultAsJson pour UN compte
    // (branche "result.Document is BankDocument bankDoc && bankDoc.Accounts.Count > 0").
    // Les fichiers dont le Document n'a produit aucun compte sont serialises differemment
    // (DocumentProcessingResult complet, sans propriete "Account") et ne sont pas geres ici :
    // un tel fichier ne peut de toute facon pas servir de reference "comportement qui marche".
    public class SnapshotFile
    {
        public string FileName { get; set; } = "";
        public string DetectedType { get; set; } = "";
        public int PageCount { get; set; }
        public float OcrConfidence { get; set; }
        public bool NeedsReview { get; set; }
        public string BankName { get; set; } = "";
        public BankAccountSection Account { get; set; } = new();
    }
}
