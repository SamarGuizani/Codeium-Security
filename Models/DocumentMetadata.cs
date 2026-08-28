namespace Codeium_Security.Models
{
    // Metadonnees generiques d'en-tete de releve/extrait bancaire, produites par
    // GenericDocumentMetadataExtractor - independant du parser de transactions
    // (BankDocumentParser), ne le modifie jamais et n'en depend pas.
    public class DocumentMetadata
    {
        // Reprise telle quelle du BankDocument.BankName deja calcule par BankDocumentParser
        // (voir DocumentProcessingService.ProcessFileAsync) : aucune detection dupliquee ici.
        public string? BankName { get; set; }
        public string? CustomerName { get; set; }
        public ExtractionPeriod? Period { get; set; }
    }

    public class ExtractionPeriod
    {
        public string? Start { get; set; }
        public string? End { get; set; }
    }
}
