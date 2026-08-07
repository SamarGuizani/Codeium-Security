using Codeium_Security.OCR;

namespace Codeium_Security.Services
{
    public class TextLine
    {
        public List<OcrWord> Words { get; set; } = new();
        public int Top => Words.Count > 0 ? Words.Min(w => w.Top) : 0;

        public string FullLineText =>
            string.Join(" ", Words.OrderBy(w => w.Left).Select(w => w.Text));
    }

    public class TableCell
    {
        public string Text { get; set; } = "";
        public int Left { get; set; }
         
        //public int Right { get; set; } // le 05 aout j'ai ajouter ceci 
    }

    public class TableRow
    {
        public List<TableCell> Cells { get; set; } = new();
    }

    // Dans DocumentAnalysisEngine.cs
    public class DocumentAnalysisEngine
    {
        // Nouvelle propriété, valeur par défaut = 10 (inchangée pour les autres banques)
        public int VerticalTolerance { get; set; } = 10;

        public List<TextLine> GroupWordsIntoLines(List<OcrWord> words)
        {
            var sortedWords = words.OrderBy(w => w.Top).ToList();
            var lines = new List<TextLine>();

            foreach (var word in sortedWords)
            {
                var lastLine = lines.Count > 0 ? lines[lines.Count - 1] : null;

                // Utiliser VerticalTolerance au lieu de 10
                if (lastLine != null && Math.Abs(lastLine.Top - word.Top) <= VerticalTolerance)
                {
                    int lastLineBottom = lastLine.Words.Count > 0 ? lastLine.Words.Max(w => w.Bottom) : lastLine.Top;
                    Console.WriteLine("[LINE MERGE]");
                    Console.WriteLine("Existing line:");
                    Console.WriteLine(lastLine.FullLineText);
                    Console.WriteLine($"LastLine Top: {lastLine.Top}");
                    Console.WriteLine($"LastLine Bottom: {lastLineBottom}");
                    Console.WriteLine();
                    Console.WriteLine("New word:");
                    Console.WriteLine(word.Text);
                    Console.WriteLine($"Word Top: {word.Top}");
                    Console.WriteLine($"Word Bottom: {word.Bottom}");
                    Console.WriteLine();
                    Console.WriteLine($"Top difference: {Math.Abs(lastLine.Top - word.Top)}");
                    Console.WriteLine($"Vertical gap: {word.Top - lastLineBottom}");
                    Console.WriteLine($"Tolerance: {VerticalTolerance}");
                    Console.WriteLine();

                    lastLine.Words.Add(word);
                }
                else
                {
                    var newLine = new TextLine();
                    newLine.Words.Add(word);
                    lines.Add(newLine);
                }
            }

            return lines.OrderBy(l => l.Top).ToList();
        }

        // Estime une tolerance verticale a partir de la hauteur mediane des mots OCR de CE document,
        // au lieu d'une valeur fixe par nom de banque. Plafonnee a 25 : valeur re-calibree (et non
        // plus VerticalTolerance=10) car le regroupement image utilise desormais une reconciliation
        // locale en 2 passes (voir GroupWordsIntoLines(words, tolerance) plus bas), validee sur
        // coordonnees reelles avec une tolerance de 15-20, pas 10 (voir Tools/TestSkewGrouping.cs).
        public int EstimateVerticalTolerance(List<OcrWord> words)
        {
            var heights = words.Select(w => w.Bottom - w.Top).Where(h => h > 0).ToList();
            if (heights.Count == 0) return VerticalTolerance;
            heights.Sort();
            int medianHeight = heights[heights.Count / 2];
            return Math.Clamp((int)(medianHeight * 0.3), 2, 25);
        }

