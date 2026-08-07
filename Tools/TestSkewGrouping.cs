using System.Text.Json;
using Codeium_Security.OCR;

namespace Codeium_Security.Tools
{
    // Verification isolee (AUCUNE modification de DocumentAnalysisEngine.cs) : valide
    // l'algorithme de regroupement propose sur des coordonnees OCR REELLES, a la fois sur un
    // petit sous-ensemble propre (4 lignes de transaction), sur la page COMPLETE (en-tete,
    // bandeau, pied de page inclus), et sur une zone dense connue pour avoir ete corrompue par
    // du texte parasite en marge (Left tres eloigne des vraies colonnes du tableau).
    public static class TestSkewGrouping
    {
        // Tolerance reellement utilisee en production : EstimateVerticalTolerance() est
        // plafonnee a VerticalTolerance (defaut 10), donc jamais > 10 aujourd'hui.
        private const int ProductionTolerance = 15;

        public static void RunTests()
        {
            RunSmallFixtureTest();
            RunFullPageFixtureTest();
        }

        private static void RunSmallFixtureTest()
        {
            var words = new List<OcrWord>
            {
                // Ligne reelle X1 : 15/06 PAIEMENT EFFET 01965314 12062026 2.076,945
                new() { Text = "2.076;945", Left = 3184, Top = 3010, Bottom = 3073 },
                new() { Text = "12062026",  Left = 2366, Top = 3020, Bottom = 3080 },
                new() { Text = "01965314",  Left = 1815, Top = 3032, Bottom = 3086 },
                new() { Text = "15",        Left = 200,  Top = 3037, Bottom = 3088 },
                new() { Text = "06",        Left = 374,  Top = 3041, Bottom = 3086 },
                new() { Text = "EFFET",     Left = 1100, Top = 3043, Bottom = 3094 },
                new() { Text = "PAIEMENT",  Left = 600,  Top = 3047, Bottom = 3096 },

                // Ligne reelle X2 : 15/06 PAIEMENT EFFET 26957445 12062026 4,165
                new() { Text = "4,165",     Left = 3407, Top = 3099, Bottom = 3163 },
                new() { Text = "12062026",  Left = 2365, Top = 3109, Bottom = 3170 },
                new() { Text = "26957445",  Left = 1812, Top = 3122, Bottom = 3175 },
                new() { Text = "15",        Left = 199,  Top = 3129, Bottom = 3180 },
                new() { Text = "06",        Left = 371,  Top = 3131, Bottom = 3182 },
                new() { Text = "EFFET",     Left = 1099, Top = 3132, Bottom = 3183 },
                new() { Text = "PAIEMENT",  Left = 599,  Top = 3137, Bottom = 3186 },

                // Ligne reelle X3 : 15/06 PAIEMENT EFFET 26957445 12062026 1.400,000
                new() { Text = "1.400,000", Left = 3187, Top = 3187, Bottom = 3252 },
                new() { Text = "12062026",  Left = 2364, Top = 3197, Bottom = 3258 },
                new() { Text = "26957445",  Left = 1813, Top = 3210, Bottom = 3262 },
                new() { Text = "15",        Left = 196,  Top = 3218, Bottom = 3268 },
                new() { Text = "06",        Left = 372,  Top = 3219, Bottom = 3270 },
                new() { Text = "EFFET",     Left = 1100, Top = 3220, Bottom = 3272 },
                new() { Text = "PAIEMENT",  Left = 599,  Top = 3226, Bottom = 3274 },

                // Ligne reelle X4 (partielle) : 17/06 REDRESSEMENT 50150626 15062026 5,000
                new() { Text = "5,000",     Left = 3405, Top = 3277, Bottom = 3341 },
                new() { Text = "15062026",  Left = 2364, Top = 3287, Bottom = 3347 },
                new() { Text = "50150626",  Left = 1812, Top = 3298, Bottom = 3353 },
            };

            var lines = GroupWordsLocalSkew(words, ProductionTolerance);

            Console.WriteLine($"[TestSkewGrouping] (petit jeu, tol={ProductionTolerance}) Lignes obtenues : {lines.Count} (attendu : 4)");
            for (int i = 0; i < lines.Count; i++)
            {
                string text = string.Join(" ", lines[i].OrderBy(w => w.Left).Select(w => w.Text));
                Console.WriteLine($"[TestSkewGrouping] Ligne {i}: {text}");
            }

            bool pass = lines.Count == 4;
            Console.WriteLine(pass
                ? "[TestSkewGrouping] PASS (petit jeu) : 4 lignes reconstruites correctement"
                : $"[TestSkewGrouping] FAIL (petit jeu) : {lines.Count} lignes au lieu de 4");
        }

