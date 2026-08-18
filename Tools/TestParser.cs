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

    // ─────────────────────────────────────────────────────────────────────────
    // Classe SŒUR de TestParser (pas imbriquée dedans), même style : script
    // isolé, aucune dépendance à l'OCR, vérifie juste les regex/formule.
    // ─────────────────────────────────────────────────────────────────────────
    public static class TestBteParser
    {
        private static readonly Regex AmountRegex =
            new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");

        public static void RunTests()
        {
            TestBankNameDetection();
            TestReferenceNeverTreatedAsAmount();
            TestSoldeSignParsing();
            TestAccountingControl();
        }

        // ── 1. Détection du nom de banque (cause du bug #8) ────────────────────
        private static void TestBankNameDetection()
        {
            var testCases = new[]
            {
                new { Label = "Nom complet",      Text = "BANQUE DE TUNISIE ET DES EMIRATS - Relevé de compte", Expected = "Banque de Tunisie et des Emirats (BTE)" },
                new { Label = "Sigle seul",        Text = "BTE - Extrait de compte N° 123456", Expected = "Banque de Tunisie et des Emirats (BTE)" },
                new { Label = "Sigle en minuscule", Text = "bte - relevé mensuel", Expected = "Banque de Tunisie et des Emirats (BTE)" },
                new { Label = "Autre banque (BNA)", Text = "BANQUE NATIONALE AGRICOLE - Relevé", Expected = "Banque Nationale Agricole (BNA)" },
                new { Label = "Faux positif potentiel", Text = "OBTENU LE 12/01/2026 PAR LE CLIENT", Expected = "" }, // "BTE" n'apparaît nulle part ici en tant que mot
            };

            Console.WriteLine("=== Détection bankName ===");
            foreach (var t in testCases)
            {
                string result = ExtractBankNameCopy(t.Text);
                string status = result == t.Expected ? "PASS" : "FAIL";
                Console.WriteLine($"[{status}] {t.Label} -> '{result}' (attendu: '{t.Expected}')");
            }
        }

        // Copie locale de la nouvelle logique ajoutée dans ExtractBankName pour BTE.
        private static string ExtractBankNameCopy(string text)
        {
            if (text.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(text, @"\bBTE\b", RegexOptions.IgnoreCase))
            {
                return "Banque de Tunisie et des Emirats (BTE)";
            }

            var knownBanks = new (string Keyword, string FullName)[]
            {
                ("BNA", "Banque Nationale Agricole (BNA)"),
                ("BIAT", "Banque Internationale Arabe de Tunisie (BIAT)"),
                ("BH", "Banque de l'Habitat (BH)"),
                ("BTK", "Banque Tuniso-Koweitienne (BTK)"),
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }

        // ── 2. Une référence bancaire (7 chiffres, sans séparateur décimal) ────
        //      ne doit JAMAIS matcher AmountRegex, donc ne peut jamais être
        //      confondue avec un montant par la classification par colonne.
        private static void TestReferenceNeverTreatedAsAmount()
        {
            Console.WriteLine("\n=== Référence vs montant ===");
            string[] references = { "1981058", "1981072", "1290301", "1290302", "1290303" };

            foreach (var r in references)
            {
                bool matchesAsAmount = AmountRegex.IsMatch(r);
                string status = !matchesAsAmount ? "PASS" : "FAIL";
                Console.WriteLine($"[{status}] '{r}' ne doit pas matcher AmountRegex -> matches={matchesAsAmount}");
            }

            // Vrais montants : doivent matcher.
            string[] amounts = { "10 000.000", "18 500.000", "4 691.000", "11 373.574", "6 400.000" };
            foreach (var a in amounts)
            {
                bool matchesAsAmount = AmountRegex.IsMatch(a);
                string status = matchesAsAmount ? "PASS" : "FAIL";
                Console.WriteLine($"[{status}] '{a}' doit matcher AmountRegex -> matches={matchesAsAmount}");
            }
        }

        // ── 3. CR / DB toujours rattaché au solde, jamais fusionné dans un montant
        private static void TestSoldeSignParsing()
        {
            Console.WriteLine("\n=== Sens du solde (CR/DB) ===");
            var testCases = new[]
            {
                new { Text = "9 937.083", Sign = "CR", ExpectedSigned = 9937.083m },
                new { Text = "17.834",    Sign = "DB", ExpectedSigned = -17.834m },
                new { Text = "3995.210",  Sign = "CR", ExpectedSigned = 3995.210m },
            };

            foreach (var t in testCases)
            {
                decimal val = ParseAmount(t.Text);
                decimal signed = t.Sign.Equals("DB", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(val) : Math.Abs(val);
                string status = signed == t.ExpectedSigned ? "PASS" : "FAIL";
                Console.WriteLine($"[{status}] '{t.Text} {t.Sign}' -> {signed} (attendu: {t.ExpectedSigned})");
            }
        }

        // ── 4. Contrôle comptable : solde_final = solde_initial + credit - debit ──
        private static void TestAccountingControl()
        {
            Console.WriteLine("\n=== Contrôle comptable ===");

            decimal soldeInitial = 3995.210m;   // CR
            decimal totalDebit = 119443.044m;
            decimal totalCredit = 115430.000m;
            decimal expectedSoldeFinal = -17.834m; // soit 17.834 DB

            decimal computed = soldeInitial + totalCredit - totalDebit;
            string status = Math.Abs(computed - expectedSoldeFinal) < 0.005m ? "PASS" : "FAIL";
            Console.WriteLine($"[{status}] {soldeInitial} + {totalCredit} - {totalDebit} = {computed} (attendu: {expectedSoldeFinal}, soit 17.834 DB)");
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
