using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Factories
{
    public class DocumentParserFactory
    {
        private readonly IEnumerable<IDocumentParser> _parsers;

        public DocumentParserFactory(IEnumerable<IDocumentParser> parsers)
        {
            _parsers = parsers;
        }

        public IDocumentParser? GetParser(DocumentType type)
        {
            return _parsers.FirstOrDefault(p => p.SupportedType == type);
        }
    }
}