using ClosedXML.Excel;
using Codeium_Security.Models;
using Codeium_Security.Services.Export;
using Xunit;
using Xunit.Abstractions;

namespace Codeium_Security.Tests
{
    // DIAGNOSTIC PUR (2026-09-17) : ne modifie rien, ne fait aucune assertion de non-regression.
    // Fait tourner le VRAI pipeline (PDF -> OCR Tesseract -> DocumentProcessingService ->
    // BankExcelExporter) sur un vrai fichier Zitouna, exactement comme OcrController le fait en
    // production, et affiche a chaque etape les valeurs demandees par l'utilisateur pour localiser
    // la cause reelle du probleme observe dans l'Excel exporte. A supprimer une fois le diagnostic
    // termine.
    [Collection("Pipeline")]
    public class ZitounaRealPipelineDiagnosticTest
    {
        private readonly PipelineFixture _fixture;
        private readonly ITestOutputHelper _output;

        public ZitounaRealPipelineDiagnosticTest(PipelineFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        private const string CodeuimZitounaDir = @"C:\Users\USER\OneDrive\Desktop\codeuim\zitouna";
        private const string ImagesDir = @"C:\Users\USER\OneDrive\Desktop\Codeium Security\Codeium Security\Images";

        public static IEnumerable<object[]> RealZitounaFiles()
        {
            (string dir, string file)[] files =
            {
                (CodeuimZitounaDir, "LISTE_DES_TRANSACTIONS_SOCIETE TOPDIS_08_02_2025.pdf"),
                (CodeuimZitounaDir, "LISTE_DES_TRANSACTIONS_STE ESPACE ZORRAGA AUTO _01_02_2024.pdf"),
                (CodeuimZitounaDir, "Relevé Zitouna 06.pdf"),
                (CodeuimZitounaDir, "extraitzitouna.pdf"),
                (ImagesDir, "ZITOUNA.pdf"),
                (ImagesDir, "extraitzitouna2.pdf"),
            };
            foreach (var (dir, f) in files)
                yield return new object[] { Path.Combine(dir, f) };
        }

        [Theory]
        [MemberData(nameof(RealZitounaFiles))]
        public async Task Diagnostic_Complet_Pdf_Vers_Excel(string pdfPath)
        {
            string fileName = Path.GetFileName(pdfPath);
            if (!File.Exists(pdfPath))
            {
                _output.WriteLine($"[SKIP] Introuvable : {pdfPath}");
                return;
            }

            _output.WriteLine($"===== {fileName} =====");

            // ----- ETAPE 1 : OCR brut (ocrResult.FullText) -----
            var ocrResult = await _fixture.ProcessingService.RunOcrOnlyAsync(pdfPath, fileName);
            bool fullTextContainsZitouna = ocrResult.FullText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase);
            _output.WriteLine($"[1] ocrResult.FullText.Length={ocrResult.FullText.Length} | Contains(\"ZITOUNA\")={fullTextContainsZitouna}");
            _output.WriteLine("[1] ----- Extrait FullText (800 premiers caracteres) -----");
            _output.WriteLine(ocrResult.FullText.Length > 800 ? ocrResult.FullText[..800] : ocrResult.FullText);
            _output.WriteLine("[1] ----- FIN extrait -----");

            // ----- ETAPE 2 : pipeline complet (DocumentProcessingService) -----
            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);
            var metadata = result.Metadata;
            _output.WriteLine($"[2] metadata is null: {metadata == null}");
            _output.WriteLine($"[2] metadata.BankName = '{metadata?.BankName}'");
            _output.WriteLine($"[2] metadata.CustomerName = '{metadata?.CustomerName}'");

            if (result.Document is not BankDocument bankDoc)
            {
                _output.WriteLine($"[SKIP] Document non reconnu comme BankDocument (type detecte: {result.DetectedType}).");
                return;
            }
            _output.WriteLine($"[2] bankDoc.BankName (document, distinct de metadata.BankName) = '{bankDoc.BankName}'");
            _output.WriteLine($"[2] Accounts.Count = {bankDoc.Accounts.Count}");

