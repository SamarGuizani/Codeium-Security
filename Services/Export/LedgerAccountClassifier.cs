using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services.Export
{
    // Regles metier donnees par l'utilisateur (2026-09-02) pour pre-remplir Compte Debit /
    // Compte Credit dans l'export Excel (voir BankExcelExporter). Cote Debit et cote Credit
    // sont mutuellement exclusifs (une ligne de releve n'a jamais les deux) : on applique les
    // regles Debit si Transaction.Debit est rempli, sinon les regles Credit si Transaction.Credit
    // est rempli. Si aucun mot-cle ne correspond au Libelle, on ne remplit rien, meme si la case
    // (debit ou credit) est remplie (consigne explicite de l'utilisateur).
    public static class LedgerAccountClassifier
    {
        private const string CompteBancaire = "532000";

        private static readonly Regex CommissionPattern = new(@"\b(comm|commission|frais|rem)\b", RegexOptions.Compiled);
        private static readonly Regex TvaPattern = new(@"\btva\b", RegexOptions.Compiled);
        private static readonly Regex PrelevementPattern = new(@"\b(paiement|prelevement|min|minimum)\b", RegexOptions.Compiled);
        private static readonly Regex EncaissementPattern = new(@"\b(commercant|virement|encaissement|redressement)\b", RegexOptions.Compiled);
        private static readonly Regex BlocagePattern = new(@"\b(blocage|deblocage)\b", RegexOptions.Compiled);
        private static readonly Regex RetraitPattern = new(@"\bretrait\b", RegexOptions.Compiled);
        private static readonly Regex EspecesPattern = new(@"\bespece(s)?\b", RegexOptions.Compiled);
        private static readonly Regex VirementDebitPattern = new(@"\b(vir|virement)\b", RegexOptions.Compiled);
        private static readonly Regex ReglementPattern = new(@"\breglement\b", RegexOptions.Compiled);
        private static readonly Regex ChequePattern = new(@"\bcheque\b", RegexOptions.Compiled);

        public const string CompteIntermediaire = "580000";
        public const string CompteCaisse = "541000";

        // Liste fournie par l'utilisateur (2026-09-02) : quand la Societe du document (voir
        // DocumentMetadata.CustomerName) correspond a l'une de ces raisons sociales, la regle
        // encaissement/virement/commercant ci-dessous ecrit 411000 au lieu de 461000 en Compte
        // Credit. Comparaison normalisee (accents/casse ignores) et tolerante aux suffixes/
        // prefixes (SARL, etc.) via une correspondance partielle dans les deux sens.
        private static readonly string[] ClientCompanies =
        {
            "ROTANA TRAVEL", "ROYAL LUMIERE", "SOFOMEKA", "BLANC PAIN", "MP SHOP",
            "LES 4 FRERES", "WERGHIMMI NEGOCE", "ALBARAKA RENT A CAR", "SOGI TRANSPORT",
            "SOGEPA", "HMAMMED DE CCE", "MARWEN WERGANE", "NEOPEQ",
            "BOULANG PATISSERIE MALL", "DINOS ONS COMPANY", "SYD PEINTURE MODERNE",
            "DARRAGINO", "CAFE HAZAR", "STATION BLEUE", "DAKHLI RENT A CAR",
            "TROIS CENT SOIXANTE DEGRES RENT CAR", "DAHLIA", "LE BOSPHORE",
            "ROYAUME TUNIS VOYAGE TOURSM", "KIDS VALLEY", "BIDAH RENT A CAR",
            "VOLAILLES MARKET", "JSO DISRIBUTION", "LAOUINA VIANDES ET VOLLAILES LVV",
            "SINOFOB", "ALHARRAZI MUSICALHARRAZI MUSIC", "SLICE TIME",
            "PROMOTION DE L ENSG", "ARYANE RENT A CAR", "ALRIADH POUR DIS ET COM",
            "FOUKA FOOD", "K AND A", "SEA CARGO", "FLEUR DE SUD", "CAFEINNE",
            "T BAHRI RENT A CAR", "HOSNI RENT CAR", "AMAZIGUEN", "FLAVORY",
        };

        private static readonly string[] NormalizedClientCompanies =
            Array.ConvertAll(ClientCompanies, NormalizeForMatch);

        // Retrait especes (2026-09-02) : quand le Libelle contient "retrait" et "espece(s)" et
        // que le montant est en Debit (pas en Credit), la ligne doit etre dediee au dedoublement
        // comptable via un compte intermediaire (voir BankExcelExporter) plutot que passer par
        // Classify - deux lignes sont ecrites avec les comptes inverses entre elles.
        public static bool IsRetraitEspeces(Transaction tx)
        {
            if (!tx.Debit.HasValue) return false;
            string libelle = NormalizeForMatch(tx.Libelle);
            return RetraitPattern.IsMatch(libelle) && EspecesPattern.IsMatch(libelle);
        }

        // Tax on Charges (EN, 2026-09-17) : pas d'equivalent "com" a departager, prime directement
        // sur CommissionPattern ci-dessous.
        private static readonly Regex TaxOnChargesPattern = new(@"\btax\s+on\s+charges\b", RegexOptions.Compiled);
        // "Paiement par Carte" (2026-09-17) : doit primer sur PrelevementPattern ci-dessous, dont le
        // mot generique "paiement" matcherait sinon en premier (437001 au lieu de 461000).
        private static readonly Regex PaiementParCartePattern = new(@"\bpaiement\s+par\s+carte\b", RegexOptions.Compiled);
        // "Paiement principale" (2026-09-17, precision utilisateur : toutes les variantes, pas
        // seulement la forme exacte avec espace - couvre aussi "PAIEMENTPRINCIPAL" accole) : doit
        // primer sur PrelevementPattern ci-dessous (mot generique "paiement").
        private static readonly Regex PaiementPrincipaleCompletePattern = new(@"\bpaiement\s*principale?\b", RegexOptions.Compiled);
        // 437001 reserve a "MIN DE(S) FIN(ANCES)"/"DECLARATION" (2026-09-17, precision utilisateur) -
        // doit primer sur PrelevementPattern ci-dessous, qui donnerait sinon 437001 pour n'importe
        // quel "prelevement"/"paiement"/"min" isole.
        private static readonly Regex PrelevementMinFinDeclarationPattern = new(@"\bmin\s+des?\s+fin\w*\b|\bdeclaration\b", RegexOptions.Compiled);
        // "com"/"comm"/"commission" (2026-09-17) : utilise pour la comparaison de position avec
        // TvaPattern (voir Classify) - "frais"/"pdl" geres separement par FraisPdlPattern, non
        // concernes par cette comparaison.
        private static readonly Regex ComFamillePattern = new(@"\b(com|comm|commission)\b", RegexOptions.Compiled);
        private static readonly Regex FraisPdlPattern = new(@"\b(frais|pdl)\b", RegexOptions.Compiled);
        // Rejet / "Regl prelev" (2026-09-17) : verifies avant PrelevementBarePattern ci-dessous pour
        // que "Rejet prélèv..." (627000, regle deja validee) et "Règl prélèv..." (dedoublement par
        // seuil de montant, deja gere par le repli AccountingKeywordRules) ne soient pas intercepts
        // par le nouveau repli 461000 de PrelevementBarePattern avant de les atteindre.
        private static readonly Regex RejetPattern = new(@"\brejet\b", RegexOptions.Compiled);
        private static readonly Regex ReglPrelevPattern = new(@"\bregl\w*\s+prelev", RegexOptions.Compiled);
        // "Prelevement"/"prelev"/"paiement"/"min"/"minimum" isoles, sans "com" ni "min de
        // finance(s)"/"declaration" (2026-09-17, precision utilisateur : "437001 reserve
        // UNIQUEMENT a MIN DE(S) FIN(ANCES)/DECLARATION" + "si tu trouve seulement prelevement tu
        // mets 461000") : doit primer sur PrelevementPattern historique ci-dessous, qui donnerait
        // sinon 437001 pour n'importe quel "paiement"/"min"/"minimum" isole (pas seulement
        // "prelevement") - couvre ainsi tous les cas que PrelevementPattern historique matchait.
        private static readonly Regex PrelevementBarePattern = new(@"\bprelevement\b|\bprelev\b|\bpaiement\b|\bmin\b|\bminimum\b", RegexOptions.Compiled);

        public static (string? CompteDebit, string? CompteCredit) Classify(Transaction tx, string? customerName = null)
        {
            string libelle = NormalizeForMatch(tx.Libelle);
            bool inDebit = tx.Debit.HasValue;

            if (!inDebit && !tx.Credit.HasValue)
                return (null, null);

            if (TaxOnChargesPattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("436600", CompteBancaire, inDebit);

            if (PrelevementMinFinDeclarationPattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("437001", CompteBancaire, inDebit);

            if (PaiementPrincipaleCompletePattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("461000", CompteBancaire, inDebit);

            if (PaiementParCartePattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("461000", CompteBancaire, inDebit);

            // COM vs TVA (2026-09-17, precision utilisateur : "si tu trouve le 1er mot com toujours
            // commission 627000, si tu trouve le 1er mot tva c'est un tva 436600") : c'est le mot
            // rencontre EN PREMIER dans le libelle qui tranche, pas simplement la presence de "tva"
            // quelque part dans le texte.
            {
                var comMatch = ComFamillePattern.Match(libelle);
                var tvaMatch = TvaPattern.Match(libelle);
                if (comMatch.Success && (!tvaMatch.Success || comMatch.Index < tvaMatch.Index))
                    return AccountingKeywordRules.Pair("627000", CompteBancaire, inDebit);
                if (tvaMatch.Success)
                    return AccountingKeywordRules.Pair("436600", CompteBancaire, inDebit);
            }

            if (FraisPdlPattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("627000", CompteBancaire, inDebit);

            // "Rejet prélèv..." / "Règl prélèv..." : ces cas ont deja leur propre logique correcte
            // dans le repli AccountingKeywordRules (627000 fixe pour Rejet, seuil de montant pour
            // Regl prelev) - on y delegue directement plutot que de la dupliquer ici, pour eviter
            // qu'ils ne soient interceptes par PrelevementBarePattern ci-dessous.
            if (RejetPattern.IsMatch(libelle) || ReglPrelevPattern.IsMatch(libelle))
                return FallbackToKeywordRules(tx);

            if (PrelevementBarePattern.IsMatch(libelle))
                return AccountingKeywordRules.Pair("461000", CompteBancaire, inDebit);

            if (tx.Debit.HasValue)
            {
                if (CommissionPattern.IsMatch(libelle))
                    return ("627000", CompteBancaire);

                if (TvaPattern.IsMatch(libelle))
                    return ("436600", CompteBancaire);

                if (PrelevementPattern.IsMatch(libelle))
                    return ("437001", CompteBancaire);

                if (VirementDebitPattern.IsMatch(libelle) || (ReglementPattern.IsMatch(libelle) && ChequePattern.IsMatch(libelle)) || BlocagePattern.IsMatch(libelle))
                    return ("461000", CompteBancaire);

                return FallbackToKeywordRules(tx);
            }

            if (tx.Credit.HasValue)
            {
                if (CommissionPattern.IsMatch(libelle))
                    return (CompteBancaire, "627000");

                if (EncaissementPattern.IsMatch(libelle))
                {
                    string compteCredit = IsClientCompany(customerName) ? "411000" : "461000";
                    return (CompteBancaire, compteCredit);
                }

                return FallbackToKeywordRules(tx);
            }

            return (null, null);
        }

        // Repli (2026-09-17, demande utilisateur explicite : "les regles que j'ai donner [s'appliquent]
        // pour toutes les banques") sur AccountingKeywordRules (TVA, prelevement, commission,
        // virement, agios, blocage... - vocabulaire bancaire tunisien generique). Les regles
        // ci-dessus restent inchangees et prioritaires (deja validees pour BIAT/BTL/BNA/QNB/...) : ce
        // repli ne s'applique QUE quand elles ne trouvent rien (case actuellement vide) - ne remplace
        // jamais un resultat deja produit par les regles ci-dessus.
        private static (string? CompteDebit, string? CompteCredit) FallbackToKeywordRules(Transaction tx)
        {
            var lignes = AccountingKeywordRules.ClassifyByKeyword(tx);
            return lignes.Count > 0 ? (lignes[0].CompteDebit, lignes[0].CompteCredit) : (null, null);
        }

        private static bool IsClientCompany(string? customerName)
        {
            string normalizedCustomer = NormalizeForMatch(customerName);
            if (normalizedCustomer.Length == 0) return false;

            foreach (var company in NormalizedClientCompanies)
            {
                if (normalizedCustomer.Contains(company) || company.Contains(normalizedCustomer))
                    return true;
            }
            return false;
        }

        private static string NormalizeForMatch(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";

            string decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