        private static void RunFullPageFixtureTest()
        {
            string fixturePath = Path.Combine("TrainingData", "TestFixtures", "biatscanner_full_words.json");
            if (!File.Exists(fixturePath))
            {
                Console.WriteLine($"[TestSkewGrouping] (page complete) fixture introuvable ({fixturePath}), test ignore.");
                return;
            }

            var json = File.ReadAllText(fixturePath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var results = JsonSerializer.Deserialize<List<ResultFixture>>(json, options);
            var words = results?.FirstOrDefault()?.Words?.Select(w => new OcrWord
            {
                Text = w.Text,
                Left = w.Left,
                Top = w.Top,
                Right = w.Right,
                Bottom = w.Bottom
            }).ToList() ?? new List<OcrWord>();

            Console.WriteLine($"[TestSkewGrouping] (page complete) Mots charges : {words.Count}");

            var lines = GroupWordsLocalSkew(words, ProductionTolerance);
            Console.WriteLine($"[TestSkewGrouping] (page complete) Lignes obtenues : {lines.Count}");

            bool notExploded = lines.Count < words.Count / 2;
            Console.WriteLine(notExploded
                ? $"[TestSkewGrouping] PASS (page complete) : pas d'explosion ({lines.Count} lignes pour {words.Count} mots)"
                : $"[TestSkewGrouping] FAIL (page complete) : explosion probable ({lines.Count} lignes pour {words.Count} mots)");

            var matchingLines = lines.Where(l => l.Any(w => w.Text == "26957445")).ToList();
            Console.WriteLine($"[TestSkewGrouping] (page complete) Lignes contenant '26957445' : {matchingLines.Count} (attendu : 2, une par transaction)");
            foreach (var line in matchingLines)
            {
                string text = string.Join(" ", line.OrderBy(w => w.Left).Select(w => w.Text));
                Console.WriteLine($"[TestSkewGrouping] (page complete) -> {text}");
            }

            // Zone dense connue pour avoir ete corrompue par du texte parasite en marge (Left
            // ~4789-4808, tres eloigne des vraies colonnes ~150-4600) : REDRESSEMENT + PRELEVEMENT
            // BANCAIRE rapproches (6 lignes reelles distinctes, ~88px d'ecart chacune). Avant
            // exclusion du bruit de marge, elles fusionnaient en une seule ligne geante.
            var denseZoneLines = lines
                .Where(l => l.Any(w => w.Top > 4150 && w.Top < 4700))
                .Where(l => l.Any(w => w.Text is "REDRESSEMENT" or "PRELEVEMENT"))
                .ToList();
            Console.WriteLine($"[TestSkewGrouping] (page complete) Lignes REDRESSEMENT/PRELEVEMENT zone dense : {denseZoneLines.Count} (attendu : 6)");
            foreach (var line in denseZoneLines)
            {
                string text = string.Join(" ", line.OrderBy(w => w.Left).Select(w => w.Text));
                Console.WriteLine($"[TestSkewGrouping] (zone dense) -> {text}");
            }
        }

        // Algorithme candidat (isole ici, PAS encore dans DocumentAnalysisEngine.cs).
        // Etape 0 : separe le texte parasite de marge (Left tres eloigne du contenu tabulaire
        // principal -- ex. tampon/annotation illisible) du contenu "coeur" du tableau, via le
        // plus grand ecart dans la distribution des Left, a condition qu'il n'isole qu'une
        // minorite de mots (jamais une vraie colonne). Le bruit de marge est regroupe simplement
        // (Top brut, sans reconciliation de skew) pour ne jamais contaminer le tableau reel.
        // Passe 1 (sur le coeur) : regroupement grossier par Top brut, qui sur-fragmente les
        // lignes inclinees mais ne fusionne jamais deux vraies lignes distinctes.
        // Passe 2 (sur le coeur) : tente de fusionner un fragment avec le fragment SUIVANT
        // uniquement, via un ajustement lineaire LOCAL a ces deux fragments (jamais a la page
        // entiere) : si le residu maximal apres correction de pente reste <= tolerance, on
        // fusionne.
        private static List<List<OcrWord>> GroupWordsLocalSkew(List<OcrWord> words, int verticalTolerance)
        {
            var (core, margin) = SplitMarginNoise(words);

            var lines = GroupCoreWithLocalSkew(core, verticalTolerance);
            lines.AddRange(GroupByRawTopOnly(margin, verticalTolerance));

            return lines;
        }

        private static (List<OcrWord> core, List<OcrWord> margin) SplitMarginNoise(List<OcrWord> words)
        {
            if (words.Count < 10) return (words, new List<OcrWord>());

            var distinctLefts = words.Select(w => w.Left).Distinct().OrderBy(x => x).ToList();
            int splitLeft = -1;
            int maxGap = 0;

            for (int i = 1; i < distinctLefts.Count; i++)
            {
                int gap = distinctLefts[i] - distinctLefts[i - 1];
                int wordsToRight = words.Count(w => w.Left >= distinctLefts[i]);

                // Ne considere ce point de coupure que s'il isole une minorite de mots (bruit
                // de marge), jamais une vraie colonne du tableau qui contiendrait beaucoup de mots.
                if (gap > maxGap && gap > 80 && wordsToRight <= Math.Max(1, words.Count / 10))
                {
                    maxGap = gap;
                    splitLeft = distinctLefts[i];
                }
            }

            if (splitLeft < 0) return (words, new List<OcrWord>());

            var core = words.Where(w => w.Left < splitLeft).ToList();
            var margin = words.Where(w => w.Left >= splitLeft).ToList();
            return (core, margin);
        }

        private static List<List<OcrWord>> GroupByRawTopOnly(List<OcrWord> words, int verticalTolerance)
        {
            var sorted = words.OrderBy(w => w.Top).ToList();
            var lines = new List<List<OcrWord>>();

            foreach (var word in sorted)
            {
                var last = lines.Count > 0 ? lines[lines.Count - 1] : null;
                if (last != null && Math.Abs(last.Min(w => w.Top) - word.Top) <= verticalTolerance)
                {
                    last.Add(word);
                }
                else
                {
                    lines.Add(new List<OcrWord> { word });
                }
            }

            return lines;
        }

        private static List<List<OcrWord>> GroupCoreWithLocalSkew(List<OcrWord> words, int verticalTolerance)
        {
            var fragments = GroupByRawTopOnly(words, verticalTolerance);

            var merged = new List<List<OcrWord>>();
            int i = 0;
            while (i < fragments.Count)
            {
                var current = new List<OcrWord>(fragments[i]);
                int j = i + 1;
                while (j < fragments.Count && FitsLocalSlope(current, fragments[j], verticalTolerance))
                {
                    current.AddRange(fragments[j]);
                    j++;
                }
                merged.Add(current);
                i = j;
            }

            return merged;
        }

        private static bool FitsLocalSlope(List<OcrWord> a, List<OcrWord> b, int tolerance)
        {
            var combined = a.Concat(b).ToList();
            if (combined.Count < 2) return false;

            var byLeft = combined.OrderBy(w => w.Left).ToList();
            var minLeftWord = byLeft.First();
            var maxLeftWord = byLeft.Last();
            if (maxLeftWord.Left == minLeftWord.Left) return false;

            double slope = (double)(maxLeftWord.Top - minLeftWord.Top) / (maxLeftWord.Left - minLeftWord.Left);
            var adjusted = combined.Select(w => w.Top - slope * w.Left).ToList();
            double spread = adjusted.Max() - adjusted.Min();
            return spread <= tolerance;
        }

        private class WordFixture
        {
            public string Text { get; set; } = "";
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
        }

        private class ResultFixture
        {
            public List<WordFixture> Words { get; set; } = new();
        }
    }
}
