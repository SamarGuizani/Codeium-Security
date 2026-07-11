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

    public class DocumentAnalysisEngine
    {
        public List<TextLine> GroupWordsIntoLines(List<OcrWord> words, int verticalTolerance = 10)
        {
            var lines = new List<TextLine>();
            var sortedWords = words.OrderBy(w => w.Top).ToList();

            foreach (var word in sortedWords)
            {
                var matchingLine = lines.FirstOrDefault(line =>
                    Math.Abs(line.Top - word.Top) <= verticalTolerance);

                if (matchingLine != null)
                    matchingLine.Words.Add(word);
                else
                {
                    var newLine = new TextLine();
                    newLine.Words.Add(word);
                    lines.Add(newLine);
                }
            }

            return lines.OrderBy(l => l.Top).ToList();
        }

        // ↓↓↓ NOUVELLE MÉTHODE AJOUTÉE ICI (H3) ↓↓↓
        public List<TableRow> BuildTable(List<TextLine> lines, int horizontalTolerance = 30)
        {
            var rows = new List<TableRow>();

            foreach (var line in lines)
            {
                var sortedWords = line.Words.OrderBy(w => w.Left).ToList();
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
                        if (word.Left - estimatedRight < horizontalTolerance)
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
        // ↑↑↑ FIN DE LA NOUVELLE MÉTHODE ↑↑↑
    }
}