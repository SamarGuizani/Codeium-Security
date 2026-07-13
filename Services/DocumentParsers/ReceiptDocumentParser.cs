using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class ReceiptDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Receipt;

        public object Parse(string fullText, List<TextLine> lines)
        {
            return new ReceiptDocument
            {
                Total = ExtractTotal(fullText),
                Currency = fullText.Contains("TND") ? "TND" : ""
            };
        }

        private decimal ExtractTotal(string text)
        {
            var matches = Regex.Matches(text, @"-?\d[\d\s]*[.,]\d{2,3}");
            if (matches.Count == 0) return 0;

            var cleaned = matches[matches.Count - 1].Value.Replace(" ", "").Replace(',', '.');
            decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal total);
            return total;
        }
    }
}