            // ----- ETAPE 3/4 : ce que BankExcelExporter voit reellement (memes variables que le
            // code de AddAccountSheet, sans dupliquer sa logique) -----
            foreach (var account in bankDoc.Accounts)
            {
                string bankNameVuParExporter = bankDoc.BankName; // parametre "bankName" de AddAccountSheet
                string? metadataBankNameVuParExporter = metadata?.BankName;
                string valeurEffectivementPasseeAIsZitounaDocument = metadataBankNameVuParExporter ?? bankNameVuParExporter;

                _output.WriteLine($"[3] Compte '{account.AccountNumber}' | bankName(document)='{bankNameVuParExporter}' | metadata?.BankName='{metadataBankNameVuParExporter}' | valeur passee a IsZitounaDocument='{valeurEffectivementPasseeAIsZitounaDocument}'");
                _output.WriteLine($"[3] account.RawSectionText.Length={account.RawSectionText.Length} | RawSectionText.Contains(\"ZITOUNA\")={account.RawSectionText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase)}");

                bool isZitouna = ZitounaLedgerAccountClassifier.IsZitounaDocument(account, valeurEffectivementPasseeAIsZitounaDocument);
                _output.WriteLine($"[4] IsZitounaDocument(...) = {isZitouna}");

                // ----- Dump BRUT de toutes les transactions (non filtre) pour verifier si les
                // libelles exacts rapportes ("Prélèv com/ EPS...", "TVA/COMM", "Intérêts créd/déb")
                // apparaissent reellement dans ce fichier. -----
                _output.WriteLine($"[DUMP] {account.Transactions.Count} transactions :");
                foreach (var txDump in account.Transactions)
                    _output.WriteLine($"[DUMP]   Libelle='{txDump.Libelle}' | Debit={txDump.Debit} | Credit={txDump.Credit}");

                // ----- ETAPE 5/6 : transactions reelles ciblees par l'utilisateur -----
                foreach (var tx in account.Transactions)
                {
                    bool estPrelevCom = tx.Libelle.Contains("prélèv", StringComparison.OrdinalIgnoreCase)
                        || tx.Libelle.Contains("prelev", StringComparison.OrdinalIgnoreCase);
                    bool estTvaComm = tx.Libelle.Contains("tva", StringComparison.OrdinalIgnoreCase)
                        && (tx.Libelle.Contains("comm", StringComparison.OrdinalIgnoreCase) || tx.Libelle.Contains("com", StringComparison.OrdinalIgnoreCase));
                    bool estInteret = tx.Libelle.Contains("intér", StringComparison.OrdinalIgnoreCase)
                        || tx.Libelle.Contains("interet", StringComparison.OrdinalIgnoreCase);

                    if (!estPrelevCom && !estTvaComm && !estInteret)
                        continue;

                    var lignes = isZitouna
                        ? ZitounaLedgerAccountClassifier.Classify(tx, metadata?.CustomerName)
                        : Array.Empty<(string CompteDebit, string CompteCredit)>();

                    string lignesTxt = lignes.Count == 0
                        ? "(aucune)"
                        : string.Join(" | ", lignes.Select(l => $"{l.CompteDebit}/{l.CompteCredit}"));

                    _output.WriteLine($"[5/6] Libelle='{tx.Libelle}' | Debit={tx.Debit} | Credit={tx.Credit} | isZitouna={isZitouna} | Classify={lignesTxt}");
                }
            }

            // ----- ETAPE 7/8 : verification de ce qui est REELLEMENT ecrit dans le xlsx -----
            var exporter = new BankExcelExporter();
            byte[] xlsxBytes = exporter.Export(bankDoc, metadata);
            using var stream = new MemoryStream(xlsxBytes);
            using var workbook = new XLWorkbook(stream);

