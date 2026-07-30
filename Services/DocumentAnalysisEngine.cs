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

    // BuildTable reste inchangé

        // ↓↓↓ NOUVELLE MÉTHODE AJOUTÉE ICI (H3) ↓↓↓
        public List<TableRow> BuildTable(List<TextLine> lines, int? horizontalTolerance = null)
        {
            var rows = new List<TableRow>();

            foreach (var line in lines)
            {
                var sortedWords = line.Words.OrderBy(w => w.Left).ToList();
                if (sortedWords.Count == 0) continue;

                // Tolerance adaptee a la largeur moyenne des caracteres de CETTE ligne,
                // au lieu d'une constante fixe qui ne convient pas a tous les documents
                int tolerance = horizontalTolerance ?? EstimateTolerance(sortedWords);

                var row = new TableRow();
                TableCell? currentCell = null;

                foreach (var word in sortedWords)
                {
                    if (currentCell == null)
                    {
                        currentCell = new TableCell { Text = word.Text, Left = word.Left };
                    }
                    else
                    {
                        var estimatedRight = currentCell.Left + currentCell.Text.Length * 8;
                        if (word.Left - estimatedRight < tolerance)
                        {
                            currentCell.Text += " " + word.Text;
                        }
                        else
                        {
                            row.Cells.Add(currentCell);
                            currentCell = new TableCell { Text = word.Text, Left = word.Left };
                        }
                    }
                }

                if (currentCell != null)
                    row.Cells.Add(currentCell);

                rows.Add(row);
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