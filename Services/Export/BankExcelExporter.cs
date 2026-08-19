using ClosedXML.Excel;
using Codeium_Security.Models;

namespace Codeium_Security.Services.Export
{
    // Prend un BankDocument deja parse (voir DocumentProcessingService/BankDocumentParser) et
    // produit un classeur .xlsx : une feuille par BankAccountSection, avec un bloc d'en-tete
    // (Banque/Compte/Devise/RIB/Soldes) suivi du tableau Date/Libelle/Debit/Credit. Ne touche
    // jamais au parsing : ne lit que les proprietes deja presentes sur BankDocument.
    public class BankExcelExporter
    {
        private static readonly char[] InvalidSheetChars = { '\\', '/', '?', '*', '[', ']', ':' };

        public byte[] Export(BankDocument document)
        {
            using var workbook = new XLWorkbook();
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (document.Accounts.Count == 0)
            {
                AddAccountSheet(workbook, document.BankName, new BankAccountSection(), usedSheetNames);
            }
            else
            {
                foreach (var account in document.Accounts)
                {
                    try
                    {
                        AddAccountSheet(workbook, document.BankName, account, usedSheetNames);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[BankExcelExporter] ERREUR export du compte '{account.AccountNumber}': {ex.Message}");
                    }
                }
            }

            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return stream.ToArray();
        }

        private void AddAccountSheet(XLWorkbook workbook, string bankName, BankAccountSection account, HashSet<string> usedSheetNames)
        {
            var sheet = workbook.Worksheets.Add(BuildSheetName(account.AccountNumber, usedSheetNames));

            int row = 1;
            sheet.Cell(row, 1).Value = "Banque :";
            sheet.Cell(row, 2).Value = bankName;
            row++;

            sheet.Cell(row, 1).Value = "Compte :";
            sheet.Cell(row, 2).Value = account.AccountNumber;
            row++;

            sheet.Cell(row, 1).Value = "Devise :";
            sheet.Cell(row, 2).Value = account.Currency;
            row++;

            sheet.Cell(row, 1).Value = "RIB :";
            sheet.Cell(row, 2).Value = account.Rib;
            row++;

            row = WriteOptionalAmount(sheet, row, "Solde initial :", account.SoldeInitial);
            row = WriteOptionalAmount(sheet, row, "Solde final :", account.SoldeFinal);
            row = WriteOptionalAmount(sheet, row, "Solde disponible :", account.SoldeDisponible);

            row++; // ligne vide de separation

            int headerRow = row;
            sheet.Cell(headerRow, 1).Value = "Date";
            sheet.Cell(headerRow, 2).Value = "Libellé";
            sheet.Cell(headerRow, 3).Value = "Débit";
            sheet.Cell(headerRow, 4).Value = "Crédit";
            sheet.Range(headerRow, 1, headerRow, 4).Style.Font.Bold = true;
            row++;

            string amountFormat = string.Equals(account.Currency?.Trim(), "TND", StringComparison.OrdinalIgnoreCase)
                ? "#,##0.000"
                : "#,##0.00";

            foreach (var tx in account.Transactions)
            {
                sheet.Cell(row, 1).Value = tx.Date;
                sheet.Cell(row, 2).Value = tx.Libelle;

                if (tx.Debit.HasValue)
                {
                    sheet.Cell(row, 3).Value = tx.Debit.Value;
                    sheet.Cell(row, 3).Style.NumberFormat.Format = amountFormat;
                }
                if (tx.Credit.HasValue)
                {
                    sheet.Cell(row, 4).Value = tx.Credit.Value;
                    sheet.Cell(row, 4).Style.NumberFormat.Format = amountFormat;
                }

                row++;
            }

            sheet.Column(1).Width = 14;
            sheet.Column(2).Width = 60;
            sheet.Column(3).Width = 16;
            sheet.Column(4).Width = 16;
        }

        private static int WriteOptionalAmount(IXLWorksheet sheet, int row, string label, decimal? value)
        {
            sheet.Cell(row, 1).Value = label;
            if (value.HasValue)
                sheet.Cell(row, 2).Value = value.Value;
            return row + 1;
        }

        private static string BuildSheetName(string accountNumber, HashSet<string> usedSheetNames)
        {
            string baseName = string.IsNullOrWhiteSpace(accountNumber) ? "Compte" : accountNumber;
            foreach (var c in InvalidSheetChars)
                baseName = baseName.Replace(c, '_');
            baseName = baseName.Trim().Trim('\'');
            if (baseName.Length == 0)
                baseName = "Compte";
            if (baseName.Length > 31)
                baseName = baseName[..31];

            string candidate = baseName;
            int suffix = 2;
            while (!usedSheetNames.Add(candidate))
            {
                string suffixText = $"_{suffix}";
                int maxBaseLength = 31 - suffixText.Length;
                candidate = (baseName.Length > maxBaseLength ? baseName[..maxBaseLength] : baseName) + suffixText;
                suffix++;
            }

            return candidate;
        }
    }
}
