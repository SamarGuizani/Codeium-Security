using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class InvoiceDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Invoice;

        public object Parse(string fullText, List<TextLine> lines)
        {
            return new InvoiceDocument
            {
                InvoiceNumber = ExtractInvoiceNumber(fullText),
                TotalTTC = ExtractTotal(fullText),
                Currency = fullText.Contains("TND") ? "TND" : ""
            };
        }

        private string ExtractInvoiceNumber(string text)
        {
            var match = Regex.Match(text, @"(?:Facture|Invoice)\s*N?°?\s*:?\s*(\S+)");
            return match.Success ? match.Groups[1].Value.Trim() : "";
        }

        private decimal ExtractTotal(string text)
        {
            var match = Regex.Match(text, @"Total\s*TTC\s*:?\s*(-?\d[\d\s]*[.,]\d{2,3})");
            if (!match.Success) return 0;

            var cleaned = match.Groups[1].Value.Replace(" ", "").Replace(',', '.');
            decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal total);
            return total;
        }
    }
}