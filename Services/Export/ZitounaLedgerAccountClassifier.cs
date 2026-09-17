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
        // la case "Societe :" de l'export) : c'est lui qui determine si on est en presence d'une
        // "societe de la liste" (sections 5-9), pas le texte de la transaction elle-meme - un retrait
        // DAB ou un versement especes est fait PAR le titulaire du compte, dont le nom n'apparait pas
        // ligne par ligne dans le libelle. Meme parametre/convention que LedgerAccountClassifier.Classify.
        public static IReadOnlyList<(string CompteDebit, string CompteCredit)> Classify(Transaction tx, string? customerName)
        {
            if (!tx.Debit.HasValue && !tx.Credit.HasValue)
                return Array.Empty<(string, string)>();

            bool inDebit = tx.Debit.HasValue;
            string libelle = NormalizeForMatch(tx.Libelle);
            bool isSpecialCompany = IsListedCompany(customerName);

            // --- Cas speciaux societes (section 5 a 9) : verifies avant toute regle generique ---

            if (VersementEspPattern.IsMatch(libelle))
            {
                if (isSpecialCompany)
                    // Section 7 : les 2 variantes sont donnees explicitement dans le cahier des charges.
                    return One(inDebit ? (CompteBancaire, CompteClientDivers) : (CompteClientDivers, CompteBancaire));

                // Section 8 (hors liste) : cas Debit tel que donne dans le cahier des charges.
                return Two(inDebit,
                    (CompteBancaire, CompteIntermediaire),
                    (CompteIntermediaire, CompteCaisse));
            }

            if (RetraitEspPattern.IsMatch(libelle) || DabWibGabGbhDbhPattern.IsMatch(libelle))
            {
                if (!isSpecialCompany)
                    // Section 9 (hors liste) : cas Debit tel que donne dans le cahier des charges.
                    return Two(inDebit,
                        (CompteIntermediaire, CompteBancaire),
                        (CompteCaisse, CompteIntermediaire));

                // Societe de la liste : pas de regle speciale donnee -> regle DAB/retrait normale.
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));
            }

            if (ContextSocietePattern.IsMatch(libelle) && isSpecialCompany)
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

            if (CommissionPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (PrelevementPattern.IsMatch(libelle))
                return One(Pair("437001", CompteBancaire, inDebit));

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

        // --- Motifs de reconnaissance (appliques a un libelle deja normalise : minuscules, sans
        // accents, espaces multiples reduits a un seul, apostrophes remplacees par un espace). ---

        private static readonly Regex VersementEspPattern = new(@"\bversement\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex RetraitEspPattern = new(@"\bretrait\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex DabWibGabGbhDbhPattern = new(@"\b(dab|wib|gab|gbh|dbh)\b", RegexOptions.Compiled);
        private static readonly Regex ContextSocietePattern = new(@"\b(virement|vir|encaissement|encaiss)\b|\brem\s+commercant\b", RegexOptions.Compiled);

        private static readonly Regex TvaDebitDiversPattern = new(@"\btva\s+debit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex TvaSurComPattern = new(@"\btva\s*/\s*com\b|\btva\s+sur\s+com\b", RegexOptions.Compiled);
        private static readonly Regex TvaLeasingPattern = new(@"\btva\s+leasing\b", RegexOptions.Compiled);
        private static readonly Regex TvaPattern = new(@"\btva\b", RegexOptions.Compiled);
        private static readonly Regex DobctPattern = new(@"\bdobct\s+comptoir\s+de\s+tunis\s+reg\b", RegexOptions.Compiled);
        private static readonly Regex MouvementCommPattern = new(@"\bmouvement\s+comm\b", RegexOptions.Compiled);
        private static readonly Regex DebitDiversPattern = new(@"\bdebit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex CommissionPattern = new(@"\b(com|commission|frais|pdl)\b", RegexOptions.Compiled);
        private static readonly Regex PrelevementPattern = new(@"\bprelevement\b|\bmin\s+de\s+fin\b|\bdeclaration\b", RegexOptions.Compiled);
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
