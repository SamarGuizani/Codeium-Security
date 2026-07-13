using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentClassification
{
    public interface  IDocumentClassifier
    {

        DocumentType Classify(string fullText);
    }
}
