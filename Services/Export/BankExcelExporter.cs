using System.Globalization;
using ClosedXML.Excel;
using Codeium_Security.Models;
using Codeium_Security.Services.Calculation;

namespace Codeium_Security.Services.Export
{
    // Prend un BankDocument deja parse (voir DocumentProcessingService/BankDocumentParser) et
    // produit un classeur .xlsx : une feuille par BankAccountSection, avec Total Debit/Total
    // Credit en haut (voir TransactionSumCalculator - aucune validation, aucune comparaison
    // avec le solde), suivi du bloc d'en-tete (Banque/Compte/Devise/RIB/Soldes) puis du tableau
    // Date/Libelle/Debit/Credit. Ne touche jamais au parsing : ne lit que les proprietes deja
    // presentes sur BankDocument.
    public class BankExcelExporter
    {
        private static readonly char[] InvalidSheetChars = { '\\', '/', '?', '*', '[', ']', ':' };
        private readonly TransactionSumCalculator _sumCalculator = new();

        // metadata est optionnel (defaut null) : n'affecte aucun appelant existant qui ne le
        // fournit pas encore. Ne contient que CustomerName/Period, deja produits par
        // GenericDocumentMetadataExtractor - aucune nouvelle detection ici.
        public byte[] Export(BankDocument document, DocumentMetadata? metadata = null)
        {
            using var workbook = new XLWorkbook();
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (document.Accounts.Count == 0)
            {
                AddAccountSheet(workbook, document.BankName, new BankAccountSection(), usedSheetNames, metadata);
            }
            else
            {
                foreach (var account in document.Accounts)
                {
                    try
                    {
                        AddAccountSheet(workbook, document.BankName, account, usedSheetNames, metadata);
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

        private static string FormatPeriod(ExtractionPeriod? period)
        {
            if (period == null) return "";
            if (!string.IsNullOrWhiteSpace(period.Start) && !string.IsNullOrWhiteSpace(period.End))
                return $"{period.Start} - {period.End}";
            if (!string.IsNullOrWhiteSpace(period.End))
                return $"au {period.End}";
            return "";
        }

        private void AddAccountSheet(XLWorkbook workbook, string bankName, BankAccountSection account, HashSet<string> usedSheetNames, DocumentMetadata? metadata)
        {
            var sheet = workbook.Worksheets.Add(BuildSheetName(account.AccountNumber, usedSheetNames));

            var sums = _sumCalculator.Calculate(account);

            // BIAT (voir BankDocumentParser, capture "TOTAUX <debit> <credit>") : quand le total
            // imprime par la banque a pu etre lu sur le releve, on l'affiche a la place de la somme
            // des transactions - il inclut le solde de depart comme mouvement (convention propre a
            // ce releve) et correspond donc au chiffre que l'utilisateur voit sur le papier. Repli
            // silencieux sur la somme calculee si absent (OCR n'a pas trouve la ligne). Gate sur le
            // nom de banque : aucun changement pour les autres formats.
            if (bankName.Contains("BIAT", StringComparison.OrdinalIgnoreCase))
            {
                if (account.TotalDebit.HasValue) sums.TotalDebit = account.TotalDebit.Value;
                if (account.TotalCredit.HasValue) sums.TotalCredit = account.TotalCredit.Value;
            }

            int row = WriteSumsSummary(sheet, sums);
            row++; // ligne vide de separation

            sheet.Cell(row, 1).Value = "Banque :";
            sheet.Cell(row, 2).Value = bankName;
            row++;

            sheet.Cell(row, 1).Value = "Société :";
            sheet.Cell(row, 2).Value = metadata?.CustomerName ?? "";
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

            sheet.Cell(row, 1).Value = "Période :";
            sheet.Cell(row, 2).Value = FormatPeriod(metadata?.Period);
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
            sheet.Cell(headerRow, 5).Value = "Compte Débit";
            sheet.Cell(headerRow, 6).Value = "Compte Crédit";
            sheet.Range(headerRow, 1, headerRow, 6).Style.Font.Bold = true;
            row++;

            string amountFormat = string.Equals(account.Currency?.Trim(), "TND", StringComparison.OrdinalIgnoreCase)
                ? "#,##0.000"
                : "#,##0.00";

            // Banque Zitouna : couche d'imputation dediee (voir ZitounaLedgerAccountClassifier),
            // completement separee des regles generiques ci-dessous - aucune autre banque n'est
            // affectee par cette branche. Detection via RawSectionText (BankName reste vide pour
            // ces relevés aujourd'hui, voir ZitounaLedgerAccountClassifier.IsZitounaDocument) sans
            // toucher a BankDocumentParser.
            bool isZitouna = ZitounaLedgerAccountClassifier.IsZitounaDocument(account, metadata?.BankName ?? bankName);

            foreach (var tx in account.Transactions)
            {
                if (isZitouna)
                {
                    var lignes = ZitounaLedgerAccountClassifier.Classify(tx, metadata?.CustomerName);
                    if (lignes.Count == 0)
                    {
                        row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle, tx.Debit, tx.Credit, null, null, amountFormat);
                    }
                    else if (lignes.Count == 1)
                    {
                        row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle, tx.Debit, tx.Credit,
                            lignes[0].CompteDebit, lignes[0].CompteCredit, amountFormat);
                    }
                    else
                    {
                        row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle, tx.Debit, tx.Credit,
                            lignes[0].CompteDebit, lignes[0].CompteCredit, amountFormat);
                        row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle + " (dupliqué)", tx.Debit, tx.Credit,
                            lignes[1].CompteDebit, lignes[1].CompteCredit, amountFormat);
                    }
                    continue;
                }

                if (LedgerAccountClassifier.IsRetraitEspeces(tx))
                {
                    // Retrait especes : dedoublement comptable via le compte intermediaire
                    // (voir LedgerAccountClassifier.IsRetraitEspeces) - deux lignes, comptes
                    // inverses entre elles, au lieu du classement habituel Debit/Credit.
                    row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle, tx.Debit, tx.Credit,
                        LedgerAccountClassifier.CompteIntermediaire, LedgerAccountClassifier.CompteCaisse, amountFormat);
                    row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle + " (dupliqué)", tx.Debit, tx.Credit,
                        LedgerAccountClassifier.CompteCaisse, LedgerAccountClassifier.CompteIntermediaire, amountFormat);
                    continue;
                }

