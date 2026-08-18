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


        private static readonly Regex ArabicScriptRegex = new("[؀-ۿ]");

        // [AMEN] "DU jj.mm.aaaa" est un echo (au format point) de la date d'operation qui peut
        // apparaitre au milieu d'une cellule de libelle (ex. "R394 DU 01.07.2026 VMA"). Ce
        // fragment correspond partiellement a AmountRegex (ex. "01.07" y est reconnu comme un
        // montant), ce qui cree de faux montants (voir "1.07", "2.07"...) et fait exclure a tort
        // la cellule entiere du libelle. La colonne OCR ou apparait cet echo est parfois coupee
        // en largeur, ce qui tronque le dernier chiffre de l'annee (ex. "DU 21.07.202" au lieu
        // de "DU 21.07.2026") : l'annee est donc acceptee de 2 a 4 chiffres. On neutralise ce
        // motif avant toute detection de montant/texte, uniquement pour AMEN BANK (voir
        // isAmenDocument) : les autres banques ne sont jamais concernees par ce nettoyage.
        private static readonly Regex AmenEchoDateRegex =
            new(@"\bDU\s+\d{2}\.\d{2}\.\d{2,4}\b", RegexOptions.IgnoreCase);

        private static string StripAmenEchoDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = AmenEchoDateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        // [BNA - format "RELEVE DE COMPTE"] La colonne "Valeur" de ce format s'imprime en
        // "jj mm aaaa" sans separateur (ex. "01 07 2026"), jamais utilisee comme date de
        // transaction (voir bnaReleveMonthYear/isBna dans Parse) mais qui, faute de correspondre
        // a AmountRegex ni aux motifs de date avec separateur deja nettoyes ailleurs, reste sinon
        // visible telle quelle dans le libelle.
        private static readonly Regex BnaReleveValeurDateRegex =
            new(@"\b\d{1,2}\s+\d{1,2}\s+\d{4}\b");

        private static string StripBnaReleveValeurDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaReleveValeurDateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        // [BNA] Sur une ligne "Intéréts créd/déb", le taux (ex. "TEG14.4060") est colle sans
        // espace au vrai montant qui suit (ex. "TEG14.4060 30 06 2026 2,354.301" - confirme sur
        // BNA.pdf). AmountRegex, applique cellule par cellule, matche alors "14.4060" comme si
        // c'etait un montant independant et IsMergedRow/SplitMergedRow scindent a tort la ligne
        // en 2 transactions (l'une avec ce faux montant). Le taux n'est jamais un montant de
        // mouvement : on le retire de la cellule avant toute detection de montant.
        private static readonly Regex BnaRateLabelRegex =
            new(@"\bTEG\s*\d+(?:[.,]\d+)?", RegexOptions.IgnoreCase);

        private static string StripBnaRateLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaRateLabelRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        // [BNA-RELEVE] Le pied de page publicitaire/legal ("BNA H24, votre banque 100% digitale
        // ..." puis le rappel legal "TOUTE ERREUR OU OMISSION EST A SIGNALER ...") se retrouve
        // colle a la derniere operation du releve dans la meme TableRow (regroupement par
        // proximite verticale). Coupe tout ce qui suit ces deux amorces reconnaissables, jamais
        // une operation.
        private static readonly Regex BnaFooterBoilerplateRegex =
            new(@"\bBNA\s+H24\b.*$|\bTOUTE\s+ERREUR\s+OU\s+OMISSION\b.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static string StripBnaFooterBoilerplate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string cleaned = BnaFooterBoilerplateRegex.Replace(text, "").Trim();
            return Regex.Replace(cleaned, @"\s{2,}", " ");
        }

        private List<TableRow> SplitDuplicatedRows(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Cells.Select(c => c.Text));
           
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
            bool isAbcBankDoc = fullText.Contains("Bank ABC", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("BANK ABC", StringComparison.OrdinalIgnoreCase);


            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);
            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);
            bool isWifak = fullText.Contains("WIFAK", StringComparison.OrdinalIgnoreCase);
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);
            bool isUbci = fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase);
            // [BTE] Même style de détection que pour les autres banques dédiées.
           
            // [BTE] Détection structurelle : le sigle seul (3 lettres) est ambigu avec BTK
            // en cas de confusion OCR — on exige donc soit le nom complet, soit le sigle
            // accompagné de la signature de colonnes propre au tableau BTE.
            bool bteNameHit = (Regex.IsMatch(fullText, @"\bBTE\b", RegexOptions.IgnoreCase)
                    && !fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase))
                || fullText.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase);

            // APRÈS
           
            int bteColumnHits =
                  (Regex.IsMatch(fullText, @"D\.?\s*Op[ée]\.?", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"(D\.?\s*Valeur|Date\s*de\s*Valeur)", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"\bD[ée]bit\b", RegexOptions.IgnoreCase) ? 1 : 0)
                + (Regex.IsMatch(fullText, @"\bCr[ée]dit\b", RegexOptions.IgnoreCase) ? 1 : 0);
            bool bteColumnSignature = bteColumnHits >= 3;

            bool bteDocumentTitleHit = Regex.IsMatch(fullText, @"Extrait\s+de\s+Compte", RegexOptions.IgnoreCase)
                || Regex.IsMatch(fullText, @"Relev[ée]\s+de\s+Compte", RegexOptions.IgnoreCase);

            // [BTE] Repli structurel : certains scans BTE n'ont aucun texte OCR-isable pour le nom
            // de la banque (en-tete/logo purement graphique, non reconnu par l'OCR) - bteNameHit
            // reste alors a False alors que la mise en page (titre "Extrait de compte"/"Relevé de
            // Compte" + les 4 en-tetes de colonnes D.Opé/D.Valeur/Débit/Crédit, tous presents) est
            // sans ambiguite celle de BTE. Ce repli n'est active que si AUCUNE autre banque nommee
            // n'a ete detectee dans le texte, pour ne jamais risquer de reclasser a tort un
            // document d'une autre banque. N'affecte que les documents qui, avant ce correctif,
            // tombaient dans le parseur generique faute de nom detecte (isBte=False) alors que la
            // structure BTE est complete : aucun document deja correctement classe (isBte=True) ou
            // appartenant a une autre banque nommee n'est concerne.
            bool bteStructuralFallback = !bteNameHit
                && bteColumnHits >= 4
                && bteDocumentTitleHit
                && !isBiat && !isZitouna && !isBtk && !isBna && !isWifak && !isAtb && !isUbci && !isAbcBankDoc
                && fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("STB", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("UIB", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("ATTIJARI", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("ALBARAKA", StringComparison.OrdinalIgnoreCase) < 0
                && fullText.IndexOf("albarakabank", StringComparison.OrdinalIgnoreCase) < 0;

            bool isBte = fullText.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || (bteNameHit && bteColumnSignature)
                || (bteNameHit && bteDocumentTitleHit && bteColumnHits >= 2)
                || bteStructuralFallback;

            bool bhSignalCompte = Regex.IsMatch(fullText, @"No\s*du\s*compte\s*[-:]", RegexOptions.IgnoreCase);
            bool bhSignalTitulaire = Regex.IsMatch(fullText, @"du\s*compte\s*:\s*\S", RegexOptions.IgnoreCase);
            bool bhSignalPeriode = Regex.IsMatch(fullText, @"Op[ée]rations\s+du\s+\d{2}/\d{2}/\d{4}\s+au\s+\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);
            int bhSignalCount = (bhSignalCompte ? 1 : 0) + (bhSignalTitulaire ? 1 : 0) + (bhSignalPeriode ? 1 : 0);

            bool isBh = fullText.Contains("bhbank", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("BH BANK", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Banque de l'Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("Habitat", StringComparison.OrdinalIgnoreCase)
                || bankName.Contains("(BH)", StringComparison.OrdinalIgnoreCase)
                || bhSignalCount >= 2;

            if (isBh)
            {
                var bhDocument = new BankDocument
                {
                    BankName = "Banque de l'Habitat (BH)",
                    Accounts = ExtractBhAccountSections(fullText)
                };
                return bhDocument;
            }
            // [UBCI] Detection + contexte pour les 3 regles UBCI (solde, dates deformees,
            if ((isBiat || isZitouna || isBtk || isBna || isBh || isWifak || isUbci || isBte) && !isAbcBankDoc) engine.VerticalTolerance = 1;

            var rows = engine.BuildTable(lines);
            rows = SplitDuplicatedRows(rows);
            rows = MergeContinuationLines(rows);   // ← LIGNE AJOUTÉE, une seule fois
            if (isUbci)
            {
                var ubciDocument = new BankDocument
                {
                    BankName = "Union Bancaire pour le Commerce et l'Industrie (UBCI)",
                    Accounts = ExtractUbciAccountSections(rows, fullText)
                };
                return ubciDocument;
            }
            if (isBte)
            {
                var bteDocument = new BankDocument
                {
                    BankName = "Banque de Tunisie et des Emirats (BTE)",
                    Accounts = ExtractBteAccountSections(rows, fullText)
                };
                return bteDocument;
            }

            var document = new BankDocument
            {
                BankName = bankName,
                Accounts = ExtractAccountSections(rows, fullText)
            };

            return document;
        }
        private enum LineCategory
        {
            TransactionStart, TransactionContinuation, TableHeader,
            AccountMetadata, Total, Balance, DocumentFooter, Noise
        }

        // Date "opération"/"valeur" : separateurs classiques (/ . -) ET espace, ce dernier
        // couvrant les tableaux ou la date est imprimee "jj mm aaaa" (ex. BTK/AMEN : "02 05
        // 2025"). Sans l'espace, une date de continuation comme celle-ci ne serait jamais vue
        // par ClassifyLine et la ligne porteuse serait, a tort, jugee "sans date".
        private static readonly Regex ClassifyDateAnywhereRegex =
            new(@"(?<!\d)\d{1,2}[\s/.\-]\d{1,2}[\s/.\-]\d{2,4}(?!\d)");

        private static readonly Regex ClassifyAmountAnywhereRegex =
            new(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}");

        // La colonne "operation" en tete de ligne n'est pas toujours une date complete : BIAT
        // imprime "03 01" (jour mois, sans annee), ATB un simple "03" (jour seul, la vraie date
        // est plus loin sur la meme ligne dans la colonne "Date Valeur"), BTE/QNB une date
        // complete. Dans tous les cas, une VRAIE ligne d'operation commence par un chiffre ; une
        // continuation/annotation (ex. "ECHEANCE 25/04/2025", "DONT TVA: 0,570") commence par un
        // mot. C'est ce signal positionnel simple - pas le format exact de la date - qui est
        // fiable a travers les banques.
        private static readonly Regex LeadingDigitRegex = new(@"^\d");

        private LineCategory ClassifyLine(TableRow row, bool hasOpenTransaction)
        {
            var orderedCells = row.Cells.OrderBy(c => c.Left).ToList();
            string joined = string.Join(" ", orderedCells.Select(c => c.Text)).Trim();
            if (string.IsNullOrWhiteSpace(joined)) return LineCategory.Noise;

            if (Regex.IsMatch(joined, @"(D[ée]bit.{0,20}Cr[ée]dit)|(Cr[ée]dit.{0,20}D[ée]bit)|D\.?\s*Op[ée]\.?\s+Libell[ée]|^Page\s*\d|^Folio\s*\d+\s*$|^\d+\s*/\s*\d+\s*$", RegexOptions.IgnoreCase))
                return LineCategory.TableHeader;

            if (Regex.IsMatch(joined, @"Solde\s*(au|initial|final|pr[ée]c[ée]dent|d['’]ouverture|de\s*cl[ôo]ture)", RegexOptions.IgnoreCase))
                return LineCategory.Balance;

            if (Regex.IsMatch(joined, @"Totaux?\b|Encours", RegexOptions.IgnoreCase))
                return LineCategory.Total;

            if (Regex.IsMatch(joined, @"sauf\s+erreur|d[ée]lai\s+de\s+r[ée]clamation|jours?\s+[àa]\s+compter|tacitement|nos\s+[ée]critures|en\s+cas\s+de\s+contestation|si[èe]ge\s+social", RegexOptions.IgnoreCase))
                return LineCategory.DocumentFooter;

            if (Regex.IsMatch(joined, @"Compte\s*N[°o]|^\s*RIB\b|IBAN\b|Adresse\s*:|Agence\s*:|Intitul[ée]\s*:|Devise\s*:|Ville\s*:|P[ée]riode\s*:|Titulaire", RegexOptions.IgnoreCase))
                return LineCategory.AccountMetadata;

            bool hasDate = ClassifyDateAnywhereRegex.IsMatch(joined);
            bool hasAmount = ClassifyAmountAnywhereRegex.IsMatch(joined);
            bool hasLeadingDigit = orderedCells.Count > 0 && LeadingDigitRegex.IsMatch(orderedCells[0].Text.TrimStart());

            // Une ligne qui porte a la fois une date (n'importe ou) ET un montant est toujours le
            // debut d'une operation autonome (ex. "TVA 010546128672 19/06/25 1,330"), meme au
            // milieu d'une transaction deja ouverte.
            if (hasDate && hasAmount)
                return LineCategory.TransactionStart;

            // Meme sans date reconnue (jour seul, jour+mois sans annee, date valeur glissee sans
            // separateur...), une ligne dont la premiere cellule commence par un chiffre ET qui
            // porte une date OU un montant est une vraie ligne de la colonne operation - jamais
            // une continuation, meme transaction deja ouverte. Une continuation/annotation
            // commence toujours par un mot (ECHEANCE, DONT, Folio...), jamais par un chiffre.
            if (hasLeadingDigit && (hasDate || hasAmount))
                return LineCategory.TransactionStart;

            // Pas de transaction ouverte : rien pour rattacher cette ligne a quoi que ce soit
            // d'anterieur. Une date seule (montant a suivre sur la ligne d'apres) ou un montant
            // seul (cas orphelin deja tolere) demarrent donc une nouvelle transaction.
            if (!hasOpenTransaction)
                return (hasDate || hasAmount) ? LineCategory.TransactionStart : LineCategory.Noise;

            // Une transaction est deja ouverte, et cette ligne ne commence ni par un chiffre ni
            // par une date+montant complets : c'est un detail rattache a l'operation en cours
            // (ex. "ECHEANCE 25/04/2025", "DONT TVA: 0,570"), jamais une nouvelle transaction.
            // Le texte est conserve (voir Transaction.Libelle), mais IsContinuationDetail (pose
            // par MergeContinuationLines) empeche tout extracteur de le relire comme
            // montant/date/reference de colonne.
            return LineCategory.TransactionContinuation;
        }
        private List<TableRow> MergeContinuationLines(List<TableRow> rows)
        {
            var result = new List<TableRow>();
            int? openTxIndex = null;

            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Cells.Select(c => c.Text)).Trim();
                var category = ClassifyLine(row, openTxIndex.HasValue);

                switch (category)
                {
                    case LineCategory.TransactionContinuation:
                        var lastCell = result[openTxIndex!.Value].Cells.LastOrDefault();
                        int left = lastCell != null ? lastCell.Left + 1 : 0;
                        
                        result[openTxIndex.Value].Cells.Add(new TableCell { Text = joined, Left = left, IsContinuationDetail = true });
                        break;

                    case LineCategory.TransactionStart:
                        result.Add(row);
                        openTxIndex = result.Count - 1;
                        break;

                    default: // TableHeader, AccountMetadata, Total, Balance, DocumentFooter, Noise
                        result.Add(row);
                        openTxIndex = null; // une transaction ne peut jamais enjamber ces catégories
                        break;
                }
            }
            return result;
        }
        private List<BankAccountSection> ExtractBteAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);


            var bteAmountRegex = new Regex(@"-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}");
            var dateRegex = new Regex(@"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$");

            // Année du document : période explicite en priorité, sinon 1ère date trouvée.
            int? documentYear = null;
            var btePeriodeMatch = Regex.Match(fullText,
         @"P[ée]riode\s*:?\s*(?:du\s*)?\d{2}[/.\-]\d{2}[/.\-](\d{4})|AU\s+\d{2}[/.\-]\d{2}[/.\-](\d{4})",
         RegexOptions.IgnoreCase);
            if (btePeriodeMatch.Success)
            {
                var yGroup = btePeriodeMatch.Groups[1].Success ? btePeriodeMatch.Groups[1] : btePeriodeMatch.Groups[2];
                if (int.TryParse(yGroup.Value, out int py)) documentYear = py;
            }
            if (!documentYear.HasValue)
            {
                var anyDateMatch = Regex.Match(fullText, @"\d{2}[/.\-]\d{2}[/.\-](\d{4})");
                if (anyDateMatch.Success && int.TryParse(anyDateMatch.Groups[1].Value, out int ay))
                    documentYear = ay;
            }

            // ── ÉTAPE 1 : ancrer les colonnes depuis la ligne d'en-tête ────────────
            // (répétée à chaque page — on garde la première détection, stable d'une
            // page à l'autre car c'est le même document / même mise en page).
            int? dateOpeAnchor = null, referenceAnchor = null, dateValeurAnchor = null,
                 debitAnchor = null, creditAnchor = null, soldeAnchor = null;

            foreach (var row in rows)
            {
                foreach (var cell in row.Cells.OrderBy(c => c.Left))
                {
                    string t = cell.Text.Trim();
                    if (!dateOpeAnchor.HasValue && Regex.IsMatch(t, @"^D\.?\s*Op[ée]\.?$", RegexOptions.IgnoreCase))
                        dateOpeAnchor = cell.Left;
                    else if (!referenceAnchor.HasValue && Regex.IsMatch(t, @"^R[ée]f[ée]rence$|^R[ée]f\.?$", RegexOptions.IgnoreCase))
                        referenceAnchor = cell.Left;
                    else if (!dateValeurAnchor.HasValue && Regex.IsMatch(t, @"^D\.?\s*Valeur$", RegexOptions.IgnoreCase))
                        dateValeurAnchor = cell.Left;
                    else if (!debitAnchor.HasValue && Regex.IsMatch(t, @"^D[ée]bit$", RegexOptions.IgnoreCase))
                        debitAnchor = cell.Left;
                    else if (!creditAnchor.HasValue && Regex.IsMatch(t, @"^Cr[ée]dit$", RegexOptions.IgnoreCase))
                        creditAnchor = cell.Left;
                    else if (!soldeAnchor.HasValue && Regex.IsMatch(t, @"^Solde$", RegexOptions.IgnoreCase))
                        soldeAnchor = cell.Left;
                }
                if (dateOpeAnchor.HasValue && debitAnchor.HasValue && creditAnchor.HasValue && soldeAnchor.HasValue)
                    break;
            }

            // Repli si l'en-tête n'a pas été détecté proprement (réutilise la méthode
            // générique déjà présente dans le fichier, sans la modifier).
            if (!debitAnchor.HasValue || !creditAnchor.HasValue)
            {
                var (inferredDebit, inferredCredit) = InferAnchorsFromAmountPositions(rows);
                if (inferredDebit.HasValue) debitAnchor = inferredDebit;
                if (inferredCredit.HasValue) creditAnchor = inferredCredit;
            }

            Console.WriteLine($"[BTE] Ancres : dateOpe={dateOpeAnchor} reference={referenceAnchor} " +
                               $"dateValeur={dateValeurAnchor} debit={debitAnchor} credit={creditAnchor} solde={soldeAnchor}");

            // ── ÉTAPE 2 : boucle principale ─────────────────────────────────────
            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = ExtractCurrency(fullText),
                SoldeInitial = null
            };

            decimal? previousSolde = null;
            string pendingDate = "";
            string pendingLibelle = "";
            string floatingLibelleBuffer = ""; // texte orphelin en attente d'être rattaché en amont ou en aval
            bool footerReached = false;

            bool IsHeaderOrMetadataNoise(string joined) => Regex.IsMatch(joined,
                @"D\.?\s*Op[ée]\.?\s+Libell[ée]|^Compte\s*N[°o]|^\s*RIB\b|Adresse\s*:|Intitul[ée]\s*:|" +
                @"Devise\s*:|Ville\s*:|P[ée]riode\s*:|Relev[ée]\s+de\s+Compte|Extrait\s+de\s+Compte|" +
                @"^Page\s*\d|^\d+\s*/\s*\d+\s*$",
                RegexOptions.IgnoreCase);

            // ── Détection de footer / fin de tableau (générique, pas un texte figé) ─
            bool LooksLikeFooter(string joined)
            {
                if (Regex.IsMatch(joined,
                    @"sauf\s+erreur\s+ou\s+omission|d[ée]lai\s+de\s+r[ée]clamation|jours?\s+[àa]\s+compter\s+de|" +
                    @"tacitement\s+accept[ée]|r[ée]clamation\s+(sera|doit)|tout\s+d[ée]bit\s+ponctuel|" +
                    @"dispositions?\s+l[ée]gales?|conform[ée]ment\s+aux?\s+articles?",
                    RegexOptions.IgnoreCase))
                    return true;

                // Heuristique structurelle : longue ligne de texte pur (pas de date, pas de
                // montant) qui arrive après qu'au moins une transaction a déjà été trouvée —
                // typique d'un paragraphe de mentions légales en bas de page/relevé.
                if (joined.Length >= 60
                    && !dateRegex.IsMatch(joined)
                    && !bteAmountRegex.IsMatch(joined)
                    && current.Transactions.Count > 0)
                    return true;

                return false;
            }
            var lineFallbackRegex = new Regex(
                @"^(\d{2}[/.\-]\d{2}[/.\-]\d{2,4})\s+(.+?)\s+(\d{2}[/.\-]\d{2}[/.\-]\d{2,4})\s+" +
                @"(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3})" +
                @"(?:\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{1,3}))?\s*(CR|DB)?\s*$",
                RegexOptions.IgnoreCase);

            foreach (var row in rows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                if (cells.Count == 0) continue;

                // "joined"/le regex de repli ne doivent jamais voir le texte des continuations
                // (ex. "DONT TVA: 0,570", "ECHEANCE 25/04/2025") : leurs propres nombres/dates
                // casseraient l'ancrage par colonne et le "$" du regex de repli plus bas. Le
                // texte est conservé séparément et rattaché au libellé final, jamais perdu.
                string joined = string.Join(" ", cells.Where(c => !c.IsContinuationDetail).Select(c => CleanWhitespace(c.Text)));
                string continuationText = Regex.Replace(
                    string.Join(" ", cells.Where(c => c.IsContinuationDetail).Select(c => CleanWhitespace(c.Text))),
                    @"\s{2,}", " ").Trim();
                if (string.IsNullOrWhiteSpace(joined) && string.IsNullOrWhiteSpace(continuationText)) continue;

                if (IsHeaderOrMetadataNoise(joined)) continue;
                if (!footerReached && LooksLikeFooter(joined))
                {
                    footerReached = true;
                }

                    // Solde d'ouverture (plusieurs libellés possibles selon le modèle du relevé).
                    // "SOLDE AU" tolère un ":" optionnel avant la date (ex. "Solde au : 31/01/2026").
                    var soldeInitMatch = Regex.Match(joined,
                    @"(SOLDE\s+PR[ée]C[ée]DENT|ANCIEN\s+SOLDE|SOLDE\s+D['’]OUVERTURE|SOLDE\s+INITIAL|SOLDE\s+AU\s*:?\s*\d{2}[/.\-]\d{2}[/.\-]\d{2,4})" +
                    @"\D*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeInitMatch.Success && current.Transactions.Count == 0 && !current.SoldeInitial.HasValue)
                {
                    decimal v = ParseAmount(soldeInitMatch.Groups[2].Value);
                    bool isDb = soldeInitMatch.Groups[3].Success && soldeInitMatch.Groups[3].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                    current.SoldeInitial = isDb ? -Math.Abs(v) : Math.Abs(v);
                    previousSolde = current.SoldeInitial;
                    continue;
                }

                // Totaux débit/crédit explicites
                var totalMatch = Regex.Match(joined,
                    @"TOTAUX?\s*:?\s*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (totalMatch.Success)
                {
                    current.TotalDebit = ParseAmount(totalMatch.Groups[1].Value);
                    current.TotalCredit = ParseAmount(totalMatch.Groups[2].Value);
                    continue;
                }

                // Solde de clôture
                var soldeFinalMatch = Regex.Match(joined,
                    @"(NOUVEAU\s+SOLDE|SOLDE\s+FINAL|SOLDE\s+DE\s+CLOTURE)\D*(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeFinalMatch.Success)
                {
                    decimal v = ParseAmount(soldeFinalMatch.Groups[2].Value);
                    bool isDb = soldeFinalMatch.Groups[3].Success && soldeFinalMatch.Groups[3].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                    current.SoldeFinal = isDb ? -Math.Abs(v) : Math.Abs(v);
                    continue;
                }
                if (footerReached) continue;
                // ── Classification des cellules de la ligne par ancre la plus proche ──
                TableCell? dateOpeCell = cells.Where(c => !c.IsContinuationDetail).FirstOrDefault(c =>
                    (!dateOpeAnchor.HasValue || c.Left <= dateOpeAnchor.Value + 40) &&
                    Regex.IsMatch(c.Text.Trim(), @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$"));

                decimal? debitVal = null, creditVal = null, soldeVal = null;
                string? soldeSign = null;
                var libelleParts = new List<string>();
                if (cells.Count > 1)
                    foreach (var cell in cells)
                {
                    if (dateOpeCell != null && cell.Left == dateOpeCell.Left && cell.Text == dateOpeCell.Text)
                        continue; // déjà consommée comme date d'opération

                    string txt = cell.Text.Trim();
                    if (string.IsNullOrEmpty(txt)) continue;

                    // Cellule de continuation (ECHEANCE, DONT TVA, remarque...) : jamais une
                    // colonne. Interdiction absolue de la faire passer par le classement par
                    // ancre (référence/débit/crédit/solde/CR-DB) ci-dessous, quel que soit son
                    // contenu apparent. Son texte est rattaché séparément via "continuationText"
                    // (voir plus bas) pour éviter de le dupliquer dans le libellé.
                    if (cell.IsContinuationDetail)
                        continue;

                    // Sens du solde : toujours et uniquement rattaché au solde, jamais au libellé.
                    if (Regex.IsMatch(txt, @"^(CR|DB)$", RegexOptions.IgnoreCase))
                    {
                        soldeSign = txt.ToUpperInvariant();
                        continue;
                    }

                    bool isAmount = AmountRegex.IsMatch(txt);
                    bool isBareDigits = Regex.IsMatch(txt, @"^\d{4,10}$");
                    bool isDateValeur = Regex.IsMatch(txt, @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$");

                    var distances = new List<(string label, int dist)>();
                    if (referenceAnchor.HasValue) distances.Add(("reference", Math.Abs(cell.Left - referenceAnchor.Value)));
                    if (dateValeurAnchor.HasValue) distances.Add(("datevaleur", Math.Abs(cell.Left - dateValeurAnchor.Value)));
                    if (debitAnchor.HasValue) distances.Add(("debit", Math.Abs(cell.Left - debitAnchor.Value)));
                    if (creditAnchor.HasValue) distances.Add(("credit", Math.Abs(cell.Left - creditAnchor.Value)));
                    if (soldeAnchor.HasValue) distances.Add(("solde", Math.Abs(cell.Left - soldeAnchor.Value)));

                    string nearest = distances.Count > 0 ? distances.OrderBy(d => d.dist).First().label : "libelle";

                    if (isDateValeur)
                        continue; // date valeur : informative seulement, jamais ajoutée au libellé

                    if (nearest == "reference" && (isBareDigits || isAmount))
                    {
                        // Référence bancaire pure : jamais un montant, jamais dans le libellé.
                        continue;
                    }
                    if (nearest == "debit" && isAmount)
                    {
                        debitVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }
                    if (nearest == "credit" && isAmount)
                    {
                        creditVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }
                    if (nearest == "solde" && isAmount)
                    {
                        soldeVal = ParseAmount(AmountRegex.Match(txt).Value);
                        continue;
                    }

                    // Tout le reste = texte du libellé (jamais interprété comme montant).
                    libelleParts.Add(txt);
                }
                

                string libelleText = Regex.Replace(string.Join(" ", libelleParts), @"\s{2,}", " ").Trim();
                bool hasAnyAmount = debitVal.HasValue || creditVal.HasValue || soldeVal.HasValue;

                if (dateOpeCell == null || !hasAnyAmount)
                {
                    var fm = lineFallbackRegex.Match(joined);
                    if (fm.Success)
                    {
                        dateOpeCell = new TableCell { Text = fm.Groups[1].Value, Left = cells.First(c => !c.IsContinuationDetail).Left };
                        string middle = fm.Groups[2].Value.Trim();
                        var trailingRef = Regex.Match(middle, @"\s(\d{4,13})$");
                        if (trailingRef.Success) middle = middle.Substring(0, trailingRef.Index).Trim();
                        libelleText = middle; decimal montant = ParseAmount(fm.Groups[4].Value);
                        string? sign = fm.Groups[6].Success ? fm.Groups[6].Value.ToUpperInvariant() : null;
                        decimal montantMouv = ParseAmount(fm.Groups[4].Value);
                        if (fm.Groups[5].Success)
                        {
                            decimal soldeLigne = ParseAmount(fm.Groups[5].Value);
                            bool isDb = fm.Groups[6].Success && fm.Groups[6].Value.Equals("DB", StringComparison.OrdinalIgnoreCase);
                            decimal soldeSigne = isDb ? -Math.Abs(soldeLigne) : Math.Abs(soldeLigne);

                            if (previousSolde.HasValue)
                            {
                                decimal diff = soldeSigne - previousSolde.Value;
                                if (diff < 0) debitVal = montantMouv; else if (diff > 0) creditVal = montantMouv;
                            }
                            previousSolde = soldeSigne;
                            hasAnyAmount = debitVal.HasValue || creditVal.HasValue;
                        }
                        else
                        {
                            libelleText = (libelleText + $" [MONTANT A CLASSER: {montantMouv} - a verifier manuellement]").Trim();
                            hasAnyAmount = false;
                        }
                        // APRÈS
                        if (hasAnyAmount)
                        {
                            // Débit/Crédit déjà déduits de façon fiable par la comparaison de solde
                            // ci-dessus : on ne les écrase plus par une heuristique positionnelle.
                        }
                        else if (sign == "CR" || sign == "DB")
                        {
                            soldeVal = montant;
                            soldeSign = sign;
                            debitVal = null; creditVal = null;
                            hasAnyAmount = soldeVal.HasValue;
                        }
                        else if (debitAnchor.HasValue && creditAnchor.HasValue)
                        {
                            var lastCell = cells.Where(c => !c.IsContinuationDetail).LastOrDefault(c => bteAmountRegex.IsMatch(c.Text));
                            if (lastCell != null)
                            {
                                int distDebit = Math.Abs(lastCell.Left - debitAnchor.Value);
                                int distCredit = Math.Abs(lastCell.Left - creditAnchor.Value);
                                if (distDebit <= distCredit) debitVal = montant; else creditVal = montant;
                                hasAnyAmount = true;
                            }
                            else
                            {
                                libelleText = (libelleText + $" [MONTANT A CLASSER: {montant.ToString(CultureInfo.InvariantCulture)} - a verifier manuellement]").Trim();
                                hasAnyAmount = false;
                            }
                        }
                        else
                        {
                            libelleText = (libelleText + $" [MONTANT A CLASSER: {montant.ToString(CultureInfo.InvariantCulture)} - a verifier manuellement]").Trim();
                            hasAnyAmount = false;
                        }
                    }
                }

                // Le regex de repli remplace entierement libelleText (fm.Groups[2]) : le texte
                // des cellules de continuation, deja ecarte de "joined" plus haut pour ne pas
                // casser ce regex, doit donc etre re-attache ici - jamais perdu.
                if (!string.IsNullOrEmpty(continuationText))
                    libelleText = string.IsNullOrEmpty(libelleText) ? continuationText : (libelleText + " " + continuationText).Trim();

                decimal? SignedSolde() => soldeVal.HasValue
                    ? (string.Equals(soldeSign, "DB", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(soldeVal.Value) : Math.Abs(soldeVal.Value))
                    : (decimal?)null;

                Transaction BuildTx(string date, string libelle)
                {
                    var tx = new Transaction { Date = date, Libelle = libelle };
                    if (debitVal.HasValue) tx.Debit = debitVal.Value;
                    if (creditVal.HasValue) tx.Credit = creditVal.Value;

                    var soldeSigned = SignedSolde();
                    // Filet de sécurité : si ni Débit ni Crédit n'ont pu être positionnés
                    // mais que le Solde a bougé, déduire le mouvement par différence.
                    if (!tx.Debit.HasValue && !tx.Credit.HasValue && previousSolde.HasValue && soldeSigned.HasValue)
                    {
                        decimal diff = soldeSigned.Value - previousSolde.Value;
                        if (diff < 0) tx.Debit = Math.Abs(diff);
                        else if (diff > 0) tx.Credit = diff;
                    }
                    if (soldeSigned.HasValue) previousSolde = soldeSigned;
                    return tx;
                }

                if (dateOpeCell != null)
                {
                    string date = NormalizeDate(dateOpeCell.Text.Trim(), documentYear);
                    if (string.IsNullOrEmpty(date)) continue;


                    // Nouvelle transaction : d'abord, clôturer proprement une transaction
                    // en attente (date sans montant sur la ligne précédente).
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                        pendingDate = ""; pendingLibelle = "";
                    }

                    string finalLibelle = string.IsNullOrEmpty(floatingLibelleBuffer)
              ? libelleText
              : (floatingLibelleBuffer + " " + libelleText).Trim();
                    floatingLibelleBuffer = "";


                    if (hasAnyAmount)
                    {
                        current.Transactions.Add(BuildTx(date, libelleText));
                    }
                    else
                    {
                        // Montant probablement sur la ligne suivante (retour à la ligne PDF) :
                        // on bufferise plutôt que d'inventer une transaction incomplète tout de suite.
                        pendingDate = date;
                        pendingLibelle = libelleText;
                    }
                    continue;
                }

                // Pas de date sur cette ligne : soit la fin d'une transaction en attente,
                // soit un simple texte de continuation.
                if (!string.IsNullOrEmpty(pendingDate))
                {
                    if (hasAnyAmount)
                    {
                        current.Transactions.Add(BuildTx(pendingDate, (pendingLibelle + " " + libelleText).Trim()));
                        pendingDate = ""; pendingLibelle = "";
                    }
                    else if (!string.IsNullOrEmpty(libelleText))
                    {
                        pendingLibelle = (pendingLibelle + " " + libelleText).Trim();
                    }
                    continue;
                }

                // Ni date en attente, ni nouvelle date : texte de continuation
                // (ex. "ENVOI RELEVE", "DEBITEUR/COP") rattaché à la dernière
                // transaction déjà enregistrée — jamais de transaction fantôme.
                if (!string.IsNullOrEmpty(libelleText) && current.Transactions.Count > 0)
                {
                    //var last = current.Transactions[current.Transactions.Count - 1];
                    floatingLibelleBuffer = (floatingLibelleBuffer + " " + libelleText).Trim();

                    // last.Libelle = (last.Libelle + " " + libelleText).Trim();
                }
            }

            if (!string.IsNullOrEmpty(pendingDate))
            {
                current.Transactions.Add(new Transaction
                {
                    Date = pendingDate,
                    Libelle = (pendingLibelle + " [ a verifier manuellement]").Trim()
                });
            }
            if (!string.IsNullOrEmpty(floatingLibelleBuffer) && current.Transactions.Count > 0)
            {
                var last = current.Transactions[current.Transactions.Count - 1];
                last.Libelle = (last.Libelle + " " + floatingLibelleBuffer).Trim();
            }

            current.RawSectionText = fullText;
            sections.Add(current);

            foreach (var sec in sections)
                ValidateBteAccounting(sec);

            return sections;
        }

        // [BTE] Validation comptable générique : solde_final = solde_initial + total_crédit
        // - total_débit, en tenant compte du signe CR/DB. Purement informatif (log), ne
        // modifie aucune donnée — n'importe quel autre parser peut s'en inspirer plus tard
        // sans que ça affecte BTE ni les autres banques aujourd'hui.
        private void ValidateBteAccounting(BankAccountSection section)
        {
            if (!section.SoldeInitial.HasValue || !section.SoldeFinal.HasValue)
            {
                Console.WriteLine("[BTE] Contrôle comptable ignoré : solde initial ou final manquant.");
                return;
            }

            decimal totalDebit = section.TotalDebit ?? section.Transactions.Sum(t => t.Debit ?? 0);
            decimal totalCredit = section.TotalCredit ?? section.Transactions.Sum(t => t.Credit ?? 0);

            decimal expectedFinal = section.SoldeInitial.Value + totalCredit - totalDebit;
            decimal diff = Math.Abs(expectedFinal - section.SoldeFinal.Value);

            if (diff > 0.005m)
            {
                Console.WriteLine(
                    $"[BTE-WARN] Contrôle comptable en échec : solde_initial({section.SoldeInitial}) + " +
                    $"crédit({totalCredit}) - débit({totalDebit}) = {expectedFinal} != solde_final({section.SoldeFinal}) " +
                    $"(écart={diff})");
            }
            else
            {
                Console.WriteLine($"[BTE-OK] Contrôle comptable validé (écart={diff}).");
            }
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

            bool biatInNoiseZone = false;
            int? debitAnchor = null, creditAnchor = null, soldeAnchor = null, montantAnchor = null, dateAnchor = null; string documentRib = ExtractRib(fullText);

           
            int? documentYear = null;
            bool isBiat = fullText.Contains("BIAT", StringComparison.OrdinalIgnoreCase);

            var dateMatch = Regex.Match(fullText, @"\b(\d{1,2})\s+(\d{1,2})\s+(\d{4})\b");
            if (dateMatch.Success && int.TryParse(dateMatch.Groups[3].Value, out int year))
                documentYear = year;
            else
            {
                var soldeMatch = Regex.Match(fullText, @"SOLDE\s+AU\s+\d{1,2}\s+\d{1,2}\s+(\d{4})", RegexOptions.IgnoreCase);
                if (soldeMatch.Success && int.TryParse(soldeMatch.Groups[1].Value, out year))
                    documentYear = year;
                else
                {
                    // Repli generique : n'importe quelle date "jj moisAbrege aa/aaaa" trouvee n'importe
                    // ou dans le document (en-tete "Edité le", "Imprimé le", "Solde départ au", ou
                    // simplement la premiere date rencontree dans le texte) donne l'annee du releve.
                    var frDateMatch = Regex.Match(fullText,
                        @"\d{1,2}\s+[A-Za-zÀ-ÿ]{3,6}\.?\s*(\d{2,4})",
                        RegexOptions.IgnoreCase);
                    if (frDateMatch.Success && int.TryParse(frDateMatch.Groups[1].Value, out year))
                        documentYear = year < 100 ? 2000 + year : year;
                }
            }


            // [QNB] Détection de la banque
            bool isQnb = fullText.Contains("QNB", StringComparison.OrdinalIgnoreCase);
            List<string> qnbAccountNumbers = new List<string>();
            int qnbAccountIndex = 0;
            if (isQnb)
            {
                qnbAccountNumbers = Regex.Matches(fullText, @"\b\d{4}-\d{6}-\d{3}\b")
                    .Select(m => m.Value)
                    .Distinct()
                    .ToList();
            }
            bool isAmenDocument = fullText.IndexOf("AMEN", StringComparison.OrdinalIgnoreCase) >= 0;
            // [AMEN] Détection du format avec code opération en première colonne (58, 53, 71...)
            bool isAmenWithOpCode = isAmenDocument && Regex.IsMatch(fullText, @"^\s*\d{2}\s+\d{2}/\d{2}/\d{4}", RegexOptions.Multiline);
            bool isBtk = fullText.Contains("BTK", StringComparison.OrdinalIgnoreCase);

            // [BNA] Chaque operation "principale" (Date+Libelle+Valeur+Montant+Solde) est suivie

            bool isBna = fullText.Contains("BNA", StringComparison.OrdinalIgnoreCase);
            // [BNA - format "RELEVE DE COMPTE"] Ce format (distinct de "EXTRAIT DU COMPTE", voir
            // isBna plus bas) n'a pas de colonne Solde par ligne : chaque ligne montre seulement
            // JOUR (jour d'operation seul, ex. "06") + Libelle + Valeur (date de valeur, qui peut
            // tomber sur un autre mois que le jour d'operation, ex. jour=06/07 mais valeur=30/06)
            // + Montant. Utiliser la date de Valeur comme date de transaction (comme le fait
            // GetDateFromAnyCell) est donc faux : la vraie date d'operation se reconstruit a
            // partir du jour (colonne JOUR) et du mois/annee du releve, imprime une seule fois en
            // entete ("DU MOIS DE JUILLET 2026"). Non calcule pour l'autre format BNA (colonne
            // Solde presente) qui n'en a pas besoin.
            (int Month, int Year)? bnaReleveMonthYear = null;
            if (isBna)
            {
                var bnaMoisMatch = Regex.Match(fullText, @"DU\s+MOIS\s+DE\s*:?\s*([A-Za-zéûÉÛ]+)\s+(\d{4})", RegexOptions.IgnoreCase);
                if (bnaMoisMatch.Success)
                {
                    string moisRaw = bnaMoisMatch.Groups[1].Value;
                    foreach (var kv in FrenchMonthsAbbrev)
                    {
                        if (moisRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            bnaReleveMonthYear = (int.Parse(kv.Value), int.Parse(bnaMoisMatch.Groups[2].Value));
                            break;
                        }
                    }
                }
            }
            bool isAlBarakaDoc = fullText.Contains("AlBaraka", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("albarakabank", StringComparison.OrdinalIgnoreCase);
            bool isAlBarakaExtrait = isAlBarakaDoc && Regex.IsMatch(fullText, @"Montant\s+cr[ée]diteur|Montant\s+d[ée]biteur", RegexOptions.IgnoreCase);
            bool isAlBarakaReleve = isAlBarakaDoc && !isAlBarakaExtrait;
            // [ATB] Meme detection que dans Parse() : la date n'est jamais dans les premieres
            // cellules, voir usage plus bas (GetDateFromAnyCell).
            bool isAtb = fullText.Contains("ATB", StringComparison.OrdinalIgnoreCase)
                || fullText.Contains("Arab Tunisian Bank", StringComparison.OrdinalIgnoreCase);

            // [BH] Meme detection que dans Parse() : format lineaire, une seule colonne montant.
            bool isBh = fullText.Contains("bhbank", StringComparison.OrdinalIgnoreCase)
                 || fullText.Contains("BH BANK", StringComparison.OrdinalIgnoreCase)
                 || fullText.Contains("Banque de l'Habitat", StringComparison.OrdinalIgnoreCase);
            
            bool isUbci = fullText.Contains("UBCI", StringComparison.OrdinalIgnoreCase);
            DateTime? ubciPeriodStart = null, ubciPeriodEnd = null;
            if (isUbci)
            {
                var ubciPeriodeMatch = Regex.Match(fullText, @"PERIODE\s+DU\s+(\d{2}/\d{2}/\d{4})\s+AU\s+(\d{2}/\d{2}/\d{4})", RegexOptions.IgnoreCase);
                if (ubciPeriodeMatch.Success)
                {
                    if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[1].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciPs))
                        ubciPeriodStart = ubciPs;
                    if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[2].Value, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciPe))
                        ubciPeriodEnd = ubciPe;
                }
                
            }
            DateTime? ubciLastConfirmedDate = null;
            bool ubciClotureReached = false;

          
            bool TryRepairUbciDate(string rawDigits, out string repaired)
            {
                repaired = "";
                if (rawDigits.Length != 9 && rawDigits.Length != 10) return false;
                if (!Regex.IsMatch(rawDigits, @"^\d+$")) return false;

                var candidates = new List<string>();
                if (rawDigits.Length == 9)
                {
                    candidates.Add(rawDigits.Remove(2, 1));
                    candidates.Add(rawDigits.Remove(4, 1));
                }
                else
                {
                    candidates.Add(rawDigits.Remove(5, 1).Remove(2, 1));
                }

                foreach (var candidate in candidates)
                {
                    if (candidate.Length != 8) continue;
                    if (!DateTime.TryParseExact(candidate, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        continue;
                    if (ubciPeriodStart.HasValue && parsed < ubciPeriodStart.Value.AddDays(-5)) continue;
                    if (ubciPeriodEnd.HasValue && parsed > ubciPeriodEnd.Value.AddDays(5)) continue;
                    if (ubciLastConfirmedDate.HasValue && Math.Abs((parsed - ubciLastConfirmedDate.Value).TotalDays) > 15) continue;

                    repaired = parsed.ToString("dd/MM/yyyy");
                    return true;
                }
                return false;
            }
            // Document utilise des montants signes (QNB, Zitouna, BIAT-extrait...) :
            // negatif = Debit, positif = Credit, strictement, partout dans ce document.
            // bool hasSignedAmounts = Regex.IsMatch(fullText, @"-\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3}");
            bool isAbcBank = fullText.Contains("Bank ABC", StringComparison.OrdinalIgnoreCase)
            || fullText.Contains("BANK ABC", StringComparison.OrdinalIgnoreCase);

            bool hasSignedAmountsHeader = Regex.IsMatch(fullText,
                @"\(-\)\s*D[ée]bit\s*/\s*Cr[ée]dit\s*\(\+\)|\(-\)\s*D[ée]bit.*Cr[ée]dit",
                RegexOptions.IgnoreCase);

            bool hasSignedAmounts = isAbcBank || hasSignedAmountsHeader || isAlBarakaReleve;
            // [GENERIQUE] Detection de bruit d'en-tete/pied de page par repetition, independante

            var repeatedLineCounts = new Dictionary<string, int>();
            foreach (var noiseRow in rows)
            {
                string noiseJoined = StripPrintArtifacts(string.Join(" ",
                    noiseRow.Cells.Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))));
               
                if (noiseJoined.Length < 40) continue;
                string noiseKey = Regex.Replace(noiseJoined, @"\d", "#").Trim();
                repeatedLineCounts[noiseKey] = repeatedLineCounts.GetValueOrDefault(noiseKey) + 1;
            }
            if (isBh)
            {
                var splitRows = new List<TableRow>();
                foreach (var row in rows)
                {
                    string rawJoined = string.Join(" ", row.Cells.Select(c => c.Text));
                    if (Regex.IsMatch(rawJoined, @"(?<=\d)\d{2}/\d{2}/\d{4}"))
                    {
                        string repaired = Regex.Replace(rawJoined, @"(?<=\d)(\d{2}/\d{2}/\d{4})", "\n$1");
                        foreach (var fragment in repaired.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var fakeRow = new TableRow();
                            fakeRow.Cells.Add(new TableCell { Text = fragment.Trim(), Left = row.Cells.FirstOrDefault()?.Left ?? 0 });
                            splitRows.Add(fakeRow);
                        }
                    }
                    else
                    {
                        splitRows.Add(row);
                    }
                }
                rows = splitRows;
            }

            bool skippingSummaryTable = Regex.IsMatch(fullText, @"Resum[ée]'?\s+du\s+compte", RegexOptions.IgnoreCase);
            foreach (var row in rows)
            {
                /* var cells = row.Cells.OrderBy(c => c.Left).ToList();
                 var cellTextsRaw = cells
                     .Select(c => NormalizeSignSpacing(CleanWhitespace(c.Text)))
                     .ToList();
                 var cellTexts = MergeLoneSignCells(cellTextsRaw);
                 if (cellTexts.Count == 0) continue;*/
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var cellTextsRaw = cells
                    .Select(c =>
                    {
                        string t = NormalizeSignSpacing(CleanWhitespace(c.Text));
                        if (isBiat) t = ConvertFrenchAbbrevDates(t);
                        return t;
                    })
                    .ToList();
                var cellTexts = MergeLoneSignCells(cellTextsRaw);

                string joined = string.Join(" ", cellTexts);
                joined = StripPrintArtifacts(joined);
                // [BNA-DEBUG] Trace ligne par ligne demandee pour verifier le parser BNA sans
                // toucher a l'OCR/DebugLines : positions de cellules, telles quelles avant tout
                // traitement de date/montant.
                if (isBna) Console.WriteLine("[BNA-DEBUG] Cellules: " + string.Join(" || ", cellTexts.Select((c, ci) => $"[{ci}]='{c}'")));
                if (string.IsNullOrWhiteSpace(joined)) continue;
                //if (isBiat) joined = ConvertFrenchAbbrevDates(joined);

                if (skippingSummaryTable)
                {
                    if (Regex.IsMatch(joined, @"Account\s*\(IBAN\)", RegexOptions.IgnoreCase))
                        skippingSummaryTable = false;
                    continue;
                }

                sectionRawText += joined + "\n";
                //update le 01/08/2026
                // ===== TRAITEMENT SPÉCIFIQUE QNB =====
                if (isQnb)
                {
                    Console.WriteLine($"[QNB-DEBUG] joined='{joined}' | current==null: {current == null}");
                    var qnbMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.*?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})$", RegexOptions.IgnoreCase);
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
                // ===== TRAITEMENT SPÉCIFIQUE BH (format linéaire, une seule colonne montant) =====
                if (isBh)
                {
                    
                    var bhMatch = Regex.Match(joined,
                        @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s*(-?\d[\d\s.,]*\d|\d)$");

                    if (bhMatch.Success)
                    {
                        string dateOp = bhMatch.Groups[1].Value;
                        string libelle = bhMatch.Groups[2].Value.Trim();
                        decimal montant = ParseAmount(bhMatch.Groups[4].Value);

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

                        var bhTx = new Transaction
                        {
                            Date = NormalizeDate(dateOp, null),
                            Libelle = libelle
                        };

                  
                        if (IsBhDebitLibelle(libelle))
                            bhTx.Debit = montant;
                        else
                            bhTx.Credit = montant;

                        current.Transactions.Add(bhTx);
                        continue;
                    }

                    
                }
                // ===== FIN TRAITEMENT BH =====
                // Extraction du numéro de compte
                var account = ExtractAccountNumber(joined);
                //if (string.IsNullOrWhiteSpace(account))
                // account = ExtractAccountNumber(fullText);//je doit supprimer cette lignes le 3 aout 
                if (!string.IsNullOrWhiteSpace(account))
                    lastSeenAccountNumber = account;

                // RIB
                var ribMatch = Regex.Match(joined, @"TN\d{2}[\s\d]{15,25}");
                if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();
               
                if (Regex.IsMatch(joined, @"Type\s*d['’]op[ée]ration|Montant\s*Min|Montant\s*Max|Date\s*D[ée]but|Date\s*Fin\b|Agence\s*:|Intitul[ée]\s*de\s*compte|Solde\s*actuel\s*:", RegexOptions.IgnoreCase))
                    continue;
                // [UBCI] Solde debiteur/crediteur d'ouverture : "SOLDE DEBITEUR AU <date>

                if (isUbci)
                {
                    var ubciOpenMatch = Regex.Match(joined,
                        @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                    if (current == null && ubciOpenMatch.Success)
                    {
                        decimal ubciSoldeInit = ParseAmount(ubciOpenMatch.Groups[2].Value);
                        // Le releve n'imprime pas toujours le signe "-" pour un solde debiteur
                        // (observe sur bq bidah 02-2025ubci.pdf) : le mot DEBITEUR fait foi.
                        ubciSoldeInit = ubciOpenMatch.Groups[1].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                            ? -Math.Abs(ubciSoldeInit)
                            : Math.Abs(ubciSoldeInit);

                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = ubciSoldeInit
                        };
                        previousSolde = ubciSoldeInit;
                    if (ribMatch.Success) lastSeenRib = Regex.Replace(ribMatch.Value, @"\s+", "").Trim();      sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        continue;
                    }

                    // [UBCI] Solde de cloture : "SOLDE DE CLOTURE <montant>". Marque aussi la fin
                    // des reparations UBCI (dates/montants) pour cette section : tout ce qui suit
                    // est du pied de page (mentions legales, coordonnees), jamais une operation.
                    var ubciCloseMatch = Regex.Match(joined, @"SOLDE\s+DE\s+CLOTURE\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (ubciCloseMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(ubciCloseMatch.Groups[1].Value);
                        ubciClotureReached = true;
                        continue;
                    }
                }

                // Détection des en-têtes Debit/Credit/Solde
                var debitCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"D[ée]bit", RegexOptions.IgnoreCase));
                var creditCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"Cr[ée]dit", RegexOptions.IgnoreCase));
                var soldeCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bSolde\b", RegexOptions.IgnoreCase));
                var montantCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"\bMontant\b", RegexOptions.IgnoreCase));
                var dateCell = cells.FirstOrDefault(c => Regex.IsMatch(c.Text, @"^Date$|Date\s*op[ée]ration", RegexOptions.IgnoreCase));
                if (isBiat)
                {
                    
                    var arabicDebit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "عليه"));
                    var arabicCredit = cells.FirstOrDefault(c => IsBiatArabicHeaderCell(c.Text, "له"));
                    if (arabicDebit != null) debitCell = arabicDebit;
                    if (arabicCredit != null) creditCell = arabicCredit;
                }

               
                bool biatLooksLikeTransactionRow = isBiat && (debitCell != null || creditCell != null) &&
                    (!string.IsNullOrEmpty(GetNormalizedDateFromCells(cellTexts, documentYear)) || cells.Any(c => AmountRegex.IsMatch(c.Text)));

                if ((debitCell != null || creditCell != null) && !biatLooksLikeTransactionRow)
                {
                    if (debitCell != null) debitAnchor = debitCell.Left;
                    if (creditCell != null) creditAnchor = creditCell.Left;
                    if (soldeCell != null) soldeAnchor = soldeCell.Left;
                    if (dateCell != null) dateAnchor = dateCell.Left;
                    if (montantCell != null) montantAnchor = montantCell.Left;

                    if (debitAnchor.HasValue && creditAnchor.HasValue && Math.Abs(debitAnchor.Value - creditAnchor.Value) < 40)
                    {
                        var (inferredDebit, inferredCredit) = InferAnchorsFromAmountPositions(rows);
                        if (inferredDebit.HasValue && inferredCredit.HasValue)
                        {
                            debitAnchor = inferredDebit;
                            creditAnchor = inferredCredit;
                        }
                    }
                    continue;
                }

                // [ATTIJARI] Solde (TND) au ...
                var soldeAuFinMatch = Regex.Match(joined, @"Solde\s*\(\w+\)\s*au\s*\d{2}/\d{2}/\d{4}\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (soldeAuFinMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(soldeAuFinMatch.Groups[1].Value);
                    continue;
                }
                // [GÉNÉRIQUE] "Solde au : JJ/MM/AAAA <montant> [CR|DB]" — sert d'ouverture (current==null)
                // ou de clôture (section déjà ouverte). Le suffixe DB indique un solde débiteur → négatif.
                var soldeAuGenericMatch = Regex.Match(joined,
                    @"Solde\s+au\s*:?\s*\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s*(CR|DB)?",
                    RegexOptions.IgnoreCase);
                if (soldeAuGenericMatch.Success)
                {
                    decimal soldeAuVal = ParseAmount(soldeAuGenericMatch.Groups[1].Value);
                    soldeAuVal = soldeAuGenericMatch.Groups[2].Success
                        && soldeAuGenericMatch.Groups[2].Value.Equals("DB", StringComparison.OrdinalIgnoreCase)
                        ? -Math.Abs(soldeAuVal)
                        : Math.Abs(soldeAuVal);

                    if (current == null)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = soldeAuVal
                        };
                        previousSolde = soldeAuVal;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                    }
                    else
                    {
                        current.SoldeFinal = soldeAuVal;
                    }
                    continue;
                }

                // [QNB - AMÉLIORÉ] "Solde Initial" avec montant
                var soldeInitMatch = Regex.Match(joined, @"\bSolde\s+Initial\s*:?\s*([-+]?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase); if (soldeInitMatch.Success || Regex.IsMatch(joined, @"\bSolde\s+Initial\b", RegexOptions.IgnoreCase))
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

                    string resolvedAccountNumber = lastSeenAccountNumber;
                    if (isQnb && qnbAccountIndex < qnbAccountNumbers.Count)
                    {
                        resolvedAccountNumber = qnbAccountNumbers[qnbAccountIndex];
                        qnbAccountIndex++;
                    }

                    current = new BankAccountSection
                    {
                        AccountNumber = resolvedAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = soldeInit
                    };
                    previousSolde = soldeInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }
                // [ABC / FORMAT SIGNÉ] "Solde de départ <montant>" sans date
                var abcSoldeDepartMatch = Regex.Match(joined,
                    @"^Solde\s+de\s+d[ée]part\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})$",
                    RegexOptions.IgnoreCase);
                if (!abcSoldeDepartMatch.Success)
                {
                    // essai sans ancrage strict (cas où la cellule contient du texte autour)
                    abcSoldeDepartMatch = Regex.Match(joined,
                        @"Solde\s+de\s+d[ée]part\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                        RegexOptions.IgnoreCase);
                }
                if (abcSoldeDepartMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";
                    decimal abcDepartInit = ParseAmount(abcSoldeDepartMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = abcDepartInit
                    };
                    previousSolde = abcDepartInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }
                // [BIAT forme 2] "Solde départ au JJ/MM/AAAA <montant>" (dates déjà converties
                // par ConvertFrenchAbbrevDates plus haut).
                var biatSoldeDepartMatch = Regex.Match(joined, @"Solde\s+d[ée]part\s+au\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeDepartMatch.Success)
                {
                    if (current != null)
                    {
                        current.RawSectionText = sectionRawText;
                        sections.Add(current);
                    }
                    sectionRawText = joined + "\n";

                    decimal biatDepartInit = ParseAmount(biatSoldeDepartMatch.Groups[1].Value);
                    current = new BankAccountSection
                    {
                        AccountNumber = lastSeenAccountNumber,
                        Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                        Currency = ExtractCurrency(fullText),
                        SoldeInitial = biatDepartInit
                    };
                    previousSolde = biatDepartInit;
                    pendingLibelleBuffer = "";
                    pendingDate = "";
                    continue;
                }

                // [BIAT forme 2] "Solde fin au JJ/MM/AAAA <montant>"
                var biatSoldeFinAuMatch = Regex.Match(joined, @"Solde\s+fin\s+au\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                if (biatSoldeFinAuMatch.Success)
                {
                    if (current != null) current.SoldeFinal = ParseAmount(biatSoldeFinAuMatch.Groups[1].Value);
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

             
                if (current == null
                    && Regex.IsMatch(joined, @"\bSOLDE\b", RegexOptions.IgnoreCase)
                    && Regex.IsMatch(joined, @"\d{1,2}[/\-. ]\d{1,2}[/\-. ]\d{2,4}|\d{1,2}\s+[A-Za-z]{3,4}\s+\d{2,4}")
                    && Regex.IsMatch(joined, @"\b(SOLDE|BALANCE|REPORT|ANCIEN)\b", RegexOptions.IgnoreCase))
                {
                    var genericSoldeAmounts = AmountRegex.Matches(joined);
                    if (genericSoldeAmounts.Count == 1)
                    {
                        decimal genericSoldeInit = ParseAmount(genericSoldeAmounts[0].Value);
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = genericSoldeInit
                        };
                        previousSolde = genericSoldeInit;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        continue;
                    }
                }
                // [GÉNÉRIQUE] "Total des mouvements : <débit> <crédit>"
                var totalMouvementsMatch = Regex.Match(joined,
                    @"Total\s+des\s+mouvements\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (totalMouvementsMatch.Success)
                {
                    if (current != null)
                    {
                        current.TotalDebit = ParseAmount(totalMouvementsMatch.Groups[1].Value);
                        current.TotalCredit = ParseAmount(totalMouvementsMatch.Groups[2].Value);
                    }
                    continue;
                }
                // [ALBARAKA] Solde de clôture : "Solde fin période : x" (extrait) ou
                // "SOLDE CREDITEUR/DEBITEUR : x" (relevé), toujours en dernière ligne.
                var albarakaSoldeFinMatch = Regex.Match(joined,
                    @"Solde\s+fin\s+p[ée]riode\s*:?\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})|" +
                    @"SOLDE\s+(CREDITEUR|DEBITEUR)\s*:\s*(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (albarakaSoldeFinMatch.Success)
                {
                    if (current != null)
                    {
                        if (albarakaSoldeFinMatch.Groups[1].Success)
                            current.SoldeFinal = ParseAmount(albarakaSoldeFinMatch.Groups[1].Value);
                        else
                        {
                            decimal v = ParseAmount(albarakaSoldeFinMatch.Groups[3].Value);
                            current.SoldeFinal = albarakaSoldeFinMatch.Groups[2].Value.Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                                ? -Math.Abs(v) : Math.Abs(v);
                        }
                    }
                    continue;
                }
                // [BNA-RELEVE] "SOLDE DEPART <montant>" : ouverture de section (solde initial,
                // reellement imprime sur le releve, pas invente). Toujours en amont de la
                // premiere operation, current est donc encore null a ce stade.
                if (isBna && current == null)
                {
                    var bnaSoldeDepartMatch = Regex.Match(joined, @"SOLDE\s+DEPART\s+(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})", RegexOptions.IgnoreCase);
                    if (bnaSoldeDepartMatch.Success)
                    {
                        current = new BankAccountSection
                        {
                            AccountNumber = lastSeenAccountNumber,
                            Rib = string.IsNullOrWhiteSpace(lastSeenRib) ? documentRib : lastSeenRib,
                            Currency = ExtractCurrency(fullText),
                            SoldeInitial = ParseAmount(bnaSoldeDepartMatch.Groups[1].Value)
                        };
                        previousSolde = current.SoldeInitial;
                        sectionRawText = joined + "\n";
                        pendingLibelleBuffer = "";
                        pendingDate = "";
                        Console.WriteLine($"[BNA-DEBUG] Ligne ignoree (SOLDE DEPART -> ouverture de section) : SoldeInitial={current.SoldeInitial}");
                        continue;
                    }
                }
                // [BNA-RELEVE] "SOLDE AU jj mm aaaa ... <montant>" (solde de cloture) et
                // "TOTAUX DES MOUVEMENTS <debit> <credit>" : jamais des operations, seulement le
                // recapitulatif de fin de releve - a exclure avant toute detection de montant
                // pour ne pas devenir de fausses transactions (dernier montant de la ligne =
                // solde de cloture, reellement imprime).
                if (isBna)
                {
                    var bnaSoldeAuMatch = Regex.Match(joined, @"\bSOLDE\s+AU\b.*?(-?\d{1,3}(?:[ .,]\d{3})*[.,]\d{2,3})\s*$", RegexOptions.IgnoreCase);
                    if (bnaSoldeAuMatch.Success)
                    {
                        if (current != null) current.SoldeFinal = ParseAmount(bnaSoldeAuMatch.Groups[1].Value);
                        Console.WriteLine($"[BNA-DEBUG] Ligne ignoree (SOLDE AU -> cloture de section) : SoldeFinal={current?.SoldeFinal}");
                        continue;
                    }
                    if (Regex.IsMatch(joined, @"\bTOTAUX\s+DES\s+MOUVEMENTS\b", RegexOptions.IgnoreCase))
                    {
                        Console.WriteLine("[BNA-DEBUG] Ligne ignoree (TOTAUX DES MOUVEMENTS -> recapitulatif, pas une operation)");
                        continue;
                    }
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
               
                if (isBtk && Regex.IsMatch(joined, @"Titulaire\s+du\s+compte|Valeur\s+Libell[ée]|Report\s+du\s+\d{2}[/\-.]\d{2}[/\-.]\d{2,4}|Page\s+sur\s+\d+", RegexOptions.IgnoreCase))
                    continue;

                // Normalisation de la date
                string normalizedDate = GetNormalizedDateFromCells(cellTexts, documentYear);
                // [AMEN avec code opération] La première cellule est un code (58, 53, 71...)
                // → la date est dans la deuxième cellule
                if (string.IsNullOrEmpty(normalizedDate) && isAmenWithOpCode && cellTexts.Count >= 2)
                {
                    if (Regex.IsMatch(cellTexts[0].Trim(), @"^\d{2}$"))
                    {
                        normalizedDate = NormalizeDate(cellTexts[1].Trim(), documentYear);
                    }
                }
                // [BNA] Reconstruction de la date, differente selon le sous-format detecte via
                // soldeAnchor (colonne "Solde" presente = "EXTRAIT DU COMPTE" ; absente =
                // "RELEVE DE COMPTE", voir bnaReleveMonthYear plus haut).
                if (isBna && string.IsNullOrEmpty(normalizedDate))
                {
                    if (!soldeAnchor.HasValue)
                    {
                        // [BNA-RELEVE] Pas de colonne Solde : reconstruire JOUR + mois/annee du
                        // releve (jamais la colonne Valeur, qui peut appartenir a un autre mois -
                        // voir bnaReleveMonthYear). cellTexts[0] est le jour seul (ex. "06") sur
                        // toute ligne de mouvement, y compris les lignes de commission/TVA qui
                        // repetent elles aussi leur propre jour.
                        if (bnaReleveMonthYear.HasValue
                            && Regex.IsMatch(cellTexts.Count > 0 ? cellTexts[0].Trim() : "", @"^\d{1,2}$")
                            && int.TryParse(cellTexts[0].Trim(), out int bnaJour) && bnaJour >= 1 && bnaJour <= 31)
                        {
                            normalizedDate = $"{bnaJour:D2}/{bnaReleveMonthYear.Value.Month:D2}/{bnaReleveMonthYear.Value.Year}";
                        }
                    }
                    else if (AmountRegex.Matches(joined).Count >= 2)
                    {
                        // [BNA-EXTRAIT] Une vraie ligne d'operation affiche Montant ET Solde (2
                        // montants sur la ligne). Une sous-ligne de detail (Com..., TVA...)
                        // n'affiche que son propre montant sans Solde (1 seul montant) : la seule
                        // date qu'on y trouve est alors la date de Valeur (echo), pas une date
                        // d'operation - GetDateFromAnyCell ne doit donc s'appliquer que si 2
                        // montants sont presents, sinon la sous-ligne reste sans date et se
                        // rattache (comme texte de continuation) a l'operation precedente au lieu
                        // de devenir une fausse transaction isolee et mal datee. Variante tolerante
                        // (GetDateFromAnyCellLenient) : la cellule contenant la date d'operation
                        // porte parfois un glyphe OCR parasite colle devant (ex. "? 04/07/2025"),
                        // ce qui la fait echouer sur une correspondance exacte et glisser a tort
                        // vers la date de Valeur suivante.
                        normalizedDate = GetDateFromAnyCellLenient(cellTexts, documentYear);
                    }
                }
                // [ATB] Même raisonnement que BNA ci-dessus : la date n'est jamais dans les 3
                
                if (isAtb && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, null);
                }
                // [GÉNÉRIQUE] Filet de sécurité pour toute banque inconnue : si aucune date
                // n'a été trouvée dans les 3 premières cellules, chercher dans toutes les cellules
                // BNA exclu : deja traite ci-dessus avec sa propre logique (delibere de laisser
                // normalizedDate vide pour une sous-ligne EXTRAIT sans Solde) - ce filet generique
                // appliquerait sinon GetDateFromAnyCell sans la garde "2 montants" et annulerait
                // le correctif.
                if (!isBna && string.IsNullOrEmpty(normalizedDate))
                {
                    normalizedDate = GetDateFromAnyCell(cellTexts, documentYear);
                }
                // [UBCI] Filet de secours : uniquement si le chemin normal (inchangé) n'a rien

                if (isUbci && string.IsNullOrEmpty(normalizedDate) && !ubciClotureReached
                    && cellTexts.Count > 0 && Regex.IsMatch(cellTexts[0], @"^\d{9,10}$"))
                {
                    if (TryRepairUbciDate(cellTexts[0], out var ubciRepairedDate))
                        normalizedDate = ubciRepairedDate;
                }

                if (isUbci && !string.IsNullOrEmpty(normalizedDate)
                    && DateTime.TryParseExact(normalizedDate, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ubciConfirmedDate))
                {
                    ubciLastConfirmedDate = ubciConfirmedDate;
                }

                if (isBna)
                    Console.WriteLine($"[BNA-DEBUG] Date detectee='{(string.IsNullOrEmpty(normalizedDate) ? "(aucune)" : normalizedDate)}' | pendingDate='{pendingDate}' | joined='{joined}'");

                // Fusion des signes "-" isolés
                var mergedWithPos = new List<(int Left, string Text, bool IsContinuationDetail)>();
                for (int i = 0; i < cells.Count; i++)
                {
                    string cellRawText = isAmenDocument ? StripAmenEchoDate(cells[i].Text) : cells[i].Text;
                    if (isBna) cellRawText = StripBnaRateLabel(cellRawText);
                    mergedWithPos.Add((cells[i].Left, NormalizeSignSpacing(CleanWhitespace(cellRawText)), cells[i].IsContinuationDetail));
                }
                for (int i = 0; i < mergedWithPos.Count - 1; i++)
                {
                    if (mergedWithPos[i].Text.Trim() == "-")
                    {
                        mergedWithPos[i + 1] = (mergedWithPos[i + 1].Left, "-" + mergedWithPos[i + 1].Text.TrimStart(), mergedWithPos[i + 1].IsContinuationDetail);
                        mergedWithPos.RemoveAt(i);
                        i--;
                    }
                }

                // Une cellule de continuation (ex. "DONT TVA: 0,570", "ECHEANCE 25/04/2025")
                // reste dans cellTexts/joined pour le libellé, mais ne doit jamais fournir de
                // montant : elle est exclue ici de la détection debit/credit/solde, quelle que
                // soit la banque.
                var amountCandidates = mergedWithPos
                    .Where(c => !c.IsContinuationDetail && AmountRegex.IsMatch(c.Text))
                    .Select(c => new { Left = c.Left, Value = ParseAmount(AmountRegex.Match(c.Text).Value) })
                    .ToList();

                // AJOUT : élimine les doublons exacts (même valeur, positions très proches)
                
                // causés par une fusion accidentelle de 2 lignes PDF en une seule row.
               amountCandidates = amountCandidates
                    .GroupBy(c => new { c.Value, ZoneLeft = c.Left / 20 }) // regroupe par valeur + zone de 20px
                    .Select(g => g.First())
                    .ToList();
                //update le 3 aout 
                
                if (debitAnchor.HasValue || creditAnchor.HasValue || soldeAnchor.HasValue)
                {
                    // [UBCI] Meme mecanisme que ci-dessus (montant sans separateur), etendu aux
                    
                    bool shortAmountsAllowed = ((isUbci && !ubciClotureReached) || isBna) && Regex.IsMatch(joined, @"[A-Za-zÀ-ÿ]");
                    string bareDigitPattern = shortAmountsAllowed ? @"^-?\d{3,10}$" : @"^-?\d{5,10}$";
                    var bareDigitCandidates = mergedWithPos
                        .Where(c => !c.IsContinuationDetail)
                        .Where(c => !AmountRegex.IsMatch(c.Text))
                        .Where(c => Regex.IsMatch(c.Text.Trim(), bareDigitPattern))
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
                            // Partie entiere vide (ex. "555" -> decimales seules) : equivaut a 0.
                         
                            // (toujours >=2 chiffres de partie entiere) - purement defensif ici.
                            string intPart = digits.Length > 3 ? digits.Substring(0, digits.Length - 3) : "0";
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

               
                string repeatedNoiseKey = Regex.Replace(joined, @"\d", "#").Trim();
                bool isRepeatedBoilerplate = amountCandidates.Count == 0
                    && joined.Length >= 40
                    && repeatedLineCounts.GetValueOrDefault(repeatedNoiseKey) >= 3;

                // [BNA] "lSolde ... a ce jour sauf erreur ou omission ..." est le rappel de solde
                // (format EXTRAIT) ; "BNA H24, votre banque 100% digitale ..." et "TOUTE ERREUR
                // OU OMISSION EST A SIGNALER ..." sont le pied de page publicitaire/legal du
                // format RELEVE, colle en continuation apres la derniere operation.
                bool bnaNoiseHit = isBna && Regex.IsMatch(joined, @"ce\s*jour\s*sauf\s*erreur\s*ou\s*omission|\bBNA\s+H24\b|TOUTE\s+ERREUR\s+OU\s+OMISSION|EST\s+A\s+SIGNALER", RegexOptions.IgnoreCase);
                // [BNA-RELEVE] Le pied de page publicitaire/legal s'etale sur plusieurs lignes de
                // continuation consecutives (ex. "BNA H24..." puis "vos cartes et..." puis "que
                // vous soyez..." puis le rappel legal) : une seule ligne porte le mot-cle detecte
                // par bnaNoiseHit, les autres non. Reutilise le meme mecanisme de "zone de bruit"
                // que BIAT (biatInNoiseZone) pour que TOUTES les lignes de continuation qui
                // suivent restent exclues jusqu'a la prochaine operation datee.
                if (bnaNoiseHit) biatInNoiseZone = true;

                bool isNoise = Regex.IsMatch(joined, @"\b(Total|Page\s*\d|Solde\s*(Initial|Final)|[ée]v[èe]nements?|\(\*\)|Solde\s*\(\w+\)\s*au|BTK@?DIRECT|https?://\S+)", RegexOptions.IgnoreCase)
                     || biatNoiseHit
                     || bnaNoiseHit
                     || isRepeatedBoilerplate
                     || (isQnb && Regex.IsMatch(joined, @"Cette\s+d[ée]claration\s+sera\s+consid[ée]r[ée]e|dans\s+votre\s+situation\s+de\s+compte|Pour\s+toute\s+r[ée]clamation", RegexOptions.IgnoreCase));
                // Si la date est vide, tenter de la trouver dans le libellé (date 8 chiffres)
                if (string.IsNullOrEmpty(normalizedDate) && amountCandidates.Count > 0)
                {
                    var dateInLibelle = Regex.Match(joined, @"\b(\d{8})\b");
                    if (dateInLibelle.Success)
                        normalizedDate = NormalizeDate(dateInLibelle.Groups[1].Value, documentYear);
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

                            if (current.Transactions.Count > 0 && !isNoise)

                                pendingDate = current.Transactions[current.Transactions.Count - 1].Date;
                            else
                                continue;
                        }

                        string fullDesc = pendingLibelleBuffer.Trim();

                       
                        if (isAmenDocument || isBtk || isUbci || isBna)
                        {
                            string currentNonAmount = string.Join(" ", cellTexts.Where(c => !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c))).Trim();
                            if (isAmenDocument) currentNonAmount = StripAmenEchoDate(currentNonAmount);
                            if (isBna && !soldeAnchor.HasValue) currentNonAmount = StripBnaFooterBoilerplate(StripBnaReleveValeurDate(currentNonAmount));
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                            currentNonAmount = Regex.Replace(currentNonAmount, @"\b\d{8}\b", "").Trim();
                            if (!string.IsNullOrEmpty(currentNonAmount))
                                fullDesc = (fullDesc + " " + currentNonAmount).Trim();
                        }
                        pendingLibelleBuffer = "";

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
                        AssignAmounts(tx2, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out var soldeCourantTX);
                        ApplyMovementFallback(tx2, soldeAvant2, soldeCourantTX);
                        //if (isQnb) ApplyQnbSignRule(tx2);   // <-- AJOUTE CETTE LIGNE
                        current.Transactions.Add(tx2);
                        if (isBna) Console.WriteLine($"[BNA-DEBUG] Transaction creee (ligne sans date propre) : Date='{tx2.Date}' Libelle='{tx2.Libelle}' Debit={tx2.Debit} Credit={tx2.Credit}");
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

             
                if (amountCandidates.Count == 0)
                {
                   
                    if (cellTexts.Count == 1 && current.Transactions.Count > 0
                        && string.IsNullOrEmpty(pendingDate) && string.IsNullOrEmpty(pendingLibelleBuffer))
                    {
                        var lastTx = current.Transactions[current.Transactions.Count - 1];
                        if (!isNoise && !biatInNoiseZone)
                            lastTx.Libelle = (lastTx.Libelle + " " + joined.Trim()).Trim();
                    }
                    else
                    {
                       
                        if (!string.IsNullOrEmpty(pendingDate) && pendingDate != normalizedDate)
                        {
                            var orphanTx = new Transaction
                            {
                                Date = pendingDate,
                                Libelle = (pendingLibelleBuffer.Trim() + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                            };
                            current.Transactions.Add(orphanTx);
                            pendingLibelleBuffer = "";
                        }

                        pendingDate = normalizedDate;
                        string textOnly = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c)));
                        if (isAmenDocument) textOnly = StripAmenEchoDate(textOnly);
                        pendingLibelleBuffer = (pendingLibelleBuffer + " " + textOnly).Trim();
                    }
                    continue;
                }


                if (current != null)
            {
                    // [AMEN avec code opération] Sauter aussi la cellule du code opération (index 0)
                    int skipCount = (isAmenWithOpCode && cellTexts.Count >= 2
                        && Regex.IsMatch(cellTexts[0].Trim(), @"^\d{2}$")) ? 2 : 1;
                    // Cas normal : date + montant(s)
                    string description = string.Join(" ", cellTexts.Skip(1).Where(c => !AmountRegex.IsMatch(isAmenDocument ? StripAmenEchoDate(c) : c)))
                    .Trim(' ', '|', '[', ']', '-', '_');
                if (isAmenDocument) description = StripAmenEchoDate(description);
                if (isBna && !soldeAnchor.HasValue) description = StripBnaFooterBoilerplate(StripBnaReleveValeurDate(description));
                description = Regex.Replace(description, @"\b\d{2}[/\-.]\d{2}[/\-.]\d{4}\b", "").Trim();
                description = Regex.Replace(description, @"\b\d{8}\b", "").Trim();
                description = Regex.Replace(description, @"\s{2,}", " ").Trim();

                // [QNB] Ne pas garder les footers dans la description
                if (isQnb && Regex.IsMatch(description, @"support|hotline|N\.B|Pour toute|Cette déclaration", RegexOptions.IgnoreCase))
                    continue;

                string fullDescription = (pendingLibelleBuffer + " " + description).Trim();
                pendingLibelleBuffer = "";
                pendingDate = "";


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
                AssignAmounts(tx, amountCandidates, debitAnchor, creditAnchor, soldeAnchor, montantAnchor, isBtk, ref previousSolde, hasSignedAmounts, out  var soldeCourantTx);
                ApplyMovementFallback(tx, soldeAvantTx, soldeCourantTx);
                //if (isQnb) ApplyQnbSignRule(tx);   // <-- AJOUTE CETTE LIGNE
                current.Transactions.Add(tx);
                if (isBna) Console.WriteLine($"[BNA-DEBUG] Transaction creee : Date='{tx.Date}' Libelle='{tx.Libelle}' Debit={tx.Debit} Credit={tx.Credit}");
                biatInNoiseZone = false;
            }
            }


            if (current != null && !sections.Contains(current))
            {
                current.RawSectionText = sectionRawText;
                sections.Add(current);
            }
            // ↓↓↓ AJOUTE ICI ↓↓↓
            // Fallback : si une section existe mais sans transactions, tenter parsing texte brut
            foreach (var sec in sections)
            {
                if (sec.Transactions.Count == 0)
                {
                    TryFallbackLineParsing(fullText, sec, documentYear);
                }
            }
            // ↑↑↑ FIN AJOUT ↑↑↑

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
        // NOUVELLE méthode à ajouter dans BankDocumentParser
        // Détecte si le fullText UBCI est en "Format B" (montant sur ligne suivante)
        // et reconstruit des transactions directement depuis le texte brut.
        private bool TryParseUbciFormatB(string fullText, BankAccountSection section, int? documentYear)
        {
            // Pattern: ligne avec date + libellé + date_valeur + ref_banque (tout collé)
            // suivie d'une ligne avec uniquement le montant
            // Ex:
            // "03/02/2025 COMMISSION REMISE CHEQUE : 055A093250340501 31/01/2025 055A093250340501"
            // "0,700"

            // On cherche si ce pattern existe : ligne date suivie d'une ligne montant seul
            var lines = fullText.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            // Regex pour ligne de type "dd/MM/yyyy LIBELLE ... dd/MM/yyyy REFBANQUE"
            var txLineRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            // Regex pour ligne montant seul (crédit ou débit)
            var amountOnlyRegex = new Regex(
                @"^-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3}$");

            int found = 0;
            for (int i = 0; i < lines.Count - 1; i++)
            {
                if (txLineRegex.IsMatch(lines[i]) && amountOnlyRegex.IsMatch(lines[i + 1]))
                {
                    found++;
                    if (found >= 3) return true; // Format B confirmé
                }
            }
            return false;
        }

        private List<BankAccountSection> ParseUbciFormatB(string fullText,
            string documentRib, string accountNumber, int? documentYear,
            DateTime? periodStart, DateTime? periodEnd)
        {
            var sections = new List<BankAccountSection>();
            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = "TND",
                SoldeInitial = null
            };

            // Extraire le solde initial
            var soldeInitMatch = Regex.Match(fullText,
                @"SOLDE\s+DEBITEUR\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (!soldeInitMatch.Success)
                soldeInitMatch = Regex.Match(fullText,
                    @"SOLDE\s+CREDITEUR\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
            if (soldeInitMatch.Success)
                current.SoldeInitial = ParseAmount(soldeInitMatch.Groups[1].Value);

            // Extraire le solde final
            var soldeFinalMatch = Regex.Match(fullText,
                @"SOLDE\s+DE\s+CLOTURE\s+(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (soldeFinalMatch.Success)
                current.SoldeFinal = ParseAmount(soldeFinalMatch.Groups[1].Value);

            // Totaux
            var totalMatch = Regex.Match(fullText,
                @"TOTAL\s+DU\s+DEBIT\s+ET\s+DU\s+CREDIT\s+(\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})\s+(\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
            {
                current.TotalDebit = ParseAmount(totalMatch.Groups[1].Value);
                current.TotalCredit = ParseAmount(totalMatch.Groups[2].Value);
            }

            // Lignes nettoyées
            var lines = fullText.Split('\n')
                .Select(l => l.Trim())
                .ToList();

            // Pattern ligne transaction Format B :
            // "dd/MM/yyyy LIBELLE ... dd/MM/yyyy REFBANQUE"
            // Le montant peut être IN-LINE (ex: "1,700" à la fin) ou sur la ligne suivante

            // Pattern 1 : montant IN-LINE à la fin de la ligne tx
            // "05/05/2026  COMMISSION SUR EFFET  1,700  04/05/2026  019EF..."
            var txInlineRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(-?\d{1,3}(?:[.,]\d{3})*[.,]\d{2,3})\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            // Pattern 2 : montant sur ligne suivante
            // ligne 1: "dd/MM/yyyy LIBELLE dd/MM/yyyy REFBANQUE"
            // ligne 2: "1,700"
            var txNoAmountRegex = new Regex(
                @"^(\d{2}/\d{2}/\d{4})\s+(.+?)\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$",
                RegexOptions.IgnoreCase);

            var amountOnlyRegex = new Regex(
                @"^(-?\d{1,3}(?:[.,\s]\d{3})*[.,]\d{2,3})$");

            // Lignes de bruit à ignorer
            var noiseRegex = new Regex(
                @"TOTAL\s+DU\s+DEBIT|SOLDE\s+DE\s+CLOTURE|SOLDE\s+DEBITEUR|SOLDE\s+CREDITEUR|" +
                @"Page\s+\d+|Natures\s+des|Date\s+op|Pour\s+plus|Vous\s+êtes|L'UBCI|" +
                @"PERIODE\s+DU|Rapport\s+d|www\.ubci|bensalah|SWIFT|R\.N\.E|MONTPLAISIR|" +
                @"BOUMHEL|Agence|AVENUE|RIB\s*:|CIF\s+CLIENT|RELEVE\s+DE|EXTRAIT\s+DE|" +
                @"كشف|الشرف|الرصيد|نقدم|Nous\s+avons|presente,\s+sauf|Société\s+Anonyme",
                RegexOptions.IgnoreCase);

            // Colonnes header à ignorer
            var headerRegex = new Regex(
                @"^(Date\s+op|Natures\s+des|Débit|Crédit|Date\s+valeur|Ref\s+Banque)",
                RegexOptions.IgnoreCase);

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (noiseRegex.IsMatch(line)) continue;
                if (headerRegex.IsMatch(line)) continue;

                // Essai Pattern 1 : tout en ligne (montant inclus avant date valeur)
                var m1 = txInlineRegex.Match(line);
                if (m1.Success)
                {
                    string date = m1.Groups[1].Value;
                    string libelle = CleanUbciLibelle(m1.Groups[2].Value);
                    decimal montant = ParseAmount(m1.Groups[3].Value);

                    if (!IsUbciLibelleNoise(libelle))
                    {
                        var tx = BuildUbciTransaction(date, libelle, montant, documentYear);
                        if (tx != null) current.Transactions.Add(tx);
                    }
                    continue;
                }

                // Essai Pattern 2 : montant sur ligne suivante
                var m2 = txNoAmountRegex.Match(line);
                if (m2.Success)
                {
                    string date = m2.Groups[1].Value;
                    string libelle = CleanUbciLibelle(m2.Groups[2].Value);

                    // Chercher le montant dans les prochaines lignes non vides
                    decimal? montant = null;
                    for (int j = i + 1; j < Math.Min(i + 4, lines.Count); j++)
                    {
                        string nextLine = lines[j].Trim();
                        if (string.IsNullOrWhiteSpace(nextLine)) continue;
                        var am = amountOnlyRegex.Match(nextLine);
                        if (am.Success)
                        {
                            montant = ParseAmount(am.Groups[1].Value);
                            i = j; // avancer l'index
                            break;
                        }
                        // Si la prochaine ligne non-vide est une nouvelle transaction, arrêter
                        if (Regex.IsMatch(nextLine, @"^\d{2}/\d{2}/\d{4}\s+")) break;
                    }

                    if (!IsUbciLibelleNoise(libelle))
                    {
                        if (montant.HasValue)
                        {
                            var tx = BuildUbciTransaction(date, libelle, montant.Value, documentYear);
                            if (tx != null) current.Transactions.Add(tx);
                        }
                        else
                        {
                            // Montant manquant : on ajoute quand même avec flag
                            current.Transactions.Add(new Transaction
                            {
                                Date = NormalizeDate(date, documentYear),
                                Libelle = libelle + " [MONTANT MANQUANT - a verifier manuellement]"
                            });
                        }
                    }
                    continue;
                }
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        private string CleanUbciLibelle(string raw)
        {
            // Supprimer les refs banques et dates valeur qui traînent dans le libellé
            string result = raw.Trim();
            // Supprimer pattern "dd/MM/yyyy REF" en fin de libellé (date valeur + ref banque)
            result = Regex.Replace(result, @"\s+\d{2}/\d{2}/\d{4}\s+\S+\s*$", "").Trim();
            // Supprimer refs banques seules en fin
            result = Regex.Replace(result, @"\s+[0-9A-Za-z]{10,30}\s*$", "").Trim();
            // Nettoyer espaces multiples
            result = Regex.Replace(result, @"\s{2,}", " ").Trim();
            return result;
        }

        private bool IsUbciLibelleNoise(string libelle)
        {
            return string.IsNullOrWhiteSpace(libelle)
                || libelle.Length < 3
                || Regex.IsMatch(libelle, @"^[\-_\|\.]+$")
                || Regex.IsMatch(libelle, @"^[a-z]?\.\s*\""?\s*", RegexOptions.IgnoreCase)
                || Regex.IsMatch(libelle,
                    @"l\.\s*""\s*ate_|Date_|opération|Natures\s+des|Débit|Crédit|Ref\s+Banque",
                    RegexOptions.IgnoreCase);
        }

        private Transaction? BuildUbciTransaction(string date, string libelle, decimal montant, int? documentYear)
        {
            string normalizedDate = NormalizeDate(date, documentYear);
            if (string.IsNullOrEmpty(normalizedDate)) return null;

            // Déterminer débit ou crédit :
            // UBCI : les opérations DÉBIT ont des libellés spécifiques
            // On utilise le contexte du libellé + le signe éventuel
            var tx = new Transaction
            {
                Date = normalizedDate,
                Libelle = libelle
            };

            if (montant < 0)
            {
                tx.Debit = Math.Abs(montant);
            }
            else if (montant > 0)
            {
                // Détecter si c'est un débit selon le libellé
                bool isDebit = IsUbciDebitByLibelle(libelle);
                if (isDebit) tx.Debit = montant;
                else tx.Credit = montant;
            }

            return tx;
        }

        private bool IsUbciDebitByLibelle(string libelle)
        {
            return Regex.IsMatch(libelle,
                @"COMMISSION|PRELEVEMENT\s+RECU|TVA|PDL|FRAIS|PAIEMENT|CHEQUE\s+RECU\s+TELECOMPENSATION|" +
                @"VIREMENT\s+DOMESTIQUE\s+EMIS|VIREMENT\s+PERMANENT\s+EMIS|PAYM\s+CARTE|LOYER|" +
                @"ABONNEMENT|TARIFICATION|EFFET\s+RECU",
                RegexOptions.IgnoreCase);
        }
        private void TryFallbackLineParsing(string fullText, BankAccountSection section, int? documentYear)
        {

            Console.WriteLine($"[FALLBACK] Tentative sur texte de {fullText.Length} chars");
            Console.WriteLine($"[FALLBACK] Premiers 500 chars : {fullText.Substring(0, Math.Min(500, fullText.Length))}");

            // teste directement sur les lignes
            var lines = fullText.Split('\n');
            Console.WriteLine($"[FALLBACK] Nombre de lignes : {lines.Length}");
            foreach (var line in lines.Take(20))
            {
                Console.WriteLine($"[FALLBACK-LINE] '{line}'");
            }
            // Regex générique : capture toute ligne contenant une date dd/MM/yyyy
            // et au moins un montant, quel que soit ce qui précède
            var lineRegex = new Regex(
                @"(?:^|\s)(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            // Format AMEN : "58 01/07/2026 LIBELLE 07/07/2026 1,968"
            var amenRegex = new Regex(
                @"^\s*\d{2,3}\s+(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(\d{2}/\d{2}/\d{4})\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            // Format simple : "01/07/2026 LIBELLE 1,968"
            var simpleRegex = new Regex(
                @"^\s*(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(-?\d{1,3}(?:[ .,]?\d{3})*[.,]\d{2,3})\s*$",
                RegexOptions.Multiline);

            bool found = false;

            // Essai 1 : format AMEN avec code opération
            foreach (Match m in amenRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[4].Value);

                // Ignorer les lignes de bruit
                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                    found = true;
                }
            }

            if (found) return;

            // Essai 2 : format avec date opération + date valeur + montant
            foreach (Match m in lineRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[4].Value);

                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                    found = true;
                }
            }

            if (found) return;

            // Essai 3 : format simple date + libelle + montant
            foreach (Match m in simpleRegex.Matches(fullText))
            {
                string dateOp = m.Groups[1].Value;
                string libelle = m.Groups[2].Value.Trim();
                decimal montant = ParseAmount(m.Groups[3].Value);

                if (Regex.IsMatch(libelle, @"Totaux|Report|TOTAUX|Solde|Total|Page", RegexOptions.IgnoreCase))
                    continue;

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, documentYear),
                    Libelle = libelle
                };

                if (montant < 0) tx.Debit = Math.Abs(montant);
                else if (montant > 0) tx.Credit = montant;

                if (!string.IsNullOrEmpty(tx.Date))
                {
                    section.Transactions.Add(tx);
                }
            }
        }
        // ↓↓↓ NOUVEAU BLOC À INSÉRER ICI ↓↓↓
        private static readonly string[] BhKnownLibellePrefixes = {
              "COMFORC PRLV.", "VERSEMENT TPE", "VERS.CHQ.ORDIN", "VRST.AUT.AG.",
            "ENC.CHQ.TN", "ENC.EFFET TN", "Vers ESP RECU",
            "COMMISSION", "T.V.A", "PRLV.", "VRST.",

            };
        private string ExtractBhLibelle(string middle) {
            string stripped = middle.Trim();
            if (stripped.Length == 0) return stripped;

            foreach (var prefix in BhKnownLibellePrefixes)
            {
                if (stripped.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return stripped.Substring(0, prefix.Length).Trim();
            }

            var ibMatch = Regex.Match(stripped, @"\bIB\b");
            if (ibMatch.Success && ibMatch.Index > 0)
                return stripped.Substring(0, ibMatch.Index).Trim();

            return stripped;
        }
        // ↑↑↑ FIN DU NOUVEAU BLOC ↑↑↑



        
        private (int? debit, int? credit) InferAnchorsFromAmountPositions(List<TableRow> rows)
        {
            var positions = new List<int>();
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                    if (AmountRegex.IsMatch(cell.Text))
                        positions.Add(cell.Left);

            var sorted = positions.Distinct().OrderBy(x => x).ToList();
            if (sorted.Count < 2) return (null, null);

            int bestGapIndex = 0, bestGap = 0;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                int gap = sorted[i + 1] - sorted[i];
                if (gap > bestGap) { bestGap = gap; bestGapIndex = i; }
            }
            // Gap trop faible : les montants ne se separent pas clairement en 2 colonnes distinctes,
            // on renonce plutot que de deviner un mauvais decoupage.
            if (bestGap < 40) return (null, null);

            int leftCluster = (int)sorted.Take(bestGapIndex + 1).Average();
            int rightCluster = (int)sorted.Skip(bestGapIndex + 1).Average();
            return (leftCluster, rightCluster);
        }
        private List<BankAccountSection> ExtractBhAccountSections(string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);

            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = ExtractCurrency(fullText),
                SoldeInitial = null
            };

            var bhLineRegex = new Regex(
            @"^(?:\d+\s+)?(\d{2}/\d{2}/\d{4})\s+(.+?)\s+(?:(\d{2}/\d{2}/\d{4})\s+)?(-?\d[\d\s.,]*\d|\d)\s*$",
            RegexOptions.IgnoreCase);

            var soldeOuvertureRegex = new Regex(@"^(-?\d[\d\s.,]*\d|\d)\s*$");
            var soldeAuLabelRegex = new Regex(@"Solde\s+au\s+\d{2}/\d{2}/\d{4}", RegexOptions.IgnoreCase);

            var lines = fullText.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var soldeAmountMatch = soldeOuvertureRegex.Match(line);
                if (soldeAmountMatch.Success)
                {
                    string nextLine = (i + 1 < lines.Length) ? lines[i + 1].Trim() : "";
                    if (soldeAuLabelRegex.IsMatch(nextLine))
                    {
                        current.SoldeInitial = ParseAmount(soldeAmountMatch.Groups[1].Value);
                    }
                    continue;
                }

                if (soldeAuLabelRegex.IsMatch(line) && !Regex.IsMatch(line, @"^\d{2}/\d{2}/\d{4}"))
                    continue;

                if (Regex.IsMatch(line, @"^-?\d[\d\s.,]*\s+-?\d[\d\s.,]*$"))
                    continue;

                if (Regex.IsMatch(line, @"Total\s+des\s+mouvements|^Solde\s+au\s+\d{2}/\d{2}/\d{4}\s*:", RegexOptions.IgnoreCase))
                {
                    var soldeFinalMatch = AmountRegex.Match(line);
                    if (soldeFinalMatch.Success)
                        current.SoldeFinal = ParseAmount(soldeFinalMatch.Value);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Date\s+op[ée]ration|N[o°]\s*du\s+compte|Titulaire\s+du\s+compte|Op[ée]rations\s+du|Extrait\s+de\s+Compte", RegexOptions.IgnoreCase))
                    continue;

                var match = bhLineRegex.Match(line);
                if (!match.Success)
                {
                    Console.WriteLine($"[BH-WARN] Ligne non reconnue (ignorée) : {line}");
                    continue;
                }

                string dateOp = match.Groups[1].Value;
                string libelle = ExtractBhLibelle(match.Groups[2].Value);
                decimal montant = ParseAmount(match.Groups[4].Value);

                var tx = new Transaction
                {
                    Date = NormalizeDate(dateOp, null),
                    Libelle = libelle
                };

                if (IsBhDebitLibelle(libelle))
                    tx.Debit = montant;
                else
                    tx.Credit = montant;

                current.Transactions.Add(tx);
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

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

        
            var formatsWithYear = new[]
            {
                "dd/MM/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
                "dd/MM/yy", "dd-MM-yy", "dd.MM.yy",
                "dd MMM yyyy", "dd MMM yy", "dd-MMM-yyyy", "dd-MMM-yy", "dd.MMM.yyyy", "dd.MMM.yy",
            };

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
        private string GetDateFromAnyCell(List<string> cellTexts, int? defaultYear = null)
        {
            foreach (var cell in cellTexts)
            {
                string normalized = NormalizeDate(cell.Trim(), defaultYear);
                if (!string.IsNullOrEmpty(normalized))
                    return normalized;
            }
            return "";
        }

        // [BNA-EXTRAIT] Variante toleree au bruit OCR isole colle a la date (ex. cellule
        // "? 04/07/2025" ou "ا 04/07/2025" - un glyphe parasite en debut de la meme cellule que
        // la date d'operation). GetDateFromAnyCell exige une correspondance EXACTE de toute la
        // cellule et echoue alors sur cette cellule-la, ce qui le fait glisser vers la cellule
        // suivante contenant la date de Valeur (fausse date d'operation). On extrait ici la
        // sous-chaine "jj/mm/aaaa" ou qu'elle soit dans la cellule, cellule par cellule dans
        // l'ordre (gauche a droite), pour retrouver la date d'operation avant d'atteindre la
        // colonne Valeur.
        private string GetDateFromAnyCellLenient(List<string> cellTexts, int? defaultYear)
        {
            foreach (var cell in cellTexts)
            {
                var m = Regex.Match(cell, @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}");
                if (!m.Success) continue;
                string normalized = NormalizeDate(m.Value, defaultYear);
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
                @"Num[ée]ro\s*de\s*Compte\s*:?\s*(\d[\d\s-]{6,25})",   // <-- AJOUTE CETTE LIGNE
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
        // ═══════════════════════════════════════════════════════════════════════════
        // REMPLACE ENTIÈREMENT ExtractUbciAccountSections + ajoute UbciAssignByLibelle
        // Aucune autre méthode touchée.
        // ═══════════════════════════════════════════════════════════════════════════

        private List<BankAccountSection> ExtractUbciAccountSections(List<TableRow> rows, string fullText)
        {
            var sections = new List<BankAccountSection>();
            string documentRib = ExtractRib(fullText);
            string accountNumber = ExtractAccountNumber(fullText);

            // ── Dédupliquer (PDF UBCI double-rendu image+texte) ──────────
            var seenRowKeys = new HashSet<string>();
            var dedupedRows = new List<TableRow>();
            foreach (var row in rows)
            {
                string rowKey = string.Join("|", row.Cells.Select(c => CleanWhitespace(c.Text).Trim()));
                if (string.IsNullOrWhiteSpace(rowKey)) continue;
                if (!seenRowKeys.Add(rowKey)) continue;
                dedupedRows.Add(row);
            }

            // ── Période & année ───────────────────────────────────────────
            DateTime? ubciPeriodStart = null, ubciPeriodEnd = null;
            var ubciPeriodeMatch = Regex.Match(fullText,
                @"PERIODE\s+DU\s+(\d{2}/\d{2}/\d{4})\s+AU\s+(\d{2}/\d{2}/\d{4})",
                RegexOptions.IgnoreCase);
            if (ubciPeriodeMatch.Success)
            {
                if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[1].Value, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var ps)) ubciPeriodStart = ps;
                if (DateTime.TryParseExact(ubciPeriodeMatch.Groups[2].Value, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var pe)) ubciPeriodEnd = pe;
            }
            int? documentYear = ubciPeriodStart?.Year ?? ubciPeriodEnd?.Year;

            // ── ÉTAPE 1 : détecter les ancres depuis l'en-tête ───────────
            // Parcourir les rows pour trouver "Débit" et "Crédit"
            int? debitAnchor = null, creditAnchor = null, dateValeurAnchor = null;
            foreach (var row in dedupedRows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                var dCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"^D[ée]bit$", RegexOptions.IgnoreCase));
                var cCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"^Cr[ée]dit$", RegexOptions.IgnoreCase));
                var dvCell = cells.FirstOrDefault(c =>
                    Regex.IsMatch(c.Text.Trim(), @"Date\s*valeur", RegexOptions.IgnoreCase));
                if (dCell != null) debitAnchor = dCell.Left;
                if (cCell != null) creditAnchor = cCell.Left;
                if (dvCell != null) dateValeurAnchor = dvCell.Left;
                if (debitAnchor.HasValue && creditAnchor.HasValue) break;
            }

            // ── ÉTAPE 2 : si ancres non trouvées, les inférer depuis
            //    les positions des montants dans toutes les rows ────────────
            if (!debitAnchor.HasValue || !creditAnchor.HasValue)
            {
                // Collecter toutes les positions X de cellules contenant un montant
                var amountPositions = new List<int>();
                foreach (var row in dedupedRows)
                    foreach (var cell in row.Cells)
                        if (AmountRegex.IsMatch(cell.Text.Trim()))
                            amountPositions.Add(cell.Left);

                if (amountPositions.Count >= 4)
                {
                    // Grouper en clusters : les montants UBCI sont en 2 colonnes distinctes
                    var sorted = amountPositions.Distinct().OrderBy(x => x).ToList();
                    int bestGap = 0, bestIdx = 0;
                    for (int i = 0; i < sorted.Count - 1; i++)
                    {
                        int gap = sorted[i + 1] - sorted[i];
                        if (gap > bestGap) { bestGap = gap; bestIdx = i; }
                    }
                    if (bestGap > 10) // seuil minimal pour 2 colonnes distinctes
                    {
                        int leftCluster = (int)sorted.Take(bestIdx + 1).Average();
                        int rightCluster = (int)sorted.Skip(bestIdx + 1).Average();
                        if (!debitAnchor.HasValue) debitAnchor = leftCluster;
                        if (!creditAnchor.HasValue) creditAnchor = rightCluster;
                        Console.WriteLine($"[UBCI] Ancres inférées : Débit~{debitAnchor} Crédit~{creditAnchor}");
                    }
                }
            }

            // ── ÉTAPE 3 : détecter la colonne Date (Left minimal des dates) ─
            // On cherche le Left médian des cellules dont le texte est une date dd/MM/yyyy
            var dateCellPositions = new List<int>();
            foreach (var row in dedupedRows)
                foreach (var cell in row.Cells)
                    if (Regex.IsMatch(cell.Text.Trim(), @"^\d{2}/\d{2}/\d{4}$"))
                        dateCellPositions.Add(cell.Left);

            // La colonne "Date opération" est la plus à gauche (le min des positions)
            // La colonne "Date valeur" est vers x~640 (plus à droite)
            int dateOpAnchor = dateCellPositions.Count > 0 ? dateCellPositions.Min() : 0;
            // Toutes les dates dont Left est proche de dateOpAnchor (±30) sont des dates d'opération
            // Les autres (Left >> dateOpAnchor) sont des dates valeur → on les ignore dans le libellé
            int dateOpMaxLeft = dateOpAnchor + 30;

            Console.WriteLine($"[UBCI] dateOpAnchor={dateOpAnchor} dateOpMaxLeft={dateOpMaxLeft} " +
                              $"debitAnchor={debitAnchor} creditAnchor={creditAnchor} dateValeurAnchor={dateValeurAnchor}");

            // ── Section courante ──────────────────────────────────────────
            var current = new BankAccountSection
            {
                AccountNumber = accountNumber,
                Rib = documentRib,
                Currency = "TND",
                SoldeInitial = null
            };

            string pendingDate = "";
            string pendingLibelle = "";
            DateTime? ubciLastConfirmedDate = null;
            bool ubciClotureReached = false;

            // Réparation dates fusionnées (9-10 chiffres)
            bool TryRepairUbciDate(string rawDigits, out string repaired)
            {
                repaired = "";
                if (rawDigits.Length != 9 && rawDigits.Length != 10) return false;
                if (!Regex.IsMatch(rawDigits, @"^\d+$")) return false;
                var candidates = new List<string>();
                if (rawDigits.Length == 9)
                {
                    candidates.Add(rawDigits.Remove(2, 1));
                    candidates.Add(rawDigits.Remove(4, 1));
                }
                else candidates.Add(rawDigits.Remove(5, 1).Remove(2, 1));

                foreach (var candidate in candidates)
                {
                    if (candidate.Length != 8) continue;
                    if (!DateTime.TryParseExact(candidate, "ddMMyyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) continue;
                    if (ubciPeriodStart.HasValue && parsed < ubciPeriodStart.Value.AddDays(-5)) continue;
                    if (ubciPeriodEnd.HasValue && parsed > ubciPeriodEnd.Value.AddDays(5)) continue;
                    if (ubciLastConfirmedDate.HasValue &&
                        Math.Abs((parsed - ubciLastConfirmedDate.Value).TotalDays) > 31) continue;
                    repaired = parsed.ToString("dd/MM/yyyy");
                    return true;
                }
                return false;
            }

            // ── Boucle principale ─────────────────────────────────────────
            foreach (var row in dedupedRows)
            {
                var cells = row.Cells.OrderBy(c => c.Left).ToList();
                if (cells.Count == 0) continue;

                string joined = string.Join(" ", cells.Select(c => CleanWhitespace(c.Text).Trim()));
                if (string.IsNullOrWhiteSpace(joined)) continue;

                // ── Solde ouverture ──────────────────────────────────────
                var openMatch = Regex.Match(joined,
                    @"SOLDE\s+(DEBITEUR|CREDITEUR)\s+AU\s+\d{2}/\d{2}/\d{4}\s+(-?\d{1,3}(?:[.,]\d{3})*[.,]\d{2,3})",
                    RegexOptions.IgnoreCase);
                if (openMatch.Success)
                {
                    decimal init = ParseAmount(openMatch.Groups[2].Value);
                    current.SoldeInitial = openMatch.Groups[1].Value
                        .Equals("DEBITEUR", StringComparison.OrdinalIgnoreCase)
                        ? -Math.Abs(init) : Math.Abs(init);
                    continue;
                }

                // ── En-têtes / bruit ─────────────────────────────────────
                if (Regex.IsMatch(joined, @"^D[ée]bit$|^Cr[ée]dit$|Date\s*valeur|Natures\s+des|^Date\s*$|^opération$",
                    RegexOptions.IgnoreCase)) continue;

                // ── Totaux / clôture ─────────────────────────────────────
                if (Regex.IsMatch(joined, @"TOTAL\s+DU\s+DEBIT\s+ET\s+DU\s+CREDIT", RegexOptions.IgnoreCase))
                {
                    var amounts = AmountRegex.Matches(joined);
                    if (amounts.Count >= 2)
                    {
                        current.TotalDebit = ParseAmount(amounts[0].Value);
                        current.TotalCredit = ParseAmount(amounts[1].Value);
                    }
                    continue;
                }
                if (Regex.IsMatch(joined, @"SOLDE\s+DE\s+CLOTURE", RegexOptions.IgnoreCase))
                {
                    var m = AmountRegex.Match(joined);
                    if (m.Success) current.SoldeFinal = ParseAmount(m.Value);
                    ubciClotureReached = true;
                    continue;
                }
                if (ubciClotureReached) continue;

                // ── Bruit pied de page ───────────────────────────────────
                if (Regex.IsMatch(joined,
                    @"Cette\s+op[ée]ration\s+est\s+provisoire|^R\.?\s*N\.?\s*E\b|SWIFT|" +
                    @"Pour\s+plus\s+de\s+d[ée]tails|Vous\s+[êe]tes\s+tenu|L'UBCI\s+s'engage|" +
                    @"bensalah|www\.ubci|Page\s*\d+\s*/|Soci[eé]t[eé]\s+Anonyme",
                    RegexOptions.IgnoreCase)) continue;


                int montantDebutX = debitAnchor.HasValue
                    ? Math.Min(debitAnchor.Value, creditAnchor ?? debitAnchor.Value) - 60
                    : int.MaxValue;
                int montantFinX = dateValeurAnchor.HasValue
                    ? dateValeurAnchor.Value + 15   // marge élargie : n'exclut plus les montants longs
                    : int.MaxValue;
                int tolerance = 50; // tolérance pour déterminer débit vs crédit
                // Cellule date d'opération
                TableCell? dateCellOp = cells.FirstOrDefault(c =>
                    c.Left <= dateOpMaxLeft &&
                    Regex.IsMatch(c.Text.Trim(), @"^\d{2}/\d{2}/\d{4}$"));

                // Date fusionnée (9-10 chiffres) → tentative de réparation
                if (dateCellOp == null)
                {
                    var fusedCell = cells.FirstOrDefault(c =>
                        c.Left <= dateOpMaxLeft &&
                        Regex.IsMatch(c.Text.Trim(), @"^\d{9,10}$"));
                    if (fusedCell != null && TryRepairUbciDate(fusedCell.Text.Trim(), out var repaired))
                    {
                        // Créer une cellule virtuelle avec la date réparée
                        dateCellOp = new TableCell { Text = repaired, Left = fusedCell.Left };
                    }
                }

                // Cellules libellé : entre dateOpMaxLeft et zone montant
                var libCells = cells
                    .Where(c => c.Left > dateOpMaxLeft && c.Left < montantDebutX)
                    .Select(c => CleanWhitespace(c.Text).Trim())
                    .Where(t => !string.IsNullOrWhiteSpace(t)
                             && !Regex.IsMatch(t, @"^\d{2}/\d{2}/\d{4}$") // pas de date valeur
                             && !Regex.IsMatch(t, @"^[A-Z0-9]{12,30}$"))  // pas de ref banque seule
                    .ToList();
                string libelleFromCells = Regex.Replace(string.Join(" ", libCells), @"\s{2,}", " ").Trim();

                // Cellules montant : dans la zone Débit/Crédit (avant date valeur)
                var montantCells = cells
      .Where(c => c.Left >= montantDebutX && c.Left < montantFinX
               && AmountRegex.IsMatch(c.Text.Trim())
               && !Regex.IsMatch(c.Text.Trim(), @"^\d{2}[/.\-]\d{2}[/.\-]\d{2,4}$")) // jamais une date valeur
      .ToList();

                Console.WriteLine($"[UBCI-ROW] dateOp={dateCellOp?.Text ?? "NULL"} " +
                                  $"libelle='{libelleFromCells}' " +
                                  $"montants={montantCells.Count} " +
                                  $"({string.Join(",", montantCells.Select(m => $"x={m.Left}:{m.Text}"))})");

                // ── CAS 1 : date + montant(s) → transaction complète ─────
                if (dateCellOp != null && montantCells.Count > 0)
                {
                    // Flush pending orphelin
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                        pendingDate = ""; pendingLibelle = "";
                    }

                    string date = NormalizeDate(dateCellOp.Text.Trim(), documentYear);
                    if (string.IsNullOrEmpty(date)) continue;

                    string fullLibelle = string.IsNullOrEmpty(pendingLibelle)
                        ? libelleFromCells
                        : (pendingLibelle + " " + libelleFromCells).Trim();
                    pendingDate = ""; pendingLibelle = "";

                    foreach (var mc in montantCells)
                    {
                        decimal montant = ParseAmount(AmountRegex.Match(mc.Text).Value);
                        var tx = new Transaction { Date = date, Libelle = fullLibelle };
                        UbciAssignDebitCredit(tx, montant, mc.Left, debitAnchor, creditAnchor, tolerance);
                        if (!string.IsNullOrEmpty(tx.Date)) current.Transactions.Add(tx);
                    }

                    if (DateTime.TryParseExact(date, "dd/MM/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
                        ubciLastConfirmedDate = cd;
                    continue;
                }

                // ── CAS 2 : date SANS montant → début d'une transaction ──
                if (dateCellOp != null && montantCells.Count == 0)
                {
                    // Flush pending orphelin
                    if (!string.IsNullOrEmpty(pendingDate))
                    {
                        current.Transactions.Add(new Transaction
                        {
                            Date = pendingDate,
                            Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                        });
                    }
                    pendingDate = NormalizeDate(dateCellOp.Text.Trim(), documentYear);
                    pendingLibelle = libelleFromCells;
                    continue;
                }

                // ── CAS 3 : SANS date, AVEC montant → rattacher au pending
                if (dateCellOp == null && montantCells.Count > 0 && !string.IsNullOrEmpty(pendingDate))
                {
                    string extraLib = libelleFromCells;
                    string fullLibelle = string.IsNullOrEmpty(extraLib)
                        ? pendingLibelle
                        : (pendingLibelle + " " + extraLib).Trim();

                    foreach (var mc in montantCells)
                    {
                        decimal montant = ParseAmount(AmountRegex.Match(mc.Text).Value);
                        var tx = new Transaction { Date = pendingDate, Libelle = fullLibelle };
                        UbciAssignDebitCredit(tx, montant, mc.Left, debitAnchor, creditAnchor, tolerance);
                        current.Transactions.Add(tx);
                    }

                    if (DateTime.TryParseExact(pendingDate, "dd/MM/yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var cd))
                        ubciLastConfirmedDate = cd;

                    pendingDate = ""; pendingLibelle = "";
                    continue;
                }

                // ── CAS 4 : SANS date, SANS montant → continuation libellé
                if (dateCellOp == null && montantCells.Count == 0
                    && !string.IsNullOrEmpty(pendingDate)
                    && !string.IsNullOrEmpty(libelleFromCells))
                {
                    pendingLibelle = (pendingLibelle + " " + libelleFromCells).Trim();
                }
            }

            // Flush dernier pending
            if (!string.IsNullOrEmpty(pendingDate))
            {
                current.Transactions.Add(new Transaction
                {
                    Date = pendingDate,
                    Libelle = (pendingLibelle + " [MONTANT MANQUANT - a verifier manuellement]").Trim()
                });
            }

            current.RawSectionText = fullText;
            sections.Add(current);
            return sections;
        }

        // ── Assigner Débit/Crédit par position X ────────────────────
        // Méthode privée UBCI uniquement, zéro impact sur les autres banques
        private void UbciAssignDebitCredit(Transaction tx, decimal montant, int cellLeft,
            int? debitAnchor, int? creditAnchor, int tolerance)
        {
            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                int distDebit = Math.Abs(cellLeft - debitAnchor.Value);
                int distCredit = Math.Abs(cellLeft - creditAnchor.Value);
                if (distDebit <= distCredit) tx.Debit = Math.Abs(montant);
                else tx.Credit = Math.Abs(montant);
            }
            else
            {
                // Fallback : libellé
                UbciAssignByLibelle(tx, montant, tx.Libelle);
            }
        }

        private void UbciAssignByLibelle(Transaction tx, decimal montant, string libelle)
        {
            bool isDebit = Regex.IsMatch(libelle,
                @"^\s*(COMMISSION|TVA\b|PDL\b|FRAIS|PAIEMENT|PAYM\s+CARTE|LOYER|ABONNEMENT|" +
                @"TARIFICATION|EFFET\s+RECU|PRELEVEMENT\s+RECU|CHEQUE\s+RECU\s+TELECOMPENSATION|" +
                @"VIREMENT\s+DOMESTIQUE\s+EMIS|VIREMENT\s+PERMANENT\s+EMIS|REGLEMENT\s+CHEQUE)",
                RegexOptions.IgnoreCase);
            if (isDebit) tx.Debit = Math.Abs(montant);
            else tx.Credit = Math.Abs(montant);
        }
        private void AssignAmounts(Transaction tx, dynamic amountCandidates, int? debitAnchor, int? creditAnchor, int? soldeAnchor, int? montantAnchor, bool isBtk, ref decimal? previousSolde , bool hasSignedAmounts, out decimal? soldeCourant)
        {
            const int Tolerance = 15;

            soldeCourant = null;   // remplace tx.Solde

            if (debitAnchor.HasValue && creditAnchor.HasValue)
            {
                var ambiguous = new List<dynamic>();

                // [BTK] Chaque ligne BTK n'affiche que Montant (Débit OU Crédit) + Solde,
              
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
                    // [FAMILLE MONTANTS SIGNES] QNB, BIAT extrait, et tout document ou hasSignedAmounts==true :
                    
                    else if (hasSignedAmounts)
                    {
                        if (cand.Value < 0) tx.Debit = Math.Abs(cand.Value);
                        else if (cand.Value > 0) tx.Credit = cand.Value;
                    }
                    else if (Math.Abs(distDebit - distCredit) < Tolerance)
                    {
                        ambiguous.Add(cand);
                    }
                    else if (distDebit < distCredit)
                    {
                        tx.Debit = Math.Abs(cand.Value);
                    }
                    else
                    {
                        tx.Credit = Math.Abs(cand.Value);
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
                    if (hasSignedAmounts)
                    {
                        if (val < 0) tx.Debit = Math.Abs(val);
                        else if (val > 0) tx.Credit = val;
                    }
                    else
                    {
                        if (val < 0) tx.Debit = Math.Abs(val);
                        else if (val > 0) tx.Credit = val;
                    }
                    soldeCourant = null;
                }
                else
                {
                    decimal mouvement = amounts[0];
                    decimal solde = amounts[amounts.Count - 1];
                    soldeCourant = solde;
                    if (hasSignedAmounts)
                    {
                        // colonne unique signée : tous les montants sont des mouvements, pas de solde
                        if (mouvement < 0) tx.Debit = Math.Abs(mouvement);
                        else if (mouvement > 0) tx.Credit = mouvement;
                        soldeCourant = null;
                    }
                    else if (previousSolde.HasValue)
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
     
        private static bool IsBiatNoise(string text)
        {
            if (ArabicScriptRegex.IsMatch(text)) return true;
            return Regex.IsMatch(text,
                
                @"Titulaire\s+du\s+compte|N[°o]?\s*de\s+compte|\bRIB\b|Cher\s+client|Agence\s*:\s*Mr\b|Fonds\s+de\s+Garantie|RELEVE\s+.*MENSUEL|BANQUE\s+INTERNATIONALE\s+ARABE|TOTAUX|Nous\s+avons\s+l.honneur|Nous\s+vous\s+prions\s+de\s+contacter",
                RegexOptions.IgnoreCase);
        }

        // [BIAT] "عليه"/"له" sont des mots arabes très courants qui apparaissent aussi dans les
       
        private static bool IsBiatArabicHeaderCell(string text, string keyword)
        {
            string trimmed = text.Trim().Trim('‏', '‎', '.', ':', '»', '«', '،', ' ');
            return trimmed.Length > 0 && trimmed.Length <= 8 && trimmed.Contains(keyword);
        }

     
        private static readonly string[] BhDebitKeywords =
        {
            "PRLV.", "COMMISSION", "T.V.A", "COMFORC", "VIR.TN MM BQ"
        };

        private bool IsBhDebitLibelle(string libelle)
        {

            // Nettoie le bruit OCR devant le mot-clé (_, |, [, ], espaces, tirets, points isolés)
            string cleaned = libelle.TrimStart('_', '|', '[', ']', ' ', '-', '.', '\t');

            foreach (var kw in BhDebitKeywords)
                if (cleaned.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
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
     Regex.Replace(
         text.Replace('\u00A0', ' ')
             .Replace('\u202F', ' ')
             .Replace('\u2212', '-')
             .Replace('\r', ' ')
             .Replace('\n', ' '),
             @"\s+", " ").Trim();

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
        private static readonly Dictionary<string, string> FrenchMonthsAbbrev = new(StringComparer.OrdinalIgnoreCase)
        {
            {"janv","01"}, {"fevr","02"}, {"févr","02"}, {"mars","03"}, {"avr","04"},
            {"mai","05"}, {"juin","06"}, {"juil","07"}, {"aout","08"}, {"août","08"},
            {"sept","09"}, {"oct","10"}, {"nov","11"}, {"dec","12"}, {"déc","12"},
        };

        private string ConvertFrenchAbbrevDates(string text)
        {
            return Regex.Replace(text, @"(\d{1,2})\s*([A-Za-zéûÉÛ]{3,5})\.?\s*(\d{2,4})", m =>
            {
                string day = m.Groups[1].Value.PadLeft(2, '0');
                string monthRaw = m.Groups[2].Value;
                string year = m.Groups[3].Value;
                foreach (var kv in FrenchMonthsAbbrev)
                    if (monthRaw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                        return $"{day}/{kv.Value}/{(year.Length == 2 ? "20" + year : year)}";
                return m.Value;
            });
        }
        private string ExtractBankName(string text)
        {
            // [BTE] Détection dédiée : nom complet en priorité, sigle avec limites de
            // mots en repli (insensible à la casse, contrairement au Contains() ci-
            // dessous utilisé pour les autres banques).
            if (text.Contains("Banque de Tunisie et des Emirats", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(text, @"\bBTE\b"))
            {
                return "Banque de Tunisie et des Emirats (BTE)";
            }
            var knownBanks = new (string Keyword, string FullName)[]
            {
                ("BNA", "Banque Nationale Agricole (BNA)"),
                ("BIAT", "Banque Internationale Arabe de Tunisie (BIAT)"),
                ("STB", "Société Tunisienne de Banque (STB)"),
                ("ATB", "Arab Tunisian Bank (ATB)"),
                ("UIB", "Union Internationale de Banques (UIB)"),
                ("ATTIJARI", "Attijari Bank"),
                ("AMEN BANK", "Amen Bank"),
                ("WIFAK", "Wifak Bank"),                      // ← AJOUT
                ("BH", "Banque de l'Habitat (BH)"),
                ("BTK", "Banque Tuniso-Koweitienne (BTK)") ,
                ("ALBARAKA", "Al Baraka Bank Tunisia"),
                ("Bank ABC", "Bank ABC Tunisia"),
                ("BTE", "Banque Tuniso-Emiratie (BTE)"),          // ← AJOUT
                ("BANK ABC", "Bank ABC Tunisia")// ← ajouter
            };

            foreach (var bank in knownBanks)
                if (text.Contains(bank.Keyword))
                    return bank.FullName;

            return "";
        }
    }
}