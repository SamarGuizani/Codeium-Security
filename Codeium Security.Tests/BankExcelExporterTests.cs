using ClosedXML.Excel;
using Codeium_Security.Models;
using Codeium_Security.Services.Export;
using Xunit;

namespace Codeium_Security.Tests
{
    // Verifie BankExcelExporter isolement (pas de pipeline OCR) : structure du classeur,
    // gestion multi-comptes, noms de feuille invalides/dupliques, cellules vides pour les
    // montants null.
    public class BankExcelExporterTests
    {
        [Fact]
        public void Export_Produces_One_Sheet_Per_Account_With_Correct_Data()
        {
            var document = new BankDocument
            {
                BankName = "Banque Test",
                Accounts = new List<BankAccountSection>
                {
                    new BankAccountSection
                    {
                        AccountNumber = "001/234:5",
                        Currency = "TND",
                        Rib = "TN5900000000000000000000",
                        SoldeInitial = 1000.500m,
                        SoldeFinal = 900.250m,
                        SoldeDisponible = 900.250m,
                        Transactions = new List<Transaction>
                        {
                            new Transaction { Date = "01/01/2025", Libelle = "Virement recu", Debit = null, Credit = 500.000m },
                            new Transaction { Date = "02/01/2025", Libelle = "Retrait especes", Debit = 100.250m, Credit = null },
                        }
                    },
                    // Meme numero de compte que le precedent une fois assaini -> doit produire
                    // un 2e nom de feuille distinct (suffixe _2) plutot qu'ecraser le premier.
                    new BankAccountSection
                    {
                        AccountNumber = "001/234:5",
                        Currency = "USD",
                        Transactions = new List<Transaction>()
                    },
                }
            };

            var exporter = new BankExcelExporter();
            byte[] bytes = exporter.Export(document);

            Assert.NotEmpty(bytes);

            using var stream = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(stream);

            Assert.Equal(2, workbook.Worksheets.Count);

            var sheetNames = workbook.Worksheets.Select(s => s.Name).ToList();
            Assert.All(sheetNames, n => Assert.True(n.Length <= 31));
            Assert.All(sheetNames, n => Assert.DoesNotContain(n, c => "\\/?*[]:".Contains(c)));
            Assert.Equal(2, sheetNames.Distinct().Count());

            var sheet1 = workbook.Worksheet(1);
            Assert.Equal("Banque :", sheet1.Cell(1, 1).GetString());
            Assert.Equal("Banque Test", sheet1.Cell(1, 2).GetString());
            Assert.Equal("Compte :", sheet1.Cell(2, 1).GetString());
            Assert.Equal("Solde initial :", sheet1.Cell(5, 1).GetString());
            Assert.Equal(1000.500, sheet1.Cell(5, 2).GetDouble(), 3);

            int headerRow = 9;
            Assert.Equal("Date", sheet1.Cell(headerRow, 1).GetString());
            Assert.Equal("Libellé", sheet1.Cell(headerRow, 2).GetString());
            Assert.Equal("Débit", sheet1.Cell(headerRow, 3).GetString());
            Assert.Equal("Crédit", sheet1.Cell(headerRow, 4).GetString());

            int firstTxRow = headerRow + 1;
            Assert.Equal("01/01/2025", sheet1.Cell(firstTxRow, 1).GetString());
            Assert.Equal("Virement recu", sheet1.Cell(firstTxRow, 2).GetString());
            Assert.True(sheet1.Cell(firstTxRow, 3).IsEmpty());
            Assert.Equal(500.000, sheet1.Cell(firstTxRow, 4).GetDouble(), 3);

            int secondTxRow = firstTxRow + 1;
            Assert.Equal(100.250, sheet1.Cell(secondTxRow, 3).GetDouble(), 3);
            Assert.True(sheet1.Cell(secondTxRow, 4).IsEmpty());
        }

        [Fact]
        public void Export_Handles_Document_With_No_Accounts_Without_Throwing()
        {
            var document = new BankDocument { BankName = "Banque Vide", Accounts = new List<BankAccountSection>() };

            var exporter = new BankExcelExporter();
            byte[] bytes = exporter.Export(document);

            using var stream = new MemoryStream(bytes);
            using var workbook = new XLWorkbook(stream);
            Assert.Single(workbook.Worksheets);
        }

        // Test d'integration : fait tourner le VRAI pipeline (OCR reel) sur un releve BIAT deja
        // fonctionnel, puis exporte le BankDocument reellement produit - verifie que l'exporteur
        // fonctionne sur des donnees reelles, pas seulement sur des fixtures synthetiques.
        [Collection("Pipeline")]
        public class BankExcelExporterRealDocumentTests
        {
            private readonly PipelineFixture _fixture;

            public BankExcelExporterRealDocumentTests(PipelineFixture fixture)
            {
                _fixture = fixture;
            }

            [Fact]
            public async Task Export_Real_Parsed_BankDocument_Produces_Valid_Xlsx()
            {
                string pdfPath = Path.Combine(RepoPaths.SourceDocumentsDir, "AMEN.pdf");
                Assert.True(File.Exists(pdfPath), $"PDF introuvable : {pdfPath}");

                var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, "AMEN.pdf");
                Assert.True(result.Document is BankDocument, "Le document n'a pas ete reconnu comme BankDocument.");
                var bankDoc = (BankDocument)result.Document!;
                Assert.NotEmpty(bankDoc.Accounts);

                var exporter = new BankExcelExporter();
                byte[] bytes = exporter.Export(bankDoc);

                using var stream = new MemoryStream(bytes);
                using var workbook = new XLWorkbook(stream);

                Assert.Equal(bankDoc.Accounts.Count, workbook.Worksheets.Count);

                var sheet = workbook.Worksheet(1);
                var account = bankDoc.Accounts[0];
                int headerRow = 9;
                Assert.Equal("Date", sheet.Cell(headerRow, 1).GetString());

                int expectedLastRow = headerRow + account.Transactions.Count;
                Assert.Equal(account.Transactions.Count, expectedLastRow - headerRow);
                if (account.Transactions.Count > 0)
                    Assert.False(sheet.Cell(headerRow + 1, 1).IsEmpty());
            }
        }
    }
}