                var (compteDebit, compteCredit) = LedgerAccountClassifier.Classify(tx, metadata?.CustomerName);
                row = WriteTransactionRow(sheet, row, tx.Date, tx.Libelle, tx.Debit, tx.Credit, compteDebit, compteCredit, amountFormat);
            }

            sheet.Column(1).Width = 14;
            sheet.Column(2).Width = 60;
            sheet.Column(3).Width = 16;
            sheet.Column(4).Width = 16;
            sheet.Column(5).Width = 16;
            sheet.Column(6).Width = 16;
        }

        // Ecrit une ligne Date/Libelle/Debit/Credit/Compte Debit/Compte Credit et retourne la
        // prochaine ligne libre. Factorise pour permettre au retrait especes d'ecrire deux
        // lignes (voir la boucle d'appel dans AddAccountSheet).
        private static int WriteTransactionRow(IXLWorksheet sheet, int row, string date, string libelle,
            decimal? debit, decimal? credit, string? compteDebit, string? compteCredit, string amountFormat)
        {
            // Ecrit une vraie date Excel (pas du texte) quand le format est reconnu, pour que le
            // tri/filtre par date et les formules de date fonctionnent nativement dans Excel, sans
            // que l'utilisateur ait besoin de convertir manuellement la colonne. Repli sur le texte
            // brut si la date n'a pas pu etre normalisee par le parseur (valeur vide ou format
            // inattendu) - aucune perte d'information dans ce cas rare.
            if (DateTime.TryParseExact(date, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                sheet.Cell(row, 1).Value = parsedDate;
                sheet.Cell(row, 1).Style.DateFormat.Format = "dd/mm/yyyy";
            }
            else
            {
                sheet.Cell(row, 1).Value = date;
            }

            sheet.Cell(row, 2).Value = libelle;

            if (debit.HasValue)
            {
                sheet.Cell(row, 3).Value = debit.Value;
                sheet.Cell(row, 3).Style.NumberFormat.Format = amountFormat;
            }
            if (credit.HasValue)
            {
                sheet.Cell(row, 4).Value = credit.Value;
                sheet.Cell(row, 4).Style.NumberFormat.Format = amountFormat;
            }
            if (compteDebit != null) sheet.Cell(row, 5).Value = compteDebit;
            if (compteCredit != null) sheet.Cell(row, 6).Value = compteCredit;

            return row + 1;
        }

        // Ecrit uniquement Total Debit / Total Credit (voir TransactionSumCalculator) tout en
        // haut de la feuille, avant le bloc Banque/Compte habituel. Aucune validation, aucune
        // comparaison avec le solde. Retourne la prochaine ligne libre.
        private static int WriteSumsSummary(IXLWorksheet sheet, TransactionSums sums)
        {
            int row = 1;

            sheet.Cell(row, 1).Value = "Total Débit :";
            sheet.Cell(row, 2).Value = sums.TotalDebit;
            sheet.Range(row, 1, row, 2).Style.Font.Bold = true;
            row++;

            sheet.Cell(row, 1).Value = "Total Crédit :";
            sheet.Cell(row, 2).Value = sums.TotalCredit;
            sheet.Range(row, 1, row, 2).Style.Font.Bold = true;
            row++;

            return row;
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
