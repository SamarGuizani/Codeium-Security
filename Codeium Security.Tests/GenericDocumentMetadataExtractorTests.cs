using Codeium_Security.Services;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Xunit;

namespace Codeium_Security.Tests
{
    // Tests purs (aucun OCR) pour GenericDocumentMetadataExtractor : construit des TableRow
    // a la main pour simuler la zone d'en-tete d'un releve, et verifie CustomerName /
    // ExtractionPeriod. N'exerce jamais BankDocumentParser.
    public class GenericDocumentMetadataExtractorTests
    {
        private readonly GenericDocumentMetadataExtractor _extractor = new();

        private static TableRow Row(string text)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell { Text = text, Left = 0 });
            return row;
        }

        private static List<TableRow> RowsFromLines(params string[] lines) =>
            lines.Select(Row).ToList();

        [Fact]
        public void CustomerName_Label_TitulaireDuCompte_SameLine()
        {
            var rows = RowsFromLines(
                "N° du compte : 14034034101700148178",
                "Titulaire du compte : AL BARAKA RENT A CAR",
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract(string.Join("\n", rows.SelectMany(r => r.Cells.Select(c => c.Text))), rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
        }

        [Fact]
        public void CustomerName_Label_NomDuClient_SameLine()
        {
            var rows = RowsFromLines(
                "Nom du client : BLUE TUNISIE",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
        }

        [Fact]
        public void CustomerName_Label_To_ValueOnNextLine()
        {
            var rows = RowsFromLines(
                "To",
                "CH CLEAN",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("CH CLEAN", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_StopsBeforeStreetAddress()
        {
            var rows = RowsFromLines(
                "SOCIETE AUTOSET 7 PIECES AUTO",
                "AVENUE FRANCE N 70",
                "2013 BEN AROUS",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("SOCIETE AUTOSET 7 PIECES AUTO", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_StopsAtFirstLine_DoesNotConcatenate()
        {
            var rows = RowsFromLines(
                "STE GROUPE CHAKROUN DE COMMERCE",
                "TUNISIE",
                "LA MARSA",
                "N8 RUE HAFSIDES",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("STE GROUPE CHAKROUN DE COMMERCE", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_SkipsNumberedStreetLine()
        {
            var rows = RowsFromLines(
                "COM'UP AGENCY",
                "N°09 RUE HAMADI BEN AMMAR",
                "LA GOULETTE 2060",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("COM'UP AGENCY", result.CustomerName);
        }

        [Fact]
        public void ExtractionPeriod_OperationsDuAu_SameLine()
        {
            var rows = RowsFromLines(
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_DuAu_WithoutOperationsPrefix()
        {
            var rows = RowsFromLines(
                "Du 01-02-2026 au 28-02-2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01-02-2026", result.Period!.Start);
            Assert.Equal("28-02-2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_PeriodeLabel_DashSeparator()
        {
            var rows = RowsFromLines(
                "Période : 01.02.2026 - 28.02.2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01.02.2026", result.Period!.Start);
            Assert.Equal("28.02.2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_DatesOnSeparateLines()
        {
            var rows = RowsFromLines(
                "Du 01/02/2026",
                "Au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_NeverReadsTransactionDates()
        {
            var rows = RowsFromLines(
                "Titulaire du compte : AL BARAKA RENT A CAR",
                "Date Libellé opération Débit Crédit Solde",
                "02/02/2026 VRST. 4325674 03/02/2026 18120",
                "03/02/2026 PRLV. AIL 30/01/2026 3664891");

            var result = _extractor.Extract("", rows);

            Assert.Null(result.Period);
        }

        [Fact]
        public void RealWorldSample_ExtraitBancaireBT2_CustomerNameAndPeriod()
        {
            // Reproduit fidelement l'en-tete reel de EXTRAIT BANCAIRE_BT2.pdf.
            var rows = RowsFromLines(
                "Extrait de Compte Bancaire",
                "No du compte - 14034034101700148178",
                "du compte : AL BARAKA RENT A CAR",
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date opération Libellé opération Date valeur Débit");

            var result = _extractor.Extract("", rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }
    }
}
