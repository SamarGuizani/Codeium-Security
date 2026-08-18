using System.Reflection;
using Codeium_Security.Models;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentParsers;
using Xunit;

namespace Codeium_Security.Tests
{
    // Tests unitaires (aucun OCR, aucun mock du reste du pipeline) pour la refonte de la
    // frontiere de transaction demandee par l'utilisateur : RawRows -> LogicalRows via
    // MergeContinuationLines/ClassifyLine (prive), puis mapping par colonne dans
    // ExtractBteAccountSections (prive). Appelle directement les methodes privees de
    // BankDocumentParser par reflexion pour verifier le comportement REEL du code, pas une
    // copie/simulation.
    public class ContinuationBoundaryTests
    {
        private static readonly MethodInfo MergeContinuationLinesMethod =
            typeof(BankDocumentParser).GetMethod("MergeContinuationLines", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static readonly MethodInfo ExtractBteAccountSectionsMethod =
            typeof(BankDocumentParser).GetMethod("ExtractBteAccountSections", BindingFlags.NonPublic | BindingFlags.Instance)!;

        private static List<TableRow> MergeContinuationLines(List<TableRow> rows)
        {
            var parser = new BankDocumentParser();
            return (List<TableRow>)MergeContinuationLinesMethod.Invoke(parser, new object[] { rows })!;
        }

        private static List<BankAccountSection> ExtractBteAccountSections(List<TableRow> rows, string fullText)
        {
            var parser = new BankDocumentParser();
            return (List<BankAccountSection>)ExtractBteAccountSectionsMethod.Invoke(parser, new object[] { rows, fullText })!;
        }

        private static TableRow Row(params string[] cellTexts)
        {
            var row = new TableRow();
            int left = 0;
            foreach (var t in cellTexts)
            {
                row.Cells.Add(new TableCell { Text = t, Left = left });
                left += 500;
            }
            return row;
        }

        // ── Regle 2/4 : "ECHEANCE 25/04/2025" (date seule, sans montant, transaction deja
        // ouverte) doit devenir une continuation - pas une 2e transaction. ────────────────────
        [Fact]
        public void Echeance_DateOnly_Continuation_Is_Merged_Not_A_New_Transaction()
        {
            var rows = new List<TableRow>
            {
                Row("05 05 REGLEMENT EFFET 010181579683 02 05 2025 4 371,003"),
                Row("ECHEANCE 25/04/2025"),
            };

            var result = MergeContinuationLines(rows);

            Assert.Single(result); // une seule ligne logique : la continuation a ete rattachee
            Assert.Equal(2, result[0].Cells.Count);
            Assert.False(result[0].Cells[0].IsContinuationDetail);
            Assert.True(result[0].Cells[1].IsContinuationDetail);
            Assert.Contains("ECHEANCE", result[0].Cells[1].Text);
        }

        // ── Regle 2/4 : "DONT TVA: 0,570" (montant seul, transaction deja ouverte) doit
        // devenir une continuation - pas une 2e transaction. ───────────────────────────────────
        [Fact]
        public void DontTva_AmountOnly_Continuation_Is_Merged_Not_A_New_Transaction()
        {
            var rows = new List<TableRow>
            {
                Row("05 05 COM ET TVA 010181577452 02 05 2025 3,570"),
                Row("DONT TVA: 0,570"),
            };

            var result = MergeContinuationLines(rows);

            Assert.Single(result);
            Assert.Equal(2, result[0].Cells.Count);
            Assert.True(result[0].Cells[1].IsContinuationDetail);
            Assert.Contains("0,570", result[0].Cells[1].Text);
        }

        // ── Regle 2/4 : une vraie ligne transactionnelle ("TVA 010546128672 19/06/25 1,330",
        // date + montant tous deux presents) reste TOUJOURS independante, meme juste apres une
        // transaction deja ouverte avec ses propres continuations. ────────────────────────────
        [Fact]
        public void Real_Tva_Line_With_Date_And_Amount_Stays_An_Independent_Transaction()
        {
            var rows = new List<TableRow>
            {
                Row("05 05 COM ET TVA 010181577452 02 05 2025 3,570"),
                Row("DONT TVA: 0,570"),
                Row("TVA 010546128672 19/06/25 1,330"),
            };

            var result = MergeContinuationLines(rows);

            Assert.Equal(2, result.Count); // transaction 1 (+ sa continuation) puis transaction 2, separees
            Assert.Equal(2, result[0].Cells.Count); // ligne 1 + continuation DONT TVA
            Assert.Single(result[1].Cells); // TVA... reste sa propre ligne, jamais fusionnee
            Assert.False(result[1].Cells[0].IsContinuationDetail);
            Assert.Contains("19/06/25", result[1].Cells[0].Text);
        }

        // ── Regle 7 : "Folio 1" ne doit jamais etre rattache a la derniere transaction. ───────
        [Fact]
        public void Folio_Marker_Is_Never_Attached_As_Continuation()
        {
            var rows = new List<TableRow>
            {
                Row("05 05 REGLEMENT EFFET 010181579683 02 05 2025 4 371,003"),
                Row("Folio 1"),
            };

            var result = MergeContinuationLines(rows);

            Assert.Equal(2, result.Count); // "Folio 1" reste une ligne a part, jamais fusionnee
            Assert.Single(result[0].Cells); // la transaction n'a pas ete polluee
        }

        // ── BTE Regle "critique" : reference bancaire jamais confondue avec un montant, et
        // colonnes assignees par coordonnee X, pas par regex de repli fragile. ─────────────────
        [Fact]
        public void Bte_Reference_Never_Becomes_A_Decimal_Amount_And_Credit_Is_Correctly_Assigned()
        {
            string fullText = "Banque de Tunisie et des Emirats - Extrait de Compte - Periode : du 01/02/2026 au 28/02/2026";

            var rows = new List<TableRow>
            {
                // En-tete : ancre les colonnes par coordonnee X.
                Row2(("D. Opé.", 0), ("Libellé", 150), ("Référence", 700), ("D. Valeur", 850), ("Débit", 1000), ("Crédit", 1150), ("Solde", 1300)),
                Row2(("Solde au : 31/01/2026", 0), ("3995.210", 700), ("CR", 850)),
                Row2(("06/02/2026", 0), ("VERS ESPECES BV N° 1981058", 150), ("1981058", 700), ("09/02/2026", 850), ("10 000.000", 1160), ("9937.083", 1300), ("CR", 1450)),
            };

            var sections = ExtractBteAccountSections(rows, fullText);

            var section = Assert.Single(sections);
            Assert.Equal(3995.210m, section.SoldeInitial);
            var tx = Assert.Single(section.Transactions); // le solde d'ouverture ne cree AUCUNE transaction
            Assert.Equal("06/02/2026", tx.Date);
            Assert.Null(tx.Debit);
            Assert.Equal(10000.000m, tx.Credit); // jamais 1981.058
            Assert.DoesNotContain("1981.058", tx.Libelle);
            Assert.DoesNotContain("1981058", tx.Libelle.Replace("N° 1981058", "")); // seule l'occurrence descriptive reste
            Assert.DoesNotContain("CR", tx.Libelle);
            Assert.Equal("VERS ESPECES BV N° 1981058", tx.Libelle.Trim());
        }

        [Fact]
        public void Bte_Debit_Column_Correctly_Assigned_By_Coordinate()
        {
            string fullText = "Banque de Tunisie et des Emirats - Extrait de Compte";

            var rows = new List<TableRow>
            {
                Row2(("D. Opé.", 0), ("Libellé", 150), ("Référence", 700), ("D. Valeur", 850), ("Débit", 1000), ("Crédit", 1150), ("Solde", 1300)),
                Row2(("Solde au : 09/02/2026", 0), ("9937.083", 700), ("CR", 850)),
                Row2(("09/02/2026", 0), ("COMM REMISE CHQ", 150), ("1290301", 700), ("03/02/2026", 850), ("0.875", 1010), ("9936.208", 1300), ("CR", 1450)),
            };

            var sections = ExtractBteAccountSections(rows, fullText);

            var section = Assert.Single(sections);
            var tx = Assert.Single(section.Transactions);
            Assert.Equal(0.875m, tx.Debit);
            Assert.Null(tx.Credit);
        }

        // ── BTE + Regle 5 : une cellule de continuation rattachee a une transaction BTE ne doit
        // jamais polluer débit/crédit/solde, mais son texte reste dans le libellé. ─────────────
        [Fact]
        public void Bte_Continuation_Cell_Never_Pollutes_Amounts_But_Keeps_Its_Text()
        {
            string fullText = "Banque de Tunisie et des Emirats - Extrait de Compte";

            var txRow = Row2(("06/02/2026", 0), ("VERS ESPECES BV N° 1981058", 150), ("1981058", 700), ("09/02/2026", 850), ("10 000.000", 1160), ("9937.083", 1300), ("CR", 1450));
            txRow.Cells.Add(new TableCell { Text = "DONT TVA: 0,570", Left = 2000, IsContinuationDetail = true });

            var rows = new List<TableRow>
            {
                Row2(("D. Opé.", 0), ("Libellé", 150), ("Référence", 700), ("D. Valeur", 850), ("Débit", 1000), ("Crédit", 1150), ("Solde", 1300)),
                Row2(("Solde au : 31/01/2026", 0), ("3995.210", 700), ("CR", 850)),
                txRow,
            };

            var sections = ExtractBteAccountSections(rows, fullText);

            var section = Assert.Single(sections);
            var tx = Assert.Single(section.Transactions);
            Assert.Equal(10000.000m, tx.Credit); // toujours correct, non pollue par 0,570
            Assert.Null(tx.Debit);
            Assert.Contains("DONT TVA: 0,570", tx.Libelle); // texte conserve
        }

        private static TableRow Row2(params (string Text, int Left)[] cells)
        {
            var row = new TableRow();
            foreach (var (text, left) in cells)
                row.Cells.Add(new TableCell { Text = text, Left = left });
            return row;
        }
    }
}