            foreach (var sheet in workbook.Worksheets)
            {
                int headerRow = -1;
                for (int r = 1; r <= 80; r++)
                {
                    if (sheet.Cell(r, 1).GetString() == "Date") { headerRow = r; break; }
                }
                if (headerRow < 0) continue;

                int row = headerRow + 1;
                while (!sheet.Cell(row, 2).IsEmpty())
                {
                    string libelleExcel = sheet.Cell(row, 2).GetString();
                    bool cible = libelleExcel.Contains("prélèv", StringComparison.OrdinalIgnoreCase)
                        || libelleExcel.Contains("prelev", StringComparison.OrdinalIgnoreCase)
                        || (libelleExcel.Contains("tva", StringComparison.OrdinalIgnoreCase) && libelleExcel.Contains("com", StringComparison.OrdinalIgnoreCase))
                        || libelleExcel.Contains("intér", StringComparison.OrdinalIgnoreCase)
                        || libelleExcel.Contains("interet", StringComparison.OrdinalIgnoreCase);

                    if (cible)
                    {
                        string cd = sheet.Cell(row, 5).GetString();
                        string cc = sheet.Cell(row, 6).GetString();
                        _output.WriteLine($"[7/8] Feuille='{sheet.Name}' Ligne={row} | Libelle(Excel)='{libelleExcel}' | Debit(Excel)='{sheet.Cell(row, 3).GetString()}' | Credit(Excel)='{sheet.Cell(row, 4).GetString()}' | CompteDebit(Excel)='{cd}' | CompteCredit(Excel)='{cc}'");
                    }
                    row++;
                }
            }

            Assert.True(true);
        }

        // DIAGNOSTIC PONCTUEL (2026-09-17) : fichier reel fourni par l'utilisateur
        // (C:\Users\USER\Downloads\releve.pdf), copie dans le scratchpad pour investigation.
        // Reproduit exactement PDF -> OCR -> DocumentProcessingService -> BankExcelExporter et
        // dump TOUTES les transactions (pas de filtre) pour localiser "Prélèv com/ EPS...",
        // "Rejet prélév...", "Com / décision...", "Certif cheg...".
        [Fact]
        public async Task Diagnostic_Fichier_Utilisateur_Releve()
        {
            string pdfPath = @"C:\Users\USER\AppData\Local\Temp\claude\releve_copy.pdf";
            if (!File.Exists(pdfPath))
            {
                _output.WriteLine($"[SKIP] Introuvable : {pdfPath}");
                return;
            }

            var ocrResult = await _fixture.ProcessingService.RunOcrOnlyAsync(pdfPath, "releve.pdf");
            _output.WriteLine($"[1] ocrResult.FullText.Length={ocrResult.FullText.Length} | Contains(\"ZITOUNA\")={ocrResult.FullText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase)}");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, "releve.pdf");
            var metadata = result.Metadata;
            _output.WriteLine($"[2] metadata.BankName = '{metadata?.BankName}' | CustomerName = '{metadata?.CustomerName}'");

            if (result.Document is not BankDocument bankDoc)
            {
                _output.WriteLine($"[SKIP] Document non reconnu comme BankDocument (type detecte: {result.DetectedType}).");
                return;
            }

            var exporter = new BankExcelExporter();
            byte[] xlsxBytes = exporter.Export(bankDoc, metadata);
            using var stream = new MemoryStream(xlsxBytes);
            using var workbook = new XLWorkbook(stream);

            foreach (var account in bankDoc.Accounts)
            {
                bool isZitouna = ZitounaLedgerAccountClassifier.IsZitounaDocument(account, metadata?.BankName ?? bankDoc.BankName);
                _output.WriteLine($"[4] Compte '{account.AccountNumber}' | IsZitounaDocument={isZitouna} | {account.Transactions.Count} transactions");

                foreach (var tx in account.Transactions)
                {
                    var lignes = isZitouna
                        ? ZitounaLedgerAccountClassifier.Classify(tx, metadata?.CustomerName)
                        : Array.Empty<(string CompteDebit, string CompteCredit)>();
                    string lignesTxt = lignes.Count == 0 ? "(aucune)" : string.Join(" | ", lignes.Select(l => $"{l.CompteDebit}/{l.CompteCredit}"));
                    _output.WriteLine($"[DUMP] Libelle='{tx.Libelle}' | Debit={tx.Debit} | Credit={tx.Credit} | Classify={lignesTxt}");
                }
            }