        // Regroupe avec une tolerance explicite, en tenant compte du skew (inclinaison) de la
        // page, via une reconciliation LOCALE en 2 passes -- et non une pente globale unique,
        // qui s'est averee corrompue par le contenu hors-tableau (en-tete/bandeau/pied de page)
        // lors d'un test sur la page complete : pente absurde +0.32 au lieu de -0.009, explosion
        // en ~1 ligne par mot.
        // Passe 1 : regroupement grossier par Top brut (comme GroupWordsIntoLines(words)
        // ci-dessus), qui sur-fragmente les lignes inclinees mais ne fusionne jamais deux
        // vraies lignes distinctes (leur ecart Top reel est toujours tres superieur a la
        // tolerance).
        // Passe 2 : tente de fusionner un fragment avec le fragment SUIVANT uniquement, via un
        // ajustement lineaire LOCAL a ces deux fragments (jamais a la page entiere) : si le
        // residu maximal apres correction de pente reste <= tolerance, on fusionne. Cette
        // decision ne regarde jamais de contenu distant/sans rapport, elle ne peut donc pas
        // etre corrompue par l'en-tete/pied de page.
        // Valide sur coordonnees reelles avant application ici (voir Tools/TestSkewGrouping.cs) :
        // sous-ensemble propre (4 lignes) ET page complete (477 mots -> 78 lignes, sans
        // explosion, les 2 transactions "26957445" correctement separees).
        // N'affecte que le pipeline image (seul appelant de cette surcharge) ; le pipeline PDF
        // utilise GroupWordsIntoLines(words) ci-dessus, non modifiee.
        public List<TextLine> GroupWordsIntoLines(List<OcrWord> words, int verticalTolerance)
        {
            var (core, margin) = SplitMarginNoise(words);

            var fragments = GroupByRawTop(core, verticalTolerance);
            var merged = new List<TextLine>();
            int i = 0;
            while (i < fragments.Count)
            {
                var current = fragments[i];
                int j = i + 1;
                while (j < fragments.Count && TryFitLocalSlope(current, fragments[j], verticalTolerance, out double slope))
                {
                    Console.WriteLine("[LINE MERGE]");
                    Console.WriteLine("Existing line:");
                    Console.WriteLine(current.FullLineText);
                    Console.WriteLine("New fragment:");
                    Console.WriteLine(fragments[j].FullLineText);
                    Console.WriteLine($"Slope: {slope:F6}  Tolerance: {verticalTolerance}");
                    Console.WriteLine();

                    current.Words.AddRange(fragments[j].Words);
                    j++;
                }
                merged.Add(current);
                i = j;
            }

            // Le bruit de marge est regroupe simplement (Top brut, sans reconciliation de skew) :
            // peu importe son decoupage exact, il ne doit juste jamais contaminer le tableau reel
            // ci-dessus.
            merged.AddRange(GroupByRawTop(margin, verticalTolerance));

            return merged.OrderBy(l => l.Top).ToList();
        }

        // Separe le texte parasite de marge (ex. tampon/annotation illisible tres eloigne des
        // vraies colonnes du tableau) du contenu "coeur", via le plus grand ecart dans la
        // distribution des Left -- a condition qu'il n'isole qu'une minorite de mots (jamais une
        // vraie colonne du tableau, qui contiendrait beaucoup de mots). Sans cette exclusion, un
        // seul mot parasite peut fausser l'ajustement lineaire local (2 points font toujours une
        // droite "parfaite") et corrompre le regroupement de plusieurs vraies lignes voisines --
        // observe sur biatscanner_page-0001.jpg (texte arabe illisible en marge droite, Left
        // ~4789-4808, contre ~150-4600 pour les vraies colonnes).
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

        // Regroupement grossier par Top brut (sans correction de skew) : point de depart de la
        // passe 1, et methode de regroupement complete pour le bruit de marge.
        private static List<TextLine> GroupByRawTop(List<OcrWord> words, int verticalTolerance)
        {
            var sortedWords = words.OrderBy(w => w.Top).ToList();
            var lines = new List<TextLine>();

            foreach (var word in sortedWords)
            {
                var last = lines.Count > 0 ? lines[lines.Count - 1] : null;
                if (last != null && Math.Abs(last.Top - word.Top) <= verticalTolerance)
                {
                    last.Words.Add(word);
                }
                else
                {
                    var newLine = new TextLine();
                    newLine.Words.Add(word);
                    lines.Add(newLine);
                }
            }

            return lines;
        }

