using Codeium_Security.Models;
using Codeium_Security.Services;

namespace Codeium_Security.Services.DocumentMetadataExtraction
{
    public interface IDocumentMetadataExtractor
    {
        DocumentMetadata Extract(string fullText, List<TableRow> rows);
    }
}
