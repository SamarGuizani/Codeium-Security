using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class AcademicDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Academic;

        public object Parse(string fullText, List<TextLine> lines)
        {
            return new AcademicDocument
            {
                Average = ExtractAverage(fullText)
            };
        }

        private decimal? ExtractAverage(string text)
        {
            var match = Regex.Match(text, @"Moyenne\s*:?\s*(\d+[.,]\d+)");
            if (!match.Success) return null;

            decimal.TryParse(match.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var avg);
            return avg;
        }
    }
}