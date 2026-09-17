using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services.Export
{
    // Regles metier BANQUE ZITOUNA (cahier des charges utilisateur, 2026-09-17) pour pre-remplir
    // Compte Debit / Compte Credit dans l'export Excel (voir BankExcelExporter). Couche separee de
    // LedgerAccountClassifier (regles generiques toutes banques) : ne modifie jamais Transaction.Debit
    // / Transaction.Credit / Date / Libelle, ne touche jamais au parsing (BankDocumentParser).
    //
    // Regle generale (section 2 du cahier des charges) : chaque regle ci-dessous est ecrite pour le
    // cas ou le montant est dans la colonne DEBIT. Si le montant est dans la colonne CREDIT, on
    // inverse UNIQUEMENT Compte Debit / Compte Credit (jamais les colonnes de montant). Cela vaut
    // aussi pour les cas a 2 lignes (VERSEMENT ESP / RETRAIT ESP hors liste, section 8/9) : le texte
    // du cahier des charges donne le cas Debit, le cas Credit s'obtient en inversant les 2 comptes de
    // chaque ligne (confirme par l'utilisateur - pas de variante "cote naturel" separee).
    public static class ZitounaLedgerAccountClassifier
    {
        private const string CompteBancaire = "532000";
        private const string CompteIntermediaire = "580000";
        private const string CompteCaisse = "541000";
        private const string CompteClientDivers = "411000";
        private const string CompteFournisseurClient = "461000";

        public static bool IsZitounaDocument(BankAccountSection account, string? bankName)
        {
            if (!string.IsNullOrEmpty(bankName) && bankName.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase))
                return true;
            return account != null && account.RawSectionText.Contains("ZITOUNA", StringComparison.OrdinalIgnoreCase);
        }

        // Une regle simple retourne 1 ligne, les cas VERSEMENT ESP / RETRAIT ESP hors liste (section
        // 8/9) en retournent 2 (dedoublement comptable d'une seule operation, meme motif que
        // LedgerAccountClassifier.IsRetraitEspeces cote generique). Liste vide -> aucune regle ne
        // correspond (cellules laissees vides, comme le classifieur generique).
        //
        // customerName est le titulaire du compte (DocumentMetadata.CustomerName, deja affiche dans
        // la case "Societe :" de l'export) : c'est lui qui determine en premier lieu si on est en
        // presence d'une "societe de la liste" (sections 5-9) - un retrait DAB ou un versement
        // especes est fait PAR le titulaire du compte. Meme parametre/convention que
        // LedgerAccountClassifier.Classify.
        //
        // Cas particulier VERSEMENT ESP / RETRAIT ESP / DAB-WIB-GAB-GBH-DBH (sections 7-9) : comme la
        // liste des 44 societes est tres restreinte, la tres grande majorite des comptes Zitouna n'en
        // font pas partie - appliquer le dedoublement (580000/541000) a TOUS les titulaires "hors
        // liste" en ferait le cas par defaut au lieu d'une exception. Le dedoublement reste donc
        // volontairement rare : il ne se declenche que si le titulaire n'est PAS une societe de la
        // liste ET que le libelle de cette transaction cite explicitement une des 44 societes. Dans
        // tous les autres cas (titulaire de la liste, ou aucun signal special du tout), on retombe
        // sur la regle simple normale (validee par l'utilisateur, 2026-09-17).
        public static IReadOnlyList<(string CompteDebit, string CompteCredit)> Classify(Transaction tx, string? customerName)
        {
            if (!tx.Debit.HasValue && !tx.Credit.HasValue)
                return Array.Empty<(string, string)>();

            bool inDebit = tx.Debit.HasValue;
            string libelle = NormalizeForMatch(tx.Libelle);
            bool holderIsSpecialCompany = IsListedCompany(customerName);

            // --- Cas speciaux societes (section 5 a 9) : verifies avant toute regle generique ---

            if (VersementEspPattern.IsMatch(libelle))
            {
                if (holderIsSpecialCompany)
                    // Section 7 : les 2 variantes sont donnees explicitement dans le cahier des charges.
                    return One(inDebit ? (CompteBancaire, CompteClientDivers) : (CompteClientDivers, CompteBancaire));

                if (LibelleMentionsListedCompany(libelle))
                    // Section 8 (titulaire hors liste, mais une des 44 societes est explicitement
                    // citee dans le libelle de cette operation) : cas Debit tel que donne dans le
                    // cahier des charges - dedoublement volontairement rare (voir commentaire de
                    // Classify), declenche uniquement par ce signal textuel precis.
                    return Two(inDebit,
                        (CompteBancaire, CompteIntermediaire),
                        (CompteIntermediaire, CompteCaisse));

                // Aucun signal special : traitement conservateur, comme un versement normal.
                return One(Pair(CompteClientDivers, CompteBancaire, inDebit));
            }

            if (RetraitEspPattern.IsMatch(libelle) || DabWibGabGbhDbhPattern.IsMatch(libelle))
            {
                if (!holderIsSpecialCompany && LibelleMentionsListedCompany(libelle))
                    // Section 9 (meme logique que section 8 ci-dessus) : cas Debit tel que donne.
                    return Two(inDebit,
                        (CompteIntermediaire, CompteBancaire),
                        (CompteCaisse, CompteIntermediaire));

                // Titulaire de la liste, ou aucun signal special : regle DAB/retrait normale.
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));
            }

            if (ContextSocietePattern.IsMatch(libelle) && holderIsSpecialCompany)
                // Section 6 : remplace 461000 par 411000 dans la regle virement/encaissement.
                return One(Pair(CompteClientDivers, CompteBancaire, inDebit));

            // --- Regles generiques, expressions specifiques avant les mots generiques (section 4) ---

            if (TvaDebitDiversPattern.IsMatch(libelle))
                return One(Pair("436600", CompteBancaire, inDebit));

            if (TvaSurComPattern.IsMatch(libelle) || TvaLeasingPattern.IsMatch(libelle) || TvaPattern.IsMatch(libelle))
                return One(Pair("436600", CompteBancaire, inDebit));

            if (DobctPattern.IsMatch(libelle))
                return One(Pair("437001", CompteBancaire, inDebit));

            if (MouvementCommPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (DebitDiversPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            // Prelevement verifie avant Commission (2026-09-17) : la forme reelle "PRELEV COM" /
            // "PRÉLÈV COM" (ex. "Prélèv com/ EPS ...") contient le mot "com", qui matcherait sinon
            // la regle Commission generique (627000) avant meme d'atteindre celle-ci.
            if (RejetPattern.IsMatch(libelle) && PrelevementPattern.IsMatch(libelle))
                // "REJET PRELEV..." (ex. "Rejet prélèv 3315") : aucune regle comptable validee pour
                // ce cas dans le cahier des charges - volontairement laisse sans imputation plutot
                // que de deviner un traitement pour un rejet de prelevement. A confirmer avec
                // l'utilisateur avant d'ajouter une regle.
                return Array.Empty<(string, string)>();

            if (PrelevementPattern.IsMatch(libelle))
                return One(Pair("437001", CompteBancaire, inDebit));

            if (CommissionPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (VirementPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (AgiosPattern.IsMatch(libelle))
                return One(Pair("651500", CompteBancaire, inDebit));

            if (BlocagePattern.IsMatch(libelle))
                return One(Pair("511700", CompteBancaire, inDebit));

            if (DeblocagePattern.IsMatch(libelle))
                return One(Pair(CompteBancaire, "511700", inDebit));

            if (RedressementPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (VersementPattern.IsMatch(libelle))
                return One(Pair(CompteClientDivers, CompteBancaire, inDebit));

            if (InteretDebiteursPattern.IsMatch(libelle))
                return One(Pair("651000", CompteBancaire, inDebit));

            if (InteretCrediteursPattern.IsMatch(libelle))
                return One(Pair(CompteBancaire, "651000", inDebit));

            if (PaiementPrincipalePattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (CotisationCartePattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (AchatAmiraPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (MutuellePattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            return Array.Empty<(string, string)>();
        }

        // Applique la regle generale d'inversion (section 2) : le couple donne represente le cas
        // Debit ; le cas Credit inverse uniquement les 2 comptes.
        private static (string CompteDebit, string CompteCredit) Pair(string compteDebitSiDebit, string compteCreditSiDebit, bool inDebit)
            => inDebit ? (compteDebitSiDebit, compteCreditSiDebit) : (compteCreditSiDebit, compteDebitSiDebit);

        private static IReadOnlyList<(string CompteDebit, string CompteCredit)> One((string CompteDebit, string CompteCredit) line)
            => new[] { line };

        private static IReadOnlyList<(string CompteDebit, string CompteCredit)> Two(bool inDebit,
            (string CompteDebit, string CompteCredit) ligne1SiDebit, (string CompteDebit, string CompteCredit) ligne2SiDebit)
        {
            if (inDebit) return new[] { ligne1SiDebit, ligne2SiDebit };
            return new[]
            {
                (ligne1SiDebit.CompteCredit, ligne1SiDebit.CompteDebit),
                (ligne2SiDebit.CompteCredit, ligne2SiDebit.CompteDebit),
            };
        }

        // --- Detection des societes speciales (section 5-9) : comparee au titulaire du compte
        // (customerName), pas au texte de chaque transaction - voir le commentaire sur Classify(). ---

        private static readonly string[] SpecialCompanies =
        {
            "ROTANA TRAVEL", "ROYAL LUMIERE", "SOFOMEKA", "BLANC PAIN", "MP SHOP",
            "LES 4 FRERES", "WERGHIMMI NEGOCE", "ALBARAKA RENT A CAR", "SOGI TRANSPORT",
            "SOGEPA", "HMAMMED DE CCE", "MARWEN WERGANE", "NEOPEQ",
            "BOULANG PATISSERIE MALL", "DINOS ONS COMPANY", "SYD PEINTURE MODERNE",
            "DARRAGINO", "CAFE HAZAR", "STATION BLEUE", "DAKHLI RENT A CAR",
            "TROIS CENT SOIXANTE DEGRES RENT CAR", "DAHLIA", "LE BOSPHORE",
            "ROYAUME TUNIS VOYAGE TOURSM", "KIDS VALLEY", "BIDAH RENT A CAR",
            "VOLAILLES MARKET", "JSO DISRIBUTION", "LAOUINA VIANDES ET VOLLAILES LVV",
            "SINOFOB", "ALHARRAZI MUSIC", "SLICE TIME",
            "PROMOTION DE L ENSG", "ARYANE RENT A CAR", "ALRIADH POUR DIS ET COM",
            "FOUKA FOOD", "K AND A", "SEA CARGO", "FLEUR DE SUD", "CAFEINNE",
            "LT BAHRI RENT CAR", "HOSNI RENT CAR", "AMAZIGUEN", "FLAVORY",
        };

        private static readonly string[] NormalizedSpecialCompanies =
            Array.ConvertAll(SpecialCompanies, NormalizeForMatch);

        private static bool IsListedCompany(string? customerName)
        {
            string normalizedCustomer = NormalizeForMatch(customerName);
            if (normalizedCustomer.Length == 0) return false;

            foreach (var company in NormalizedSpecialCompanies)
                if (normalizedCustomer.Contains(company) || company.Contains(normalizedCustomer))
                    return true;
            return false;
        }

        // Signal textuel precis (voir commentaire de Classify) : une des 44 societes est citee
        // explicitement dans le libelle de cette transaction, meme si le titulaire du compte est
        // different. Contrairement a IsListedCompany, pas de correspondance inversee (le libelle est
        // une ligne de texte bien plus longue qu'un nom de societe).
        private static bool LibelleMentionsListedCompany(string normalizedLibelle)
        {
            foreach (var company in NormalizedSpecialCompanies)
                if (normalizedLibelle.Contains(company))
                    return true;
            return false;
        }

        // --- Motifs de reconnaissance (appliques a un libelle deja normalise : minuscules, sans
        // accents, espaces multiples reduits a un seul, apostrophes remplacees par un espace). ---

        private static readonly Regex VersementEspPattern = new(@"\bversement\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex RetraitEspPattern = new(@"\bretrait\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex DabWibGabGbhDbhPattern = new(@"\b(dab|wib|gab|gbh|dbh)\b", RegexOptions.Compiled);
        private static readonly Regex ContextSocietePattern = new(@"\b(virement|vir|encaissement|encaiss)\b|\brem\s+commercant\b", RegexOptions.Compiled);

        private static readonly Regex TvaDebitDiversPattern = new(@"\btva\s+debit\s+divers\b", RegexOptions.Compiled);
        // comm? : accepte la forme reelle a 4 lettres "COMM" (ex. "TVA/COMM") en plus de "COM".
        private static readonly Regex TvaSurComPattern = new(@"\btva\s*/\s*comm?\b|\btva\s+sur\s+comm?\b", RegexOptions.Compiled);
        private static readonly Regex TvaLeasingPattern = new(@"\btva\s+leasing\b", RegexOptions.Compiled);
        private static readonly Regex TvaPattern = new(@"\btva\b", RegexOptions.Compiled);
        private static readonly Regex DobctPattern = new(@"\bdobct\s+comptoir\s+de\s+tunis\s+reg\b", RegexOptions.Compiled);
        private static readonly Regex MouvementCommPattern = new(@"\bmouvement\s+comm\b", RegexOptions.Compiled);
        private static readonly Regex DebitDiversPattern = new(@"\bdebit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex CommissionPattern = new(@"\b(com|commission|frais|pdl)\b", RegexOptions.Compiled);
        // prelev (2026-09-17) : forme reelle abregee "PRÉLÈV" / "PRELEV" (ex. "Prélèv com/ EPS ...",
        // "PRELEVEMENT" restant reconnu en plus pour compatibilite avec les libelles deja ecrits en
        // entier).
        private static readonly Regex PrelevementPattern = new(@"\bprelevement\b|\bprelev\b|\bmin\s+de\s+fin\b|\bdeclaration\b", RegexOptions.Compiled);
        // Garde utilisee uniquement pour bloquer "REJET PRELEV..." (voir Classify) - ne bloque aucune
        // autre regle.
        private static readonly Regex RejetPattern = new(@"\brejet\b", RegexOptions.Compiled);
        private static readonly Regex VirementPattern = new(@"\b(virement|vir|effet)\b|\breglement\s+cheque\b|\benc\s+cheque\b|\bencaissement\b|\bencaiss\b", RegexOptions.Compiled);
        private static readonly Regex AgiosPattern = new(@"\bagios?\b", RegexOptions.Compiled);
        private static readonly Regex BlocagePattern = new(@"\bblocage\b", RegexOptions.Compiled);
        private static readonly Regex DeblocagePattern = new(@"\bdeblocage\b", RegexOptions.Compiled);
        private static readonly Regex RedressementPattern = new(@"\bredressement\b", RegexOptions.Compiled);
        private static readonly Regex VersementPattern = new(@"\bversement\b", RegexOptions.Compiled);
        private static readonly Regex InteretDebiteursPattern = new(@"\binteret\s+debiteurs\b|\bcharges\s+d\s+interets?\b", RegexOptions.Compiled);
        private static readonly Regex InteretCrediteursPattern = new(@"\binteret\s+crediteurs\b", RegexOptions.Compiled);
        private static readonly Regex PaiementPrincipalePattern = new(@"\bpaiement\s+principale\b", RegexOptions.Compiled);
        private static readonly Regex CotisationCartePattern = new(@"\bcotisation\s+carte\b", RegexOptions.Compiled);
        private static readonly Regex AchatAmiraPattern = new(@"\bachat\s+amira\b", RegexOptions.Compiled);
        private static readonly Regex MutuellePattern = new(@"\bmutuelle\b", RegexOptions.Compiled);

        // Meme technique que LedgerAccountClassifier.NormalizeForMatch (accents/casse ignores), plus
        // tolerance espaces multiples/apostrophes pour les phrases a plusieurs mots (section 11 du
        // cahier des charges : robustesse OCR).
        private static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue;
                sb.Append(c is '\'' or '’' ? ' ' : c);
            }
            string normalized = sb.ToString().Normalize(NormalizationForm.FormC);
            return Regex.Replace(normalized, @"\s+", " ").Trim();
        }
    }
}
