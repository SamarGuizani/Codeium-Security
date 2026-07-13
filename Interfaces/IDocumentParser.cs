using Codeium_Security.Models;
using Codeium_Security.Services;

namespace Codeium_Security.Interfaces
{
    public interface IDocumentParser
    {
        DocumentType SupportedType { get; }
        object Parse(string fullText, List<TextLine> lines);
    }
}
