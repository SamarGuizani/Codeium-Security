using System.Globalization;
using System.Text.RegularExpressions;

namespace Codeium_Security.Tools
{
    // Programme de test isolé — ne touche JAMAIS à l'OCR, juste au texte
    // Usage : lance-le pour vérifier tes regex en 1 seconde au lieu de 2h
    public static class TestParser
    {
        private static readonly Regex AmountRegex =
            new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");

        public static void RunTests()
        {
            // Colle ici des extraits de texte réels de tes PDF (via l'endpoint /api/Ocr/text)
            var testCases = new[]
            {
                new { Bank = "BIAT", Text = "Compte : 59 10 02049 0 31012023 1 2035 ... Solde : 29.467,577" },
                new { Bank = "QNB",  Text = "Compte 2019-112548-001 ... Solde 69,395 ... Solde 45,000 (mouvement)" },
                new { Bank = "WAFA", Text = "Compte 0405822512301 ... Solde 3 943.810" },
            };

            foreach (var t in testCases)
            {
                var account = ExtractAccountNumber(t.Text);
                var balance = ExtractBalance(t.Text);
                Console.WriteLine($"{t.Bank} -> Compte: '{account}' | Solde: {balance}");
            }
        }

        private static string ExtractAccountNumber(string text)
        {
            var match = Regex.Match(text, @"Compte\s*:?\s*([0-9]+(?:[\s\-/][0-9]+){0,4})", RegexOptions.IgnoreCase);
            if (match.Success) return Regex.Replace(match.Groups[1].Value.Trim(), @"\s{2,}", " ");
            return "";
        }

        private static decimal ExtractBalance(string text)
        {
            var soldeMatches = Regex.Matches(text,
                @"Solde\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeMatches.Count > 0)
                return ParseAmount(soldeMatches[soldeMatches.Count - 1].Groups[1].Value);
            return 0;
        }

        private static decimal ParseAmount(string raw)
        {
            int lastSepIndex = -1;
            for (int i = raw.Length - 1; i >= 0; i--)
                if (raw[i] == '.' || raw[i] == ',' || raw[i] == ' ') { lastSepIndex = i; break; }

            if (lastSepIndex == -1)
            {
                decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var direct);
                return direct;
            }

            string integerPart = raw.Substring(0, lastSepIndex).Replace(".", "").Replace(",", "").Replace(" ", "");
            string decimalPart = raw.Substring(lastSepIndex + 1);
            decimal.TryParse(integerPart + "." + decimalPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var result);
            return result;
        }
    }
}