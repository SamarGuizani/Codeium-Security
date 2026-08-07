using Codeium_Security.OCR;

namespace Codeium_Security.Interfaces
{
    // Abstraction commune PDF / image : le parser ne sait jamais quelle implementation
    // a produit l'OcrResult, il recoit toujours la meme forme (FullText + List<OcrWord>).
    public interface IDocumentExtractor
    {
        bool CanHandle(string fileName);
        Task<OcrResult> ExtractAsync(string filePath, string originalFileName);
    }
}
