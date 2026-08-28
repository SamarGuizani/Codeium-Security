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
        public void CustomerName_Label_Client_SameLine_ShortValueNotRejectedAsAcronym()
        {
            // Reproduit un releve BTL reel ("Client: EZB") : une valeur courte tout-majuscule
            // ne doit PAS etre rejetee comme un sigle de banque quand elle vient d'un label
            // explicite et non ambigu - contrairement au ramassage sans label (voir
            // CustomerName_NoLabel_* ci-dessous, ou un sigle isole est bien rejete).
            var rows = RowsFromLines(
                "Numéro de Compte : 8901769223",
                "Client: EZB",
                "Agence : Agence Ben Arous",
                "Devise : TND",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("EZB", result.CustomerName);
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
        public void CustomerNameAndPeriod_NameThenPeriod_OrderDoesNotMatter()
        {
            var rows = RowsFromLines(
                "Nom du client : BLUE TUNISIE",
                "Période du 01/11/2024 au 30/11/2024",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/11/2024", result.Period!.Start);
            Assert.Equal("30/11/2024", result.Period!.End);
        }

        [Fact]
        public void CustomerNameAndPeriod_PeriodThenName_OrderDoesNotMatter()
        {
            var rows = RowsFromLines(
                "Période du 01/01/2024 au 31/05/2024",
                "Nom du client : BLUE TUNISIE",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/01/2024", result.Period!.Start);
            Assert.Equal("31/05/2024", result.Period!.End);
        }

        [Fact]
        public void CustomerName_UnlabeledWithArabicTitleAndLoneDate_StopsAtFirstLine()
        {
            var rows = RowsFromLines(
                "STE. CATERING CONCEPT",
                "14 IMEN ABOU HANIFA",
                "B 5 - LA MARSA",
                "Au :",
                "RELEVE DE COMPTE حساب كشف",
                "31/05/2025",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("STE. CATERING CONCEPT", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Uib_LabelValueFarApart_SkipsOtherLabelsAndAddress()
        {
            // Reproduit un releve UIB reel dont l'OCR melange fortement les colonnes : le
            // label "TITULAIRE" est separe de sa valeur reelle ("COM'UP AGENCY") par
            // plusieurs AUTRES labels (Agence, Code client, Gestionnaire, references
            // bancaires, RIB, IBAN, type de compte...), et la periode est ecrite "<date> Au"
            // puis la seconde date seule sur la ligne suivante.
            var rows = RowsFromLines(
                "UIB",
                "GROUPE SOCIETE GENERALE",
                "TITULAIRE",
                "AGENCE U.I.B CENTRALE",
                "CODE CLIENT",
                "Gestionnaire OUMAIMA OUERTANI",
                "REFERENCES BANCAIRES",
                "N°Compte",
                "00 00033029238",
                "12 000 00 00033029238 16",
                "TN59 12 000 00 00033029238 16",
                "CPTE COURANT COMMERCIAL PRIVE",
                "RELEVE D'IDENTITE BANCAIRE",
                "COM'UP AGENCY",
                "00033029238-16",
                "01/12/2025 Au",
                "19/01/2026",
                "N°09 RUE HAMADI BEN AMMAR",
                "LA GOULETTE",
                "2060",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("COM'UP AGENCY", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/12/2025", result.Period!.Start);
            Assert.Equal("19/01/2026", result.Period!.End);
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
