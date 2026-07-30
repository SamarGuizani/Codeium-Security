using System.Globalization;
using System.Text.RegularExpressions;
using Codeium_Security.Interfaces;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentParsers
{
    public class BankDocumentParser : IDocumentParser
    {
        public DocumentType SupportedType => DocumentType.Bank;

        private static readonly Regex DateRegex =
            new(@"\d{2}[/\-.]\d{2}[/\-.]\d{4}|\d{8}");

        private static readonly Regex AmountRegex =
            new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");

        public object Parse(string fullText, List<TextLine> lines)
        {
            var engine = new DocumentAnalysisEngine();
            var rows = engine.BuildTable(lines);

            var document = new BankDocument
            {
                BankName = ExtractBankName(fullText),
                Accounts = ExtractAccountSections(rows, fullText)
            };

            return document;
        }

        private List<BankAccountSection> ExtractAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            BankAccountSection? current = null;
            decimal? previousSolde = null;
            string lastSeenAccountNumber = "";
            string lastSeenRib = "";
            string pendingLibelleBuffer = "";
            string pendingDate = "";
            string sectionRawText = "";

            // [COMMUN - TOUTES BANQUES] Calibration des colonnes par en-tete
            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null;

            // [COMMUN] Repli document-entier pour le RIB (fix #3) : certains RIB sont coupes
            // sur plusieurs lignes/cellules et ne matchent jamais correctement ligne par ligne.
            string documentRib = ExtractRib(fullText);


            bool isAmenDocument = fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) >= 0;


            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var cellTextsRaw = cells
                    .Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);
                // [fix #9] Nettoie les artefacts d'impression web (ex: export BTK@DIRECT) qui
                // injectent une entete/pied de page en plein milieu du contenu.
                joined = StripPrintArtifacts(joined);
                if (string.IsNullOrWhiteSpace(joined)) continue;
                sectionRawText += joined + "\n";

                // [COMMUN] Numero de compte (fix #1) : plusieurs formats selon la banque
                var account = ExtractAccountNumber(joined);
                if (string.IsNullOrWhiteSpace(account))
                    account = ExtractAccountNumber(fullText);
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                // [COMMUN] RIB / Code IBAN (format TN + chiffres) (fix #3)
                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();

                // [COMMUN] Detection des en-tetes de colonnes Debit/Credit/Solde
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Solde$", RegexOptions.IgnoreCase));
                if (debitCell != null || creditCell != null)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    continue;
                }

                // [ATTIJARI] "Solde (TND) au [date] : [montant]" -> capture comme SoldeFinal, PAS comme nouvelle section
                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }

                // [QNB] "Solde Initial" = debut d'une nouvelle section/sous-compte
                if (Regex.IsMatch(joined, @"Solde\s*Initial", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    var initMatch = AmountRegex.Match(joined);
                    decimal soldeInit = initMatch.Success ? ParseAmount(initMatch.Value) : 0;

                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        // [fix #3] Repli sur le RIB document-entier si aucun RIB local n'a ete vu
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [BIAT] "SOLDE AU 30 09 2023 597,014" = debut de section (equivalent du "Solde Initial" QNB)
                // C'est la cause racine du bug "Accounts: []" pour BIAT : sans ce declencheur,
                // 'current' restait toujours null et TOUTES les transactions etaient ignorees
                // via le "if (current == null) continue;" plus bas.
                var biatSoldeAuMatch = Regex.Match(joined, @"SOLDE\s*AU\s*\d{1,2}\s+\d{1,2}\s+\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeAuMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal biatSoldeInit = ParseAmount(biatSoldeAuMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = biatSoldeInit
                    };
                    previousSolde = biatSoldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [QNB] "Solde Final" = fin de section
                if (Regex.IsMatch(joined, @"Solde\s*Final", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                // [BIAT] "Devise SOLDE 1.667,523" = solde final (uniquement sur la derniere page ;
                // les autres pages n'ont que "Devise SOLDE" sans montant, d'ou le lookahead negatif
                // pour ne jamais confondre avec le declencheur "SOLDE AU ..." ci-dessus)
                var biatSoldeFinalMatch = Regex.Match(joined, @"\bSOLDE\b(?!\s*AU)\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinalMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinalMatch.Groups[1].Value);
                    continue;
                }

                // [COMMUN] Ignore les lignes de Total et de numero de page
                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                // [AMEN] Ligne d'echo de la "date valeur" au format POINT (ex: "30.07.2025 VMA") = suite du
                // libelle de la transaction PRECEDENTE (retour a la ligne dans la cellule Libelle du PDF),
                // PAS une nouvelle operation. Sans ce garde-fou : NormalizeDate() la reconnait quand meme comme
                // date valide (dd.MM.yyyy), ET AmountRegex matche "30.07" a l'interieur de cette meme date
                // (le point est aussi un separateur de montant valide) -> fausse transaction fantome avec un
                // faux Debit/Credit. Restreint a AMEN + format POINT uniquement : les vraies dates d'operation
                // AMEN sont toujours en dd/MM/yyyy (avec des "/"), donc ceci n'intercepte jamais une vraie
                // ligne de transaction, ni chez AMEN ni chez les autres banques (BIAT/QNB/ATTIJARI n'ont pas
                // cette echo de date en point).
                if (isAmenDocument
      && current != null
      && current.Transactions.Count > 0
    && !joined.Contains('/')
&& Regex.IsMatch(joined.Trim(), @"^\d{2}\.\d{2}\.\d{4}\s*\S*$"))
                {
                    var lastTx = current.Transactions[current.Transactions.Count - 1];
                    lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    continue;
                }

                string normalizedDate = NormalizeDate(cellTexts[0].Trim());

                // [COMMUN] Fusionne le signe "-" isole AVEC sa position X, pour les montants de cette ligne
                var mergedWithPos = new List<(int Left, string Text)>();
                for (int i = 0; i < cells.Count; i++)
                    mergedWithPos.Add((cells[i].Left, NormalizeSignSpacing(CleanWhitespace(cells[i].Text))));
                for (int i = 0; i < mergedWithPos.Count - 1; i++)
                {
                    if (mergedWithPos[i].Text.Trim() == "-")
                    {
                        mergedWithPos[i + 1] = (mergedWithPos[i + 1].Left, "-" + mergedWithPos[i + 1].Text.TrimStart());
                        mergedWithPos.RemoveAt(i);
                        i--;
                    }
                }

                var amountCandidates = mergedWithPos
                    .Where(c => AmountRegex.IsMatch(c.Text))
                    .Select(c => new { Left = c.Left, Value = ParseAmount(AmountRegex.Match(c.Text).Value) })
                    .ToList();

                // [fix generique] Filet de securite : si aucune formule connue ("Solde Initial",
                // "SOLDE AU", "Solde (TND) au", ...) n'a permis d'ouvrir une section de compte, mais
                // qu'on est manifestement sur une vraie ligne de transaction (date + au moins un
                // montant), on ouvre une section par defaut plutot que de perdre silencieusement
                // tout le releve (c'etait le cas d'AMEN BANK, dont la formule de solde n'est pas
                // encore connue du parser).
                if (current == null)
                {
                    if (!string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = null
                        };
                        sectionRawText = joined + "\n";
                    }
                    else
                    {
                        continue;
                    }
                }

                // [COMMUN] Detecte le bruit (footers, mentions legales) qui ne doit jamais rejoindre un libelle
                bool isNoise = Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+)", RegexOptions.IgnoreCase);

                if (string.IsNullOrEmpty(normalizedDate))
                {
                    if (isNoise) continue;

                    bool isPureAmountLine = amountCandidates.Count > 0;

                    if (isPureAmountLine)
                    {
                        // Vraie ligne de montants (ex: "35.700   -2.713.339"), presque aucun texte a cote
                        if (string.IsNullOrEmpty(pendingDate)) continue;

                        string fullDesc = pendingLibelleBuffer.Trim();
                        pendingLibelleBuffer = "";

                        // [fix anti-fusion] Ligne fusionnee : plusieurs montants Debit ou plusieurs
                        // montants Credit d'un coup -> on reconstruit une transaction par montant
                        // au lieu d'en perdre silencieusement.
                        if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                        {
                            foreach (var splitTx in SplitMergedRow(pendingDate, fullDesc, amountCandidates, debitAnchor, creditAnchor))
                                if (!IsDuplicateOfLast(current, splitTx))
                                    current.Transactions.Add(splitTx);
                            pendingDate = "";
                            continue;
                        }

                        decimal? soldeAvant2 = previousSolde;
                        var tx2 = new Transaction { Date = pendingDate, Libelle = fullDesc };
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde);
                        ApplyMovementFallback(tx2, soldeAvant2);

                        // [fix #11] Ignore les doublons stricts (ex: repetition en haut de page
                        // lors d'une impression web comme BTK@DIRECT)
                        if (!IsDuplicateOfLast(current, tx2))
                            current.Transactions.Add(tx2);
                        pendingDate = "";
                        continue;
                    }

                    // [fix #4] Texte de continuation : comme "engine.BuildTable(lines)" regroupe deja
                    // les cellules en lignes visuelles coherentes, on se base sur l'absence de montant
                    // sur la ligne (deja verifie ci-dessus) sans dependance a une bounding box non
                    // exposee par TableCell. Si votre modele expose une propriete de position verticale
                    // (ex: Y, Bottom, Line.Top...), on peut reintroduire un filtre de hauteur ici.
                    if (amountCandidates.Count == 0)
                    {
                        if (current.Transactions.Count > 0 && string.IsNullOrEmpty(pendingDate))
                        {
                            var lastTx = current.Transactions[current.Transactions.Count - 1];
                            lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                        }
                        else
                        {
                            pendingLibelleBuffer = (pendingLibelleBuffer + " " + joined.Trim()).Trim();
                        }
                    }
                    continue;
                }

                // Ligne AVEC une date valide
                if (amountCandidates.Count == 0)
                {
                    // [ATTIJARI] Date + description, montant sur la ligne suivante -> on retient la date et le texte
                    pendingDate = normalizedDate;
                    string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)));
                    pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    continue;
                }

                // [QNB] Cas normal : date + montant(s) sur la meme ligne
                string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                // [fix BIAT] "Date de valeur" collee sans separateur (ex: "03102023") qui se retrouve
                // au milieu du libelle plutot que dans une colonne dediee
                description = Regex.Replace(description, @"\b\d{8}\b", "").Trim();
                description = Regex.Replace(description, @"\s{2,}", " ").Trim();

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";

                // [fix anti-fusion] Meme controle que pour les lignes de montants pures : une
                // ligne "date + description + montants" peut aussi etre la fusion de plusieurs
                // transactions physiques.
                if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                {
                    foreach (var splitTx in SplitMergedRow(normalizedDate, fullDescription, amountCandidates, debitAnchor, creditAnchor))
                        if (!IsDuplicateOfLast(current, splitTx))
                            current.Transactions.Add(splitTx);
                    continue;
                }

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde);
                ApplyMovementFallback(tx, soldeAvantTx);

                if (!IsDuplicateOfLast(current, tx))
                    current.Transactions.Add(tx);
            }

            if (current != null)
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }

            foreach (var sec in sections)
            {
                // [QNB] Format "Total Debit: X" / "Total Credit: Y" avec mot-cle repete
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

                // [ATTIJARI/BIAT] Repli : "Total" suivi de 2 montants sans mot-cle repete
                if (!sec.TotalDebit.HasValue && !sec.TotalCredit.HasValue)
                {
                    var totalTwoNumbers = Regex.Match(sec.RawSectionText,
                        @"Total\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (totalTwoNumbers.Success)
                    {
                        sec.TotalDebit = ParseAmount(totalTwoNumbers.Groups[1].Value);
                        sec.TotalCredit = ParseAmount(totalTwoNumbers.Groups[2].Value);
                    }
                }
            }

            return sections;
        }

        // [COMMUN] (fix #1) Detection du numero de compte, plusieurs formats/banques
        private string ExtractAccountNumber(string text)
        {
            var patterns = new[]
            {
                @"\b\d{2,5}-\d{4,12}-\d{1,4}\b",
                @"\b\d{10,20}\b",
                @"Compte\s*:?\s*(\d[\d\s-]{8,25})",
                @"Account\s*Number\s*:?\s*(\d[\d\s-]{8,25})",
                @"N[°o]?\s*Compte\s*:?\s*(\d[\d\s-]{8,25})"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                    return Regex.Replace(match.Value, @"\s+", "");
            }

            return "";
        }

        // [COMMUN] (fix #3) RIB document-entier, avec une plage plus large en repli
        // car le RIB peut etre coupe sur plusieurs lignes/cellules.
        private string ExtractRib(string text)
        {
            var wide = Regex.Match(text, @"TN\d{2}[\d\s]{20,30}");
            if (wide.Success)
                return Regex.Replace(wide.Value, @"\s+", "").Trim();

            var narrow = Regex.Match(text, @"TN\d{2}[\s\d]{15,25}");
            if (narrow.Success)
                return Regex.Replace(narrow.Value, @"\s+", "").Trim();

            return "";
        }

        // [fix anti-fusion] Detecte si une "ligne" contient plus d'un montant Debit OU plus d'un
        // montant Credit. Ca indique que DocumentAnalysisEngine.BuildTable a fusionne plusieurs
        // lignes physiques du PDF (verticalement trop proches, ex: releves BIAT tres denses) en
        // une seule TableRow. Sans ce controle, AssignAmounts ne gardait qu'un montant par colonne
        // et les autres disparaissaient silencieusement, sans trace ni erreur.
        // NOTE : la vraie cause reste dans DocumentAnalysisEngine.BuildTable (regroupement par Y),
        // que je n'ai pas ici. Ceci est un filet de recuperation en aval, pas une correction a la
        // source — si vous pouvez partager le code de BuildTable, on peut resserrer la tolerance
        // de regroupement verticale directement a la source, ce qui serait plus propre.
        private bool IsMergedRow(dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor)
        {
            if (!debitAnchor.HasValue || !creditAnchor.HasValue) return false;

            int debitCount = 0, creditCount = 0;
            foreach (var cand in amountCandidates)
            {
                int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;
                if (soldeAnchor.HasValue && distSolde <= distDebit && distSolde <= distCredit) continue;
                if (distDebit < distCredit) debitCount++; else creditCount++;
            }
            return debitCount > 1 || creditCount > 1;
        }

        // [fix anti-fusion] Reconstruit une transaction par montant Debit/Credit trouve dans une
        // ligne fusionnee, plutot que de perdre silencieusement toutes les transactions sauf une.
        // Le libelle ne peut pas etre redecoupe de facon fiable (le texte est aussi fusionne) donc
        // il est partage entre les sous-transactions, chacune marquee pour verification manuelle.
        private List<Transaction> SplitMergedRow(string date, string libelle, dynamic amountCandidates, int? debitAnchor, int? creditAnchor)
        {
            var debits = new List<decimal>();
            var credits = new List<decimal>();
            foreach (var cand in amountCandidates)
            {
                int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                if (distDebit < distCredit) debits.Add(cand.Value); else credits.Add(cand.Value);
            }

            int count = Math.Max(debits.Count, credits.Count);
            var result = new List<Transaction>();
            for (int i = 0; i < count; i++)
            {
                var tx = new Transaction
                {
                    Date = date,
                    Libelle = $"{libelle} [LIGNE FUSIONNEE {i + 1}/{count} - a verifier manuellement]".Trim()
                };
                if (i < debits.Count) tx.Debit = debits[i];
                if (i < credits.Count) tx.Credit = credits[i];
                result.Add(tx);
            }
            return result;
        }

        // [COMMUN] Assigne Debit/Credit/Solde soit par position de colonne (fiable), soit par comparaison de solde (repli)
        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, ref decimal? previousSolde)
        {
            // [fix #5] Tolerance en pixels : certaines banques decalent legerement les colonnes
            // Debit/Credit, ce qui peut inverser une classification purement basee sur la distance.
            const int Tolerance = 15;

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

                foreach (var cand in amountCandidates)
                {
                    int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                    int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                    int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;

                    if (distSolde <= distDebit && distSolde <= distCredit)
                    {
                        tx.Solde = cand.Value;
                    }
                    else if (Math.Abs(distDebit - distCredit) < Tolerance)
                    {
                        // Colonne ambigue -> on tranchera apres coup via le solde plutot
                        // que sur la seule position X (peu fiable a ce niveau de tolerance).
                        ambiguous.Add(cand);
                    }
                    else if (distDebit < distCredit)
                    {
                        tx.Debit = cand.Value;
                    }
                    else
                    {
                        tx.Credit = cand.Value;
                    }
                }

                // [fix BIAT] Ne recopier le dernier montant comme "Solde" que si une colonne Solde
                // existe reellement (soldeAnchor connu). Sinon (ex: BIAT qui n'a que Debit/Credit,
                // sans colonne de solde courant), ce serait fabriquer une donnee qui n'existe pas.
                if (!tx.Solde.HasValue && soldeAnchor.HasValue && amountCandidates.Count > 0)
                    tx.Solde = amountCandidates[amountCandidates.Count - 1].Value;

                foreach (var cand in ambiguous)
                {
                    decimal value = cand.Value;
                    if (previousSolde.HasValue && tx.Solde.HasValue)
                    {
                        if (tx.Solde.Value < previousSolde.Value) tx.Debit = value;
                        else if (tx.Solde.Value > previousSolde.Value) tx.Credit = value;
                    }
                    else if (value < 0)
                    {
                        tx.Debit = Math.Abs(value);
                    }
                    else
                    {
                        tx.Credit = value;
                    }
                }
            }
            else
            {
                var amounts = new List<decimal>();
                foreach (var a in amountCandidates) amounts.Add((decimal)a.Value);

                if (amounts.Count == 1)
                {
                    tx.Solde = amounts[0];
                }
                else
                {
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    tx.Solde = solde;
                    if (previousSolde.HasValue)
                    {
                        if (solde < previousSolde.Value) tx.Debit = mouvement;
                        else if (solde > previousSolde.Value) tx.Credit = mouvement;
                    }
                    else tx.Debit = mouvement;
                }
            }
            previousSolde = tx.Solde;
        }

        // [fix #2] Formats de date etendus (avec et sans annee)
        private string NormalizeDate(string raw)
        {
            raw = raw.Trim();
            string candidate = raw;

            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.'))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";
            // [fix BIAT] jour+mois colles sans separateur ni annee (ex: "1110" -> 11/10, annee courante)
            else if (raw.Length == 4 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' '))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}";

            var formatsWithYear = new[]
            {
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy",
                "yyyy-MM-dd", "yyyy/MM/dd"
            };

            if (DateTime.TryParseExact(candidate, formatsWithYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd/MM/yyyy");
            }

            // [COMMUN] Certains releves n'indiquent que jour/mois (l'annee est donnee ailleurs
            // dans l'en-tete du document) -> on retombe sur l'annee courante par defaut.
            var formatsNoYear = new[] { "dd/MM", "dd-MM", "dd.MM", "dd MM" };
            if (DateTime.TryParseExact(candidate, formatsNoYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault, out var parsedNoYear))
            {
                var withYear = new DateTime(DateTime.Now.Year, parsedNoYear.Month, parsedNoYear.Day);
                return withYear.ToString("dd/MM/yyyy");
            }

            return "";
        }

        private decimal ParseAmount(string raw)
        {
            int lastSepIndex = -1;
            for (int i = raw.Length - 1; i >= 0; i--)
            {
                if (raw[i] == '.' || raw[i] == ',' || raw[i] == ' ')
                {
                    lastSepIndex = i;
                    break;
                }
            }

            if (lastSepIndex == -1)
            {
                decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var direct);
                return direct;
            }

            string integerPart = raw.Substring(0, lastSepIndex).Replace(".", "").Replace(",", "").Replace(" ", "");
            string decimalPart = raw.Substring(lastSepIndex + 1);

            decimal.TryParse(integerPart + "." + decimalPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var result);
            return result;

            // [fix #6 - NOTE] Si l'OCR "mange" un chiffre (ex: "9853.763" lu "853.763"), ce n'est PAS
            // corrige ici : le parser n'a aucun moyen de deviner un chiffre disparu sans risquer de
            // fausser des montants corrects. A traiter en amont (qualite OCR) ou via une verification
            // metier separee (coherence du nouveau solde vs solde precedent +/- mouvement).
        }

        // [fix #7] Normalise les espaces/caracteres invisibles ET le signe moins Unicode ("−" -> "-")
        private string CleanWhitespace(string text) =>
            text.Replace('\u00A0', ' ')
                .Replace('\u202F', ' ')
                .Replace('\u2212', '-');

        // [fix #7] Recolle un signe "-" separe de son montant par un espace dans la meme cellule
        // (ex: "-   150.000" -> "-150.000"), sans toucher aux cas deja geres par MergeLoneSignCells.
        private string NormalizeSignSpacing(string text) =>
            Regex.Replace(text, @"^(\s*-)\s+(?=\d)", "-");

        // [fix #9] Les impressions "web" de certains portails (ex: export BTK@DIRECT) injectent une
        // entete/pied de page en plein milieu du contenu : URL, pagination "x/y", horodatage
        // navigateur, filigrane du portail, debut de l'entete suivante ("Date de valeur...").
        // Des qu'on detecte le debut de ce bloc (l'URL), on tronque : le reste n'appartient pas
        // au libelle de la transaction.
        private string StripPrintArtifacts(string text)
        {
            var urlMatch = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                text = text.Substring(0, urlMatch.Index);

            // Filigrane / horodatage residuels si jamais coupes autrement (sans URL devant,
            // ou repartis sur plusieurs cellules distinctes)
            text = Regex.Replace(text, @"\bBTK@?DIRECT\b", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b\d{1,2}/\d{1,2}/\d{2,4},?\s*\d{1,2}:\d{2}\s*(AM|PM)\b", "", RegexOptions.IgnoreCase);

            return CleanWhitespace(text).Trim();
        }

        // [fix #10] Repli : si aucune colonne Debit/Credit n'a pu etre assignee (montant illisible,
        // coupe par du bruit OCR/impression, colonnes non calibrees...) mais que le solde avant et
        // apres la ligne sont tous les deux connus, on deduit le mouvement par simple difference.
        private void ApplyMovementFallback(Transaction tx, decimal? soldeAvant)
        {
            if (tx.Debit.HasValue || tx.Credit.HasValue) return;
            if (!soldeAvant.HasValue || !tx.Solde.HasValue) return;

            decimal diff = tx.Solde.Value - soldeAvant.Value;
            if (diff < 0) tx.Debit = Math.Abs(diff);
            else if (diff > 0) tx.Credit = diff;
        }

        // [fix #11] Certains exports "impression web" dupliquent une ligne de mouvement identique
        // a la precedente (repetition en haut de la page suivante). On ignore le doublon strict.
        private bool IsDuplicateOfLast(BankAccountSection section, Transaction tx)
        {
            if (section.Transactions.Count == 0) return false;
            var last = section.Transactions[section.Transactions.Count - 1];
            return last.Date == tx.Date
                && last.Libelle == tx.Libelle
                && last.Debit == tx.Debit
                && last.Credit == tx.Credit
                && last.Solde == tx.Solde;
        }

        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
        }

        private List<string> MergeLoneSignCells(List<string> cellTexts)
        {
            var merged = new List<string>(cellTexts);
            for (int i = 0; i < merged.Count - 1; i++)
            {
                if (merged[i].Trim() == "-")
                {
                    merged[i + 1] = "-" + merged[i + 1].TrimStart();
                    merged.RemoveAt(i);
                    i--;
                }
            }
            return merged;
        }

        private string ExtractBankName(string text)
        {
            var knownBanks = new (string Keyword, string FullName)[]
            {
                ("BNA", "Banque Nationale Agricole (BNA)"),
                ("BIAT", "Banque Internationale Arabe de Tunisie (BIAT)"),
                ("STB", "Société Tunisienne de Banque (STB)"),
                ("ATB", "Arab Tunisian Bank (ATB)"),
                ("UIB", "Union Internationale de Banques (UIB)"),
                ("ATTIJARI", "Attijari Bank"),
                ("AMEN BANK", "Amen Bank"),
                ("BH", "Banque de l'Habitat (BH)")
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}