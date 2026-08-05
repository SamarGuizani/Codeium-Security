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

        //private static readonly Regex AmountRegex =
        //new(@"-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3}");
        //correction des nombres qui sont avec espaces done 
        private static readonly Regex AmountRegex =
new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");

        // [BIAT] Toute présence de script arabe dans une ligne "texte de continuation" (sans
        // date/montant) identifie de façon fiable le bruit d'en-tête/pied de page bilingue
        // (mentions légales, coordonnées d'agence, etc.) : les libellés d'opérations BIAT sont
        // toujours en français/latin dans les relevés observés.
        private static readonly Regex ArabicScriptRegex = new("[؀-ۿ]");

        private List<TableRow> SplitDuplicatedRows(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Cells.Select(c => c.Text));
                // heuristique : si un motif de 15+ caractères apparaît 2 fois dans la même row,
                // c'est probablement 2 lignes PDF fusionnées à tort -> on garde la row telle quelle
                // mais on la signale pour investigation (log), car un vrai split nécessite les positions Top d'origine.
                if (HasRepeatedSubstring(joined, minLength: 15))
                {
                    Console.WriteLine($"[WARN] Row potentiellement fusionnée (texte dupliqué) : {joined}");
                }
                result.Add(row);
            }
            return result;
        }

        private bool HasRepeatedSubstring(string text, int minLength)
        {
            for (int i = 0; i + minLength * 2 <= text.Length; i++)
            {
                string chunk = text.Substring(i, minLength);
                if (text.IndexOf(chunk, i + minLength, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }
        public object Parse(string fullText, List<TextLine> lines)
        {
            var engine = new DocumentAnalysisEngine();

            string bankName = ExtractBankName(fullText);
            bool isBiat = bankName.Contains("BIAT", StringComparison.OrdinalIgnoreCase);
            bool isZitouna = fullText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase);
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);   // ← ajouter

            if (isBiat || isZitouna || isBtk)
                engine.VerticalTolerance = 1;

            var rows = engine.BuildTable(lines);
            // AJOUT : détecte les rows où le texte semble dupliqué (fusion accidentelle de 2 lignes)
            rows = SplitDuplicatedRows(rows);

            var document = new BankDocument
            {
                BankName = bankName,
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

            // [BIAT] Une fois qu'on a détecté du bruit d'en-tête/pied de page (IsBiatNoise),
            // on reste en "zone de bruit" jusqu'à la prochaine vraie transaction datée. Ça évite
            // de devoir lister tous les fragments possibles d'un même bloc légal/en-tête : un
            // bloc de bruit contient souvent plusieurs lignes fragmentées par l'OCR dont certaines
            // (ex: un simple "»" ou "au -- : :") ne matchent aucun mot-clé individuellement, mais
            // qui ne doivent jamais être recollées à une transaction pour autant.
            bool biatInNoiseZone = false;

            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null, montantAnchor = null;
            string documentRib = ExtractRib(fullText);

            // [BIAT] Récupérer l'année du document
            int? documentYear = null;
            bool isBiat = fullText.Contains("BIAT", StringComparison.OrdinalIgnoreCase);
            if (isBiat)
            {
                var dateMatch = Regex.Match(fullText, @"\b(\d{1,2})\s+(\d{1,2})\s+(\d{4})\b");
                if (dateMatch.Success && int.TryParse(dateMatch.Groups[3].Value, out int year))
                    documentYear = year;
                else
                {
                    var soldeMatch = Regex.Match(fullText, @"SOLDE\s+AU\s+\d{1,2}\s+\d{1,2}\s+(\d{4})", RegexOptions.IgnoreCase);
                    if (soldeMatch.Success && int.TryParse(soldeMatch.Groups[1].Value, out year))
                        documentYear = year;
                }
            }


            // [QNB] Détection de la banque
            bool isQnb = fullText.Contains("QNB", StringComparison.OrdinalIgnoreCase);
            bool isAmenDocument = fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var cellTextsRaw = cells
                    .Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);
                if (cellTexts.Count == 0) continue;

                string joined = string.Join(" ", cellTexts);
                joined = StripPrintArtifacts(joined);
                if (string.IsNullOrWhiteSpace(joined)) continue;
                sectionRawText += joined + "\n";

                //update le 01/08/2026
                // ===== TRAITEMENT SPÉCIFIQUE QNB =====
                if (isQnb)
                {
                    Console.WriteLine($"[QNB-DEBUG] joined='{joined}' | current==null: {current == null}");
                    var qnbMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.*?)\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})$",
                        RegexOptions.IgnoreCase);
                    if (qnbMatch.Success)
                    {
                        string date = qnbMatch.Groups[1].Value;
                        string libelle = qnbMatch.Groups[2].Value.Trim();
                        decimal debit = ParseAmount(qnbMatch.Groups[3].Value);
                        decimal solde = ParseAmount(qnbMatch.Groups[4].Value);

                        if (current == null)
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

                        var qnbTx = new Transaction
                        {
                            Date = NormalizeDate(date, null),
                            Libelle = libelle,
                            //Solde = solde
                        };

                        decimal absVal = Math.Abs(debit);
                        if (debit < 0) qnbTx.Debit = absVal;
                        else if (debit > 0) qnbTx.Credit = absVal;


                        previousSolde = solde;
                        current.Transactions.Add(qnbTx);
                        continue;
                    }
                }

                // ===== FIN TRAITEMENT QNB =====
                // Extraction du numéro de compte
                var account = ExtractAccountNumber(joined);
                //if (string.IsNullOrWhiteSpace(account))
                // account = ExtractAccountNumber(fullText);//je doit supprimer cette lignes le 3 aout 
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                // RIB
                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();

                // Détection des en-têtes Debit/Credit/Solde
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bSolde\b", RegexOptions.IgnoreCase));
                var montantCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bMontant\b", RegexOptions.IgnoreCase));
                if (isBiat)
                {
                    // [BIAT] "عليه"/"له" sont aussi des mots arabes tres courants dans les
                    // mentions legales/formules de politesse repetees a chaque page. Un simple
                    // Contains() sur la cellule matche alors ces phrases (bien plus longues que
                    // le simple libelle d'en-tete) et corrompt debitAnchor/creditAnchor pour
                    // toutes les transactions suivantes. On exige donc une cellule courte
                    // (juste le mot d'en-tete) pour valider le match.
                    var arabicDebit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "عليه"));
                    var arabicCredit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "له"));
                    if (arabicDebit != null) debitCell = arabicDebit;
                    if (arabicCredit != null) creditCell = arabicCredit;
                }

                // [BIAT] Le libellé de certaines opérations BIAT contient littéralement le mot
                // "CREDIT" ou "DEBIT(EURS)" (ex: "DEBLOCAGE CREDIT ECOM", "AGIOS CREDIT ECOM",
                // "INTERETS DEBITEURS"). Le Regex.IsMatch ci-dessus matche alors cette cellule
                // comme s'il s'agissait de l'en-tête de colonne : la ligne entière est traitée
                // comme un en-tête (continue plus bas) et la TRANSACTION DISPARAÎT, tout en
                // corrompant debitAnchor/creditAnchor avec la position de ce mot dans le libellé
                // au lieu de la vraie colonne. Une vraie ligne d'en-tête BIAT n'a jamais de date
                // ni de montant décimal : on l'exige pour confirmer qu'il s'agit bien d'un en-tête
                // avant de la traiter comme tel.
                bool biatLooksLikeTransactionRow = isBiat && (debitCell != null || creditCell != null) &&
                    (!string.IsNullOrEmpty(GetNormalizedDateFromCells(cellTexts, documentYear)) || cells.Any(c => AmountRegex.IsMatch(c.Text)));

                if ((debitCell != null || creditCell != null) && !biatLooksLikeTransactionRow)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    if (montantCell != null) montantAnchor = montantCell.Left;
                    continue;
                }

                // [ATTIJARI] Solde (TND) au ...
                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }

                // [QNB - AMÉLIORÉ] "Solde Initial" avec montant
                var soldeInitMatch = Regex.Match(joined, @"\bSolde\s+Initial\s*([-+]?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeInitMatch.Success || Regex.IsMatch(joined, @"\bSolde\s+Initial\b", RegexOptions.IgnoreCase))
                {
                    // Fermer la section précédente
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    // Récupérer le montant s'il est présent, sinon 0
                    decimal soldeInit = 0;
                    if (soldeInitMatch.Success)
                        soldeInit = ParseAmount(soldeInitMatch.Groups[1].Value);

                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [BIAT] SOLDE AU ...
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

                // [ZITOUNA] Solde actuel
                var zitounaSoldeMatch = Regex.Match(joined, @"Solde\s+actuel\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (zitounaSoldeMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal soldeInit = ParseAmount(zitounaSoldeMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [QNB] "Solde Final"
                if (Regex.IsMatch(joined, @"\bSolde\s+Final\b", RegexOptions.IgnoreCase))
                {
                    if (current != null)
                    {
                        var finalMatch = AmountRegex.Match(joined);
                        current.SoldeFinal = finalMatch.Success ? ParseAmount(finalMatch.Value) : previousSolde;
                    }
                    continue;
                }

                // [BIAT] Devise SOLDE ...
                var biatSoldeFinalMatch = Regex.Match(joined, @"\bSOLDE\b(?!\s*AU)\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinalMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinalMatch.Groups[1].Value);
                    continue;
                }

                // Ignorer les lignes de Total et de page
                if (Regex.IsMatch(joined, @"\b(Total|Page\s*\d)\b", RegexOptions.IgnoreCase))
                    continue;

                // [AMEN] Ligne d'echo de date au format POINT
                if (isAmenDocument && current != null && current.Transactions.Count > 0
                    && !joined.Contains('/') && Regex.IsMatch(joined.Trim(), @"^\d{2}\.\d{2}\.\d{4}\s*\S*$"))
                {
                    var lastTx = current.Transactions[current.Transactions.Count - 1];
                    lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    continue;
                }

                // [QNB] Filtrer les footers et bruits (renforcé pour QNB)
                if (isQnb && Regex.IsMatch(joined, @"\b(Pour toute remarque|N\.B:|support@|hotline|Page\s*\d+ of \d+)\b", RegexOptions.IgnoreCase))
                    continue;

                // [BTK] Filtrer le bruit d'en-tete/pied de page repete a chaque saut de page
                // (ex: "Titulaire du compte : ...", "Valeur Libellé de l'opération Solde (TND)",
                // "Report du 11/01/2024 127,9", "4 Page sur 112"). Sans ce filtre, ces lignes
                // ne matchent ni une date ni un montant valide et sont donc collées comme texte
                // de continuation sur la derniere transaction (voir bloc "Texte de continuation"
                // plus bas), ce qui pollue/fusionne plusieurs opérations BTK entre elles.
                if (isBtk && Regex.IsMatch(joined, @"Titulaire\s+du\s+compte|Valeur\s+Libell[ée]|Report\s+du\s+\d{2}[/\-.]\d{2}[/\-.]\d{2,4}|Page\s+sur\s+\d+", RegexOptions.IgnoreCase))
                    continue;

                // Normalisation de la date
                string normalizedDate = GetNormalizedDateFromCells(cellTexts, isBiat ? documentYear : null);

                // Fusion des signes "-" isolés
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

                // AJOUT : élimine les doublons exacts (même valeur, positions très proches)
                // causés par une fusion accidentelle de 2 lignes PDF en une seule row.
                amountCandidates = amountCandidates
                    .GroupBy(c => new { c.Value, ZoneLeft = c.Left / 20 }) // regroupe par valeur + zone de 20px
                    .Select(g => g.First())
                    .ToList();
                //update le 3 aout 
                // [FIX - montants sans aucun separateur] Un montant peut perdre TOUS ses separateurs
                // lors de l'OCR (ex: "20 798,316" devient "20798316"). AmountRegex ne le reconnait jamais
                // car il exige un vrai separateur decimal. On le detecte UNIQUEMENT si la cellule tombe
                // dans une colonne Debit/Credit/Solde deja reperee par position (evite tout faux positif
                // sur des numeros de reference ou des dates, qui n'apparaissent jamais a ces positions X).
                if (debitAnchor.HasValue || creditAnchor.HasValue || soldeAnchor.HasValue)
                {
                    var bareDigitCandidates = mergedWithPos
                        .Where(c => !AmountRegex.IsMatch(c.Text))
                        .Where(c => Regex.IsMatch(c.Text.Trim(), @"^-?\d{5,10}$"))
                        .Where(c =>
                        {
                            int distDebit = debitAnchor.HasValue ? Math.Abs(c.Left - debitAnchor.Value) : int.MaxValue;
                            int distCredit = creditAnchor.HasValue ? Math.Abs(c.Left - creditAnchor.Value) : int.MaxValue;
                            int distSolde = soldeAnchor.HasValue ? Math.Abs(c.Left - soldeAnchor.Value) : int.MaxValue;
                            int minDist = Math.Min(distDebit, Math.Min(distCredit, distSolde));
                            return minDist < 60;
                        })
                        .Select(c =>
                        {
                            string digits = c.Text.Trim();
                            bool neg = digits.StartsWith("-");
                            if (neg) digits = digits.Substring(1);
                            string intPart = digits.Substring(0, digits.Length - 3);
                            string decPart = digits.Substring(digits.Length - 3);
                            decimal val = decimal.Parse(intPart + "." + decPart, CultureInfo.InvariantCulture);
                            if (neg) val = -val;
                            return new { Left = c.Left, Value = val };
                        })
                        .ToList();

                    amountCandidates.AddRange(bareDigitCandidates);
                    amountCandidates = amountCandidates.OrderBy(a => a.Left).ToList();
                }

                // Création d'une section par défaut si aucune détection précédente
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

                // Détection du bruit général
                bool biatNoiseHit = isBiat && IsBiatNoise(joined);
                if (biatNoiseHit) biatInNoiseZone = true;
                bool isNoise = Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+)", RegexOptions.IgnoreCase)
                    || biatNoiseHit;

                // Si la date est vide, tenter de la trouver dans le libellé (date 8 chiffres)
                if (string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
                {
                    var dateInLibelle = Regex.Match(joined, @"\b(\d{8})\b");
                    if (dateInLibelle.Success)
                        normalizedDate = NormalizeDate(dateInLibelle.Groups[1].Value, isBiat ? documentYear : null);
                }

                if (string.IsNullOrEmpty(normalizedDate))
                {
                    if (isNoise) continue;

                    bool isPureAmountLine = amountCandidates.Count > 0;

                    if (isPureAmountLine)
                    {
                        if (string.IsNullOrEmpty(pendingDate))
                        {
                            // [BTK] Une ligne de commission/TVA (ex: "COM & TVA RET. CARTE") suit
                            // directement une transaction complete (date+montant sur la meme ligne)
                            // sans repeter sa propre date : il n'y a donc jamais de pendingDate pour
                            // elle (pendingDate n'est renseigne que par une ligne "date seule, sans
                            // montant"). Sans ce cas particulier, la ligne est perdue ici (continue)
                            // au lieu de devenir sa propre transaction. On reprend la date de la
                            // derniere transaction de la section, comme sur le releve BTK.
                            if (isBtk && current.Transactions.Count > 0)
                                pendingDate = current.Transactions[current.Transactions.Count - 1].Date;
                            else
                                continue;
                        }

                        string fullDesc = pendingLibelleBuffer.Trim();

                        // [BTK] Comme pour AMEN : cette ligne porte sa PROPRE description (ex.
                        // "COM & TVA RET. CARTE") a cote de son montant, elle n'arrive jamais via
                        // pendingLibelleBuffer (qui n'est alimente que par des lignes sans montant).
                        // Sans ceci, fullDesc resterait vide et le libelle de l'operation serait perdu.
                        if (isAmenDocument || isBtk)
                        {
                            string currentNonAmount = string.Join(" ", cellTexts.Where(c => !AmountRegex.IsMatch(c))).Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{8}\b", "").Trim();
                            if (!string.IsNullOrEmpty(currentNonAmount))
                                fullDesc = (fullDesc + " " + currentNonAmount).Trim();
                        }
                        pendingLibelleBuffer = "";

                        /*if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                        {
                            foreach (var splitTx in SplitMergedRow(pendingDate, fullDesc, amountCandidates, debitAnchor, creditAnchor))
                                if (isBiat)
                                {
                                    current.Transactions.Add(splitTx);
                                }
                                else
                                {

                                    if (!IsDuplicateOfLast(current, splitTx))
                                        current.Transactions.Add(splitTx);
                                }
                                    pendingDate = "";
                            continue;
                        }

                        decimal? soldeAvant2 = previousSolde;
                        var tx2 = new Transaction { Date = pendingDate, Libelle = fullDesc };
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde);
                        ApplyMovementFallback(tx2, soldeAvant2);
                        //update tasli7 biat le 01/08/2026
                        if (isBiat)
                        {
                            current.Transactions.Add(tx2);
                        }
                        else
                        {
                            if (!IsDuplicateOfLast(current, tx2))
                                current.Transactions.Add(tx2);
                        }
                            pendingDate = "";
                        

                            continue;
                        
                    }*/
                        //update le 2 aout 2026 pour corriger le saut des lignes 
                        if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                        {
                            foreach (var splitTx in SplitMergedRow(pendingDate, fullDesc, amountCandidates, debitAnchor, creditAnchor))
                                current.Transactions.Add(splitTx);
                            pendingDate = "";
                            biatInNoiseZone = false;
                            continue;
                        }
                        decimal? soldeAvant2 = previousSolde;
                        var tx2 = new Transaction { Date = pendingDate, Libelle = fullDesc };
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde , out var soldeCourantTX);
                        ApplyMovementFallback(tx2, soldeAvant2, soldeCourantTX);
                        if (isQnb) ApplyQnbSignRule(tx2);   // <-- AJOUTE CETTE LIGNE
                        current.Transactions.Add(tx2);
                        pendingDate = "";
                        biatInNoiseZone = false;
                        continue;
                    }

                    // Texte de continuation
                    if (amountCandidates.Count == 0)
                    {
                        if (current.Transactions.Count > 0 && string.IsNullOrEmpty(pendingDate))
                        {
                            var lastTx = current.Transactions[current.Transactions.Count - 1];
                            // Éviter de coller les footers (et, pour BIAT, tout fragment tant qu'on
                            // est dans une zone de bruit déjà détectée, même s'il ne matche aucun
                            // mot-clé individuellement)
                            if (!isNoise && !biatInNoiseZone && !(isQnb && Regex.IsMatch(joined, @"support|hotline|N\.B", RegexOptions.IgnoreCase)))
                                lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                        }
                        else if (!biatInNoiseZone)
                        {
                            pendingLibelleBuffer = (pendingLibelleBuffer + " " + joined.Trim()).Trim();
                        }
                    }
                    continue;
                }

                // Ligne avec date valide : une vraie date marque le début d'une nouvelle
                // opération, donc la zone de bruit BIAT (s'il y en avait une) est terminée.
                biatInNoiseZone = false;

                // Ligne avec date valide
                if (amountCandidates.Count == 0)
                {
                    pendingDate = normalizedDate;
                    string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)));
                    pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    continue;
                }

              
            if (current != null)
            {
                // Cas normal : date + montant(s)
                string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                description = Regex.Replace(description, @"\b\d{8}\b", "").Trim();
                description = Regex.Replace(description, @"\s{2,}", " ").Trim();

                // [QNB] Ne pas garder les footers dans la description
                if (isQnb && Regex.IsMatch(description, @"support|hotline|N\.B|Pour toute|Cette déclaration", RegexOptions.IgnoreCase))
                    continue;

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";

                /*if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                {
                    foreach (var splitTx in SplitMergedRow(normalizedDate, fullDescription, amountCandidates, debitAnchor, creditAnchor))
                        if (isBiat)
                        {
                            // Pour BIAT, on ajoute TOUTES les transactions, même si elles sont identiques à la précédente
                            current.Transactions.Add(splitTx);
                        }
                        else
                        {

                            if (!IsDuplicateOfLast(current, splitTx))

                                current.Transactions.Add(splitTx);
                        }
                    continue;
                }

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, ref previousSolde);
                ApplyMovementFallback(tx, soldeAvantTx);
                //update je doit ajouter  cette ligne le 01/08 et remplacer par une autre 
                // Pour BIAT : on ajoute TOUTES les transactions, même si elles sont identiques à la précédente
                // (car deux lignes peuvent avoir le même montant et la même date)
                if (isBiat)
                {
                    // On ajoute directement sans vérification de doublon
                    current.Transactions.Add(tx);
                }
                else
                {
                    if (!IsDuplicateOfLast(current, tx))
                        current.Transactions.Add(tx);
                }
            }*/
                //update le 2 aout aussi meme raison
                if (IsMergedRow(amountCandidates, debitAnchor, creditAnchor, soldeAnchor))
                {
                    foreach (var splitTx in SplitMergedRow(normalizedDate, fullDescription, amountCandidates, debitAnchor, creditAnchor))
                        current.Transactions.Add(splitTx);
                    biatInNoiseZone = false;
                    continue;
                }

                decimal? soldeAvantTx = previousSolde;
                var tx = new Transaction { Date = normalizedDate, Libelle = fullDescription };
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, out  var soldeCourantTx);
                ApplyMovementFallback(tx, soldeAvantTx, soldeCourantTx);
                if (isQnb) ApplyQnbSignRule(tx);   // <-- AJOUTE CETTE LIGNE
                current.Transactions.Add(tx);
                biatInNoiseZone = false;
            }
            }

            // Ferme la derniere section en cours (les sections precedentes sont deja
            // fermees/ajoutees a "sections" au moment ou un nouveau "Solde Initial" /
            // "SOLDE AU" / "Solde actuel" est detecte). Sans ce bloc, la derniere section
            // du document n'etait jamais ajoutee a "sections".
            if (current != null && !sections.Contains(current))
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }

            // Calcul des totaux pour chaque section
            foreach (var sec in sections)
            {
                var totalDebitMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?D[ée]bit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalDebitMatch.Success) sec.TotalDebit = ParseAmount(totalDebitMatch.Groups[1].Value);

                var totalCreditMatch = Regex.Match(sec.RawSectionText,
                    @"Total\s*(?:des\s*)?Cr[ée]dit(?:s)?\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (totalCreditMatch.Success) sec.TotalCredit = ParseAmount(totalCreditMatch.Groups[1].Value);

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

        // Toutes les autres méthodes (ExtractAccountNumber, ExtractRib, IsMergedRow, SplitMergedRow, AssignAmounts, NormalizeDate, ParseAmount, etc.) restent inchangées.
        // Assurez-vous que NormalizeDate a le paramètre defaultYear et utilise bien cette valeur.
        // Je les rappelle ici pour mémoire :

        private string NormalizeDate(string raw, int? defaultYear = null)
        {
            raw = raw.Trim();
            string candidate = raw;

            if (Regex.IsMatch(raw, @"^\d{1,2}\s+\d{1,2}\s*$") && !raw.Contains('/') && !raw.Contains('-'))
            {
                candidate = Regex.Replace(raw.Trim(), @"\s+", "/");
            }

            if (raw.Length == 8 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.'))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}/{raw.Substring(4, 4)}";
            else if (raw.Length == 4 && !raw.Contains('/') && !raw.Contains('-') && !raw.Contains('.') && !raw.Contains(' '))
                candidate = $"{raw.Substring(0, 2)}/{raw.Substring(2, 2)}";

            var formatsWithYear = new[] { "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd" };

            if (DateTime.TryParseExact(candidate, formatsWithYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString("dd/MM/yyyy");
            }

            var formatsNoYear = new[] { "dd/MM", "dd-MM", "dd.MM", "dd MM" };
            if (DateTime.TryParseExact(candidate, formatsNoYear, CultureInfo.InvariantCulture,
                    DateTimeStyles.NoCurrentDateDefault, out var parsedNoYear))
            {
                int year = defaultYear ?? DateTime.Now.Year;
                var withYear = new DateTime(year, parsedNoYear.Month, parsedNoYear.Day);
                return withYear.ToString("dd/MM/yyyy");
            }

            return "";
        }

        private string GetNormalizedDateFromCells(List<string> cellTexts, int? defaultYear = null)
        {
            for (int i = 0; i < Math.Min(cellTexts.Count, 3); i++)
            {
                string candidate = string.Join(" ", cellTexts.Take(i + 1));
                string normalized = NormalizeDate(candidate, defaultYear);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
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
        }

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

        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, int? montantAnchor, bool isBtk, ref decimal? previousSolde ,  out decimal? soldeCourant)
        {
            const int Tolerance = 15;

            soldeCourant = null;   // remplace tx.Solde

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

                // [BTK] Chaque ligne BTK n'affiche que Montant (Débit OU Crédit) + Solde,
                // et soldeAnchor n'est pas fiable pour cette banque (l'en-tete "Solde" peut
                // se trouver sur une autre ligne que "Débit"/"Crédit"). On retire donc
                // explicitement le DERNIER montant de la ligne (toujours le solde courant,
                // le plus a droite) avant tout classement Débit/Crédit, pour ne jamais le
                // confondre avec un Crédit. Les autres banques ne sont pas affectees.
                dynamic classifiable = amountCandidates;
                if (isBtk && amountCandidates.Count > 1)
                {
                    var trimmed = new List<dynamic>();
                    foreach (var c in amountCandidates) trimmed.Add(c);
                    soldeCourant = trimmed[trimmed.Count - 1].Value;
                    trimmed.RemoveAt(trimmed.Count - 1);
                    classifiable = trimmed;
                }

                foreach (var cand in classifiable)
                {
                    int distDebit = Math.Abs((int)cand.Left - debitAnchor.Value);
                    int distCredit = Math.Abs((int)cand.Left - creditAnchor.Value);
                    int distSolde = soldeAnchor.HasValue ? Math.Abs((int)cand.Left - soldeAnchor.Value) : int.MaxValue;

                    if (!isBtk && distSolde <= distDebit && distSolde <= distCredit)
                    {
                        soldeCourant = cand.Value;
                    }
                    else if (Math.Abs(distDebit - distCredit) < Tolerance)
                    {
                        ambiguous.Add(cand);
                    }
                    else if (distDebit < distCredit)
                    {
                        //le 4 aout le changement des signes
                        tx.Debit = Math.Abs(cand.Value);

                        //tx.Debit = cand.Value;
                    }
                    else
                    {
                        tx.Credit = Math.Abs(cand.Value);
                        //tx.Credit = cand.Value;
                    }
                }

                if (!isBtk && !soldeCourant.HasValue && soldeAnchor.HasValue && amountCandidates.Count > 0)
                    soldeCourant = amountCandidates[amountCandidates.Count - 1].Value;

                foreach (var cand in ambiguous)
                {
                    decimal value = cand.Value;
                    if (previousSolde.HasValue && soldeCourant.HasValue)
                    {
                        if (soldeCourant.Value < previousSolde.Value) tx.Debit = value;
                        else if (soldeCourant.Value > previousSolde.Value) tx.Credit = value;
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
                    decimal val = amounts[0];
                    if (val < 0) tx.Debit = Math.Abs(val);
                    else if (val > 0) tx.Credit = val;
                    soldeCourant = null;// reste null si pas de solde courant
                  
                }
                
                else
                {
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    soldeCourant = solde;
                    if (previousSolde.HasValue)
                    {
                        if (solde < previousSolde.Value) tx.Debit = mouvement;
                        else if (solde > previousSolde.Value) tx.Credit = mouvement;
                    }
                    else tx.Debit = mouvement;
                }
            }
            previousSolde = soldeCourant;
        }

        private void ApplyMovementFallback(Transaction tx, decimal? soldeAvant , decimal? soldeCourant)
        {
            if (tx.Debit.HasValue || tx.Credit.HasValue) return;
            if (!soldeAvant.HasValue || !soldeCourant.HasValue) return;

            decimal diff = soldeCourant.Value - soldeAvant.Value;
            if (diff < 0) tx.Debit = Math.Abs(diff);
            else if (diff > 0) tx.Credit = diff;
        }

        // [QNB uniquement] Le signe du montant determine Debit/Credit de façon fiable :
        // negatif = Debit, positif = Credit. Ne s'applique qu'aux documents QNB.
        private void ApplyQnbSignRule(Transaction tx)
        {
            decimal? val = tx.Debit ?? tx.Credit;
            if (!val.HasValue) return;

            decimal abs = Math.Abs(val.Value);
            if (val.Value < 0)
            {
                tx.Debit = abs;
                tx.Credit = null;
            }
            else if (val.Value > 0)
            {
                tx.Credit = abs;
                tx.Debit = null;
            }
        }
        private bool IsDuplicateOfLast(BankAccountSection section, Transaction tx)
        {
            if (section.Transactions.Count == 0) return false;
            var last = section.Transactions[section.Transactions.Count - 1];
            return last.Date == tx.Date
                && last.Libelle == tx.Libelle
                && last.Debit == tx.Debit
                && last.Credit == tx.Credit; 
               
        }

        // [BIAT] Bruit d'en-tête/pied de page répété sur chaque page (titulaire du compte,
        // coordonnées d'agence, mentions légales bilingues, nom de banque, "TOTAUX", etc.).
        // Ces lignes ne sont jamais des transactions : sans ce filtre, elles sont collées au
        // libellé de la dernière transaction par la logique générique de "texte de
        // continuation", ce qui pollue/fusionne les opérations à chaque saut de page (la
        // quasi-totalité des relevés BIAT ont plusieurs pages).
        private static bool IsBiatNoise(string text)
        {
            if (ArabicScriptRegex.IsMatch(text)) return true;
            return Regex.IsMatch(text,
                // "Agence\s*:\s*Mr" plutot que "Directeur Agence" : l'OCR tronque parfois le
                // "D" initial ("ecteur Agence : Mr ..."), donc matcher sur "Directeur" seul
                // rate cette ligne à chaque fois qu'elle est mal reconnue.
                @"Titulaire\s+du\s+compte|N[°o]?\s*de\s+compte|\bRIB\b|Cher\s+client|Agence\s*:\s*Mr\b|Fonds\s+de\s+Garantie|RELEVE\s+.*MENSUEL|BANQUE\s+INTERNATIONALE\s+ARABE|TOTAUX|Nous\s+avons\s+l.honneur|Nous\s+vous\s+prions\s+de\s+contacter",
                RegexOptions.IgnoreCase);
        }

        // [BIAT] "عليه"/"له" sont des mots arabes très courants qui apparaissent aussi dans les
        // mentions légales/formules de politesse (bien plus longues qu'un simple libellé
        // d'en-tête). On exige donc une cellule courte pour ne matcher que le véritable en-tête
        // de colonne, et éviter de corrompre debitAnchor/creditAnchor avec la position d'une
        // phrase quelconque contenant accidentellement ce mot.
        private static bool IsBiatArabicHeaderCell(string text, string keyword)
        {
            string trimmed = text.Trim().Trim('‏', '‎', '.', ':', '»', '«', '،', ' ');
            return trimmed.Length > 0 && trimmed.Length <= 8 && trimmed.Contains(keyword);
        }

        private string ExtractCurrency(string text)
        {
            if (text.Contains("TND")) return "TND";
            if (text.Contains("DINAR")) return "TND";
            if (text.Contains("EUR")) return "EUR";
            if (text.Contains("USD")) return "USD";
            return "";
        }

        private string CleanWhitespace(string text) =>
            text.Replace('\u00A0', ' ')
                .Replace('\u202F', ' ')
                .Replace('\u2212', '-');

        private string NormalizeSignSpacing(string text) =>
            Regex.Replace(text, @"^(\s*-)\s+(?=\d)", "-");

        private string StripPrintArtifacts(string text)
        {
            var urlMatch = Regex.Match(text, @"https?://\S+", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
                text = text.Substring(0, urlMatch.Index);

            text = Regex.Replace(text, @"\bBTK@?DIRECT\b", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\b\d{1,2}/\d{1,2}/\d{2,4},?\s*\d{1,2}:\d{2}\s*(AM|PM)\b", "", RegexOptions.IgnoreCase);

            return CleanWhitespace(text).Trim();
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
                ("BH", "Banque de l'Habitat (BH)"),
                ("BTK", "Banque Tuniso-Koweitienne (BTK)")   // ← ajouter
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}