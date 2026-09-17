using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services.Export
{
    // Regles metier BANQUE ZITOUNA (cahier des charges utilisateur, 2026-09-17) pour pre-remplir
    // Compte Debit / Compte Credit dans l'export Excel (voir BankExcelExporter). Ne modifie jamais
    // Transaction.Debit/Transaction.Credit/Date/Libelle, ne touche jamais au parsing
    // (BankDocumentParser).
    //
    // Les regles de reconnaissance de libelle communes a toutes les banques (TVA, commission,
    // prelevement, virement, agios...) vivent desormais dans AccountingKeywordRules (2026-09-17,
    // demande utilisateur explicite : ces regles s'appliquent a toutes les banques, pas seulement
    // Zitouna) - cette classe ne garde que ce qui est specifique a Zitouna : la liste des 44 societes
    // speciales et le dedoublement comptable VERSEMENT ESP/RETRAIT ESP qui en depend.
    //
    // Regle generale (section 2 du cahier des charges) : chaque regle est ecrite pour le cas ou le
    // montant est dans la colonne DEBIT. Si le montant est dans la colonne CREDIT, on inverse
    // UNIQUEMENT Compte Debit / Compte Credit (jamais les colonnes de montant). Cela vaut aussi pour
    // les cas a 2 lignes (VERSEMENT ESP / RETRAIT ESP hors liste, section 8/9) : le texte du cahier
    // des charges donne le cas Debit, le cas Credit s'obtient en inversant les 2 comptes de chaque
    // ligne (confirme par l'utilisateur - pas de variante "cote naturel" separee).
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
        // sur AccountingKeywordRules (regle simple normale, validee par l'utilisateur, 2026-09-17).
        public static IReadOnlyList<(string CompteDebit, string CompteCredit)> Classify(Transaction tx, string? customerName)
        {
            if (!tx.Debit.HasValue && !tx.Credit.HasValue)
                return Array.Empty<(string, string)>();

            bool inDebit = tx.Debit.HasValue;
            string libelle = AccountingKeywordRules.NormalizeForMatch(tx.Libelle);
            bool holderIsSpecialCompany = IsListedCompany(customerName);

            // --- Cas speciaux societes (section 5 a 9), specifiques a Zitouna : verifies avant tout
            // repli sur les regles generiques communes a toutes les banques ---

            if (VersementEspPattern.IsMatch(libelle))
            {
                if (holderIsSpecialCompany)
                    // Section 7 : les 2 variantes sont donnees explicitement dans le cahier des charges.
                    return AccountingKeywordRules.One(inDebit ? (CompteBancaire, CompteClientDivers) : (CompteClientDivers, CompteBancaire));

                if (LibelleMentionsListedCompany(libelle))
                    // Section 8 (titulaire hors liste, mais une des 44 societes est explicitement
                    // citee dans le libelle de cette operation) : cas Debit tel que donne dans le
                    // cahier des charges - dedoublement volontairement rare (voir commentaire de
                    // Classify), declenche uniquement par ce signal textuel precis.
                    return Two(inDebit,
                        (CompteBancaire, CompteIntermediaire),
                        (CompteIntermediaire, CompteCaisse));

                // Aucun signal special : repli sur la regle "versement" generique (411000/532000).
            }
            else if (RetraitEspPattern.IsMatch(libelle) || DabWibGabGbhDbhPattern.IsMatch(libelle))
            {
                if (!holderIsSpecialCompany && LibelleMentionsListedCompany(libelle))
                    // Section 9 (meme logique que section 8 ci-dessus) : cas Debit tel que donne.
                    return Two(inDebit,
                        (CompteIntermediaire, CompteBancaire),
                        (CompteCaisse, CompteIntermediaire));

                // Titulaire de la liste, ou aucun signal special : repli sur la regle DAB/retrait
                // normale generique (461000/532000, voir AccountingKeywordRules).
            }
            else if (ContextSocietePattern.IsMatch(libelle) && holderIsSpecialCompany)
            {
                // Section 6 : remplace 461000 par 411000 dans la regle virement/encaissement.
                return AccountingKeywordRules.One(AccountingKeywordRules.Pair(CompteClientDivers, CompteBancaire, inDebit));
            }

            // --- Repli sur les regles generiques communes a toutes les banques ---
            return AccountingKeywordRules.ClassifyByKeyword(tx);
        }

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
            Array.ConvertAll(SpecialCompanies, AccountingKeywordRules.NormalizeForMatch);

        private static bool IsListedCompany(string? customerName)
        {
            string normalizedCustomer = AccountingKeywordRules.NormalizeForMatch(customerName);
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

        // --- Motifs specifiques a Zitouna (appliques a un libelle deja normalise). ---

        private static readonly Regex VersementEspPattern = new(@"\bversement\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex RetraitEspPattern = new(@"\bretrait\s+esp\b", RegexOptions.Compiled);
        private static readonly Regex DabWibGabGbhDbhPattern = new(@"\b(dab|wib|gab|gbh|dbh)\b", RegexOptions.Compiled);
        private static readonly Regex ContextSocietePattern = new(@"\b(virement|vir|encaissement|encaiss)\b|\brem\s+commercant\b", RegexOptions.Compiled);
    }
}
