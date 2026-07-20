using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Codeium_Security
{
    public static class CompareResults
    {
        private const string CsvPath = "TrainingData/ground_truth.csv";
        private const string JsonFolder = "TrainingData/RawResults";

        public static void Run()
        {
            var rows = LoadGroundTruth();

            int total = 0;
            int accountChecked = 0, accountCorrect = 0;
            int balanceChecked = 0, balanceCorrect = 0;
            var details = new List<string>();

            foreach (var row in rows)
            {
                if (!row.TryGetValue("FileName", out var fileName) || string.IsNullOrWhiteSpace(fileName))
                    continue;

                total++;

                var jsonPath = Path.Combine(JsonFolder, Path.GetFileNameWithoutExtension(fileName) + ".json");
                if (!File.Exists(jsonPath))
                {
                    details.Add($"{fileName} -> JSON INTROUVABLE");
                    continue;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var document = doc.RootElement.GetProperty("Document");

                string predAccount = document.TryGetProperty("AccountNumber", out var accEl) ? accEl.GetString() ?? "" : "";
                string predBalance = document.TryGetProperty("Balance", out var balEl) ? balEl.ToString() : "";

                string trueAccount = row.GetValueOrDefault("account_number_true", "").Trim();
                string trueBalance = row.GetValueOrDefault("balance_true", "").Trim();

                bool? accountOk = null;
                if (!string.IsNullOrEmpty(trueAccount) && !trueAccount.Equals("NA", StringComparison.OrdinalIgnoreCase) && trueAccount != "-")
                {
                    accountChecked++;
                    accountOk = NormalizeAccount(trueAccount) == NormalizeAccount(predAccount);
                    if (accountOk == true) accountCorrect++;
                }

                bool? balanceOk = null;
                if (!string.IsNullOrEmpty(trueBalance) && !trueBalance.Equals("NA", StringComparison.OrdinalIgnoreCase) && trueBalance != "-")
                {
                    balanceChecked++;
                    var trueNum = NormalizeNumber(trueBalance);
                    var predNum = NormalizeNumber(predBalance);
                    balanceOk = trueNum.HasValue && predNum.HasValue && Math.Abs(trueNum.Value - predNum.Value) < 0.01m;
                    if (balanceOk == true) balanceCorrect++;
                }

                details.Add($"{fileName} | Compte: {Status(accountOk)} (vrai={trueAccount} / trouvé={predAccount}) | " +
                            $"Solde: {Status(balanceOk)} (vrai={trueBalance} / trouvé={predBalance})");
            }

            Console.WriteLine(new string('=', 60));
            Console.WriteLine($"Total documents dans le CSV : {total}");
            Console.WriteLine($"Numéro de compte : {accountCorrect}/{accountChecked} corrects " +
                               $"({(accountChecked > 0 ? (double)accountCorrect / accountChecked * 100 : 0):F1}%)");
            Console.WriteLine($"Solde : {balanceCorrect}/{balanceChecked} corrects " +
                               $"({(balanceChecked > 0 ? (double)balanceCorrect / balanceChecked * 100 : 0):F1}%)");
            Console.WriteLine(new string('=', 60));
            Console.WriteLine();
            Console.WriteLine("Détail par fichier :");
            foreach (var line in details)
                Console.WriteLine(line);
        }

        private static string Status(bool? ok) => ok switch
        {
            true => "OK",
            false => "ERREUR",
            null => "N/A"
        };

        // Enlève espaces et tirets, met en minuscule -> pour comparer les numéros de compte
        private static string NormalizeAccount(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            value = value.Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"[\s\-/]", "");
            return value;
        }

        // Convertit "2 281,290" ou "-13 825.390" ou "170000.000" en vrai nombre decimal, peu importe le format
        private static decimal? NormalizeNumber(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            value = value.Trim();
            value = Regex.Replace(value, @"[\s]", ""); // enlève tous les espaces (milliers)

            // Remplace la virgule décimale par un point, uniquement si c'est la dernière séparation
            int lastComma = value.LastIndexOf(',');
            int lastDot = value.LastIndexOf('.');

            if (lastComma > lastDot)
            {
                // virgule = séparateur décimal (format tunisien/français)
                value = value.Replace(".", "").Replace(",", ".");
            }
            else if (lastDot > lastComma)
            {
                // point = séparateur décimal (format anglais), enlève les virgules de milliers
                value = value.Replace(",", "");
            }

            return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        private static List<Dictionary<string, string>> LoadGroundTruth()
        {
            var rows = new List<Dictionary<string, string>>();
            var lines = ParseCsvLines(CsvPath);
            if (lines.Count == 0) return rows;

            var headers = lines[0];

            for (int i = 1; i < lines.Count; i++)
            {
                var values = lines[i];
                var row = new Dictionary<string, string>();

                for (int j = 0; j < headers.Count && j < values.Count; j++)
                    row[headers[j].Trim()] = values[j].Trim();

                rows.Add(row);
            }

            return rows;
        }

        // Lit le CSV en gérant correctement les guillemets ajoutés par Excel autour des valeurs
        // Lit le CSV entier d'un coup, en gérant les guillemets Excel
        // (y compris quand une cellule contient un retour à la ligne à l'intérieur)
        private static List<List<string>> ParseCsvLines(string path)
        {
            var result = new List<List<string>>();
            var text = File.ReadAllText(path);

            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // "" à l'intérieur de guillemets = un seul " littéral (échappement Excel)
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c); // même un \n ou \r est gardé tel quel, PAS une nouvelle ligne
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ';')
                    {
                        fields.Add(current.ToString());
                        current.Clear();
                    }
                    else if (c == '\r')
                    {
                        // ignoré, on gère la fin de ligne avec \n
                    }
                    else if (c == '\n')
                    {
                        fields.Add(current.ToString());
                        current.Clear();

                        // on ignore les lignes complètement vides
                        if (fields.Count > 1 || !string.IsNullOrWhiteSpace(fields[0]))
                            result.Add(fields);

                        fields = new List<string>();
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
            }

            // dernière ligne si le fichier ne finit pas par \n
            fields.Add(current.ToString());
            if (fields.Count > 1 || !string.IsNullOrWhiteSpace(fields[0]))
                result.Add(fields);

            return result;
        }
    }
}