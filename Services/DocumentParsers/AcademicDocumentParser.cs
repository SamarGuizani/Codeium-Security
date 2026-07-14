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
                StudentName = ExtractAfterLabel(fullText, "Nom et prénom"),
                Cin = ExtractAfterLabel(fullText, @"Cin/Identifiant unique", @"[0-9]+"),
                Email = ExtractAfterLabel(fullText, "E-mail", @"[^\s]+@[^\s]+"),
                Phone = ExtractAfterLabel(fullText, "Téléphone", @"[0-9]+"),
                University = fullText.Contains("Institut Supérieur d'Informatique") || fullText.Contains(" ISI ")
                    ? "Institut Supérieur d'Informatique (ISI)"
                    : "",
                MasterTitle = ExtractAfterLabel(fullText, "Intitulé"),
                Parcours = ExtractAfterLabel(fullText, "Parcours"),
                Level = ExtractAfterLabel(fullText, "Niveau"),
                AcademicYear = ExtractAfterLabel(fullText, "Année universitaire"),
                Average = ExtractAverage(fullText)
            };
        }

        private string ExtractAfterLabel(string text, string label, string? valuePattern = null)
        {
            string pattern = valuePattern != null
                ? $@"{label}\s*:?\s*({valuePattern})"
                : $@"{label}\s*:?\s*(.+)";

            var match = Regex.Match(text, pattern);
            if (!match.Success) return "";

            string value = match.Groups[1].Value.Trim();
            int newlineIndex = value.IndexOfAny(new[] { '\r', '\n' });
            if (newlineIndex > -1) value = value.Substring(0, newlineIndex).Trim();

            return value;
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