        // Ajustement lineaire local a 2 fragments SEULEMENT (jamais a la page entiere) : si le
        // residu maximal apres correction de la pente ainsi estimee reste <= tolerance, les deux
        // fragments sont consideres comme faisant partie de la meme ligne physique inclinee.
        private static bool TryFitLocalSlope(TextLine a, TextLine b, int tolerance, out double slope)
        {
            var combined = a.Words.Concat(b.Words).ToList();
            slope = 0;
            if (combined.Count < 2) return false;

            var byLeft = combined.OrderBy(w => w.Left).ToList();
            var minLeftWord = byLeft.First();
            var maxLeftWord = byLeft.Last();
            if (maxLeftWord.Left == minLeftWord.Left) return false;

            double localSlope = (double)(maxLeftWord.Top - minLeftWord.Top) / (maxLeftWord.Left - minLeftWord.Left);
            slope = localSlope;
            var adjusted = combined.Select(w => w.Top - localSlope * w.Left).ToList();
            double spread = adjusted.Max() - adjusted.Min();
            return spread <= tolerance;
        }

    // BuildTable reste inchangé

        // ↓↓↓ NOUVELLE MÉTHODE AJOUTÉE ICI (H3) ↓↓↓
        public List<TableRow> BuildTable(List<TextLine> lines, int? horizontalTolerance = null)
        {
            var rows = new List<TableRow>();
            Console.WriteLine($"[DIAG-BuildTable] Input TextLines: {lines.Count}");

            foreach (var line in lines)
            {
                var sortedWords = line.Words.OrderBy(w => w.Left).ToList();
                if (sortedWords.Count == 0) continue;

                // Tolerance adaptee a la largeur moyenne des caracteres de CETTE ligne,
                // au lieu d'une constante fixe qui ne convient pas a tous les documents
                int tolerance = horizontalTolerance ?? EstimateTolerance(sortedWords);
                Console.WriteLine($"[DIAG-BuildTable] Line Top={line.Top} tolerance={tolerance}");

                var row = new TableRow();
                TableCell? currentCell = null;

                foreach (var word in sortedWords)
                {
                    if (currentCell == null)
                    {
                        currentCell = new TableCell { Text = word.Text, Left = word.Left };//Right = word.Right 
                    }
                    else
                    {
                        var estimatedRight = currentCell.Left + currentCell.Text.Length * 8;
                        if (word.Left - estimatedRight < tolerance)
                        //if( word.Left - currentCell.Right < tolerance)
                        {
                            currentCell.Text += " " + word.Text;
                            //currentCell.Right = word.Right; //ajout de cette ligne le 5 aout 
                        }
                        else
                        {
                            row.Cells.Add(currentCell);
                            currentCell = new TableCell { Text = word.Text, Left = word.Left }; //Right = word.Right 
                        }
                    }
                }

                if (currentCell != null)
                    row.Cells.Add(currentCell);

                rows.Add(row);
            }
         

            for (int i = 0; i < rows.Count; i++)
            {
                string cellsDump = string.Join(" || ", rows[i].Cells.Select(c => $"Left={c.Left}:'{c.Text}'"));
                Console.WriteLine($"[DIAG-BuildTable] Row {i}: {cellsDump}");
            }

            return rows;
        }

        // Estime un espacement de tolerance a partir de la largeur moyenne des mots de la ligne,
        // pour s'adapter automatiquement a la resolution/mise en page de chaque document
        private int EstimateTolerance(List<OcrWord> words)
        {
            var widths = words.Select(w => (w.Right - w.Left) / Math.Max(1, w.Text.Length)).Where(w => w > 0).ToList();
            if (widths.Count == 0) return 30;
            double avgCharWidth = widths.Average();
            return (int)Math.Clamp(avgCharWidth * 2.5, 15, 60);
        }
        // ↑↑↑ FIN DE LA NOUVELLE MÉTHODE ↑↑↑
    }
}