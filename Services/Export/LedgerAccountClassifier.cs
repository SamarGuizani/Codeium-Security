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

        public static (string? CompteDebit, string? CompteCredit) Classify(Transaction tx, string? customerName = null)
        {
            string libelle = NormalizeForMatch(tx.Libelle);

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