            _output.WriteLine("[XLSX] ----- Cellules Compte Debit/Compte Credit reellement ecrites -----");
            foreach (var sheet in workbook.Worksheets)
            {
                int headerRow = -1;
                for (int r = 1; r <= 80; r++)
                {
                    if (sheet.Cell(r, 1).GetString() == "Date") { headerRow = r; break; }
                }
                if (headerRow < 0) continue;

                int row = headerRow + 1;
                while (!sheet.Cell(row, 2).IsEmpty() || !sheet.Cell(row, 1).IsEmpty())
                {
                    _output.WriteLine($"[XLSX] L{row} | '{sheet.Cell(row, 2).GetString()}' | D='{sheet.Cell(row, 3).GetString()}' C='{sheet.Cell(row, 4).GetString()}' | CD='{sheet.Cell(row, 5).GetString()}' CC='{sheet.Cell(row, 6).GetString()}'");
                    row++;
                    if (row > headerRow + 500) break;
                }
            }

            Assert.True(true);
        }

        // DIAGNOSTIC PONCTUEL (2026-09-17) : lit un xlsx local quelconque (ex. une version corrigee
        // a la main par l'utilisateur, avec des cellules en rouge) et dump TOUTES les lignes avec la
        // couleur de police des colonnes Compte Debit/Compte Credit, pour identifier les corrections.
        [Theory]
        [InlineData(@"C:\Users\USER\AppData\Local\Temp\claude\bna_gtt.xlsx")]
        [InlineData(@"C:\Users\USER\AppData\Local\Temp\claude\qnb.xlsx")]
        [InlineData(@"C:\Users\USER\AppData\Local\Temp\claude\releve4.xlsx")]
        [InlineData(@"C:\Users\USER\AppData\Local\Temp\claude\bqkk.xlsx")]
        public void Diagnostic_Lecture_Xlsx_Local_Avec_Couleurs(string xlsxPath)
        {
            if (!File.Exists(xlsxPath))
            {
                _output.WriteLine($"[SKIP] Introuvable : {xlsxPath}");
                return;
            }

            using var workbook = new XLWorkbook(xlsxPath);
            foreach (var sheet in workbook.Worksheets)
            {
                _output.WriteLine($"===== Feuille '{sheet.Name}' =====");
                int headerRow = -1;
                for (int r = 1; r <= 80; r++)
                {
                    if (sheet.Cell(r, 1).GetString() == "Date") { headerRow = r; break; }
                }
                if (headerRow < 0)
                {
                    _output.WriteLine("[SKIP] Pas d'en-tete 'Date' trouve sur cette feuille.");
                    continue;
                }

                int row = headerRow + 1;
                while (!sheet.Cell(row, 2).IsEmpty() || !sheet.Cell(row, 1).IsEmpty())
                {
                    var cdCell = sheet.Cell(row, 5);
                    var ccCell = sheet.Cell(row, 6);
                    string cdColor = cdCell.Style.Font.FontColor.ToString();
                    string ccColor = ccCell.Style.Font.FontColor.ToString();
                    _output.WriteLine($"[XLSX] L{row} | '{sheet.Cell(row, 2).GetString()}' | D='{sheet.Cell(row, 3).GetString()}' C='{sheet.Cell(row, 4).GetString()}' | CD='{cdCell.GetString()}'(couleur={cdColor}) CC='{ccCell.GetString()}'(couleur={ccColor})");
                    row++;
                    if (row > headerRow + 500) break;
                }
            }
        }
    }
}
