using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services.Export
{
    // Regles de reconnaissance de libelle -> Compte Debit/Compte Credit COMMUNES A TOUTES LES
    // BANQUES (2026-09-17, demande utilisateur explicite : les regles donnees pour Zitouna portent
    // sur du vocabulaire bancaire tunisien generique - prelevement, commission, TVA, virement,
    // agios, blocage... - et doivent s'appliquer partout, pas seulement aux relevés Zitouna).
    // Ne modifie jamais Transaction.Debit/Credit/Date/Libelle, ne touche jamais au parsing.
    //
    // Utilisee :
    //  - par LedgerAccountClassifier (toutes banques, y compris BIAT/BTL/BNA/QNB/...) UNIQUEMENT EN
    //    COMPLEMENT : appelee seulement quand ses propres regles deja validees ne trouvent rien (case
    //    actuellement vide) - ne remplace ni n'ecrase jamais une regle bancaire deja validee.
    //  - par ZitounaLedgerAccountClassifier, comme repli apres ses propres regles specifiques a
    //    Zitouna (societes de la liste, dedoublement VERSEMENT ESP/RETRAIT ESP...).
    //
    // Regle generale (section 2 du cahier des charges Zitouna, reprise ici car generique a toutes
    // ces regles) : chaque regle est ecrite pour le cas ou le montant est dans la colonne DEBIT. Si
    // le montant est dans la colonne CREDIT, on inverse UNIQUEMENT Compte Debit / Compte Credit
    // (jamais les colonnes de montant).
    public static class AccountingKeywordRules
    {
        private const string CompteBancaire = "532000";
        private const string CompteClientDivers = "411000";
        private const string CompteFournisseurClient = "461000";

        // Expressions specifiques evaluees avant les mots generiques (ex: "TVA DEBIT DIVERS" avant
        // "TVA" et "DEBIT DIVERS" separement, "REJET..." avant "PRELEV"/"EFFET"...). Retourne une
        // liste vide si aucune regle ne correspond (cellules laissees vides).
        public static IReadOnlyList<(string CompteDebit, string CompteCredit)> ClassifyByKeyword(Transaction tx)
        {
            if (!tx.Debit.HasValue && !tx.Credit.HasValue)
                return Array.Empty<(string, string)>();

            bool inDebit = tx.Debit.HasValue;
            string libelle = NormalizeForMatch(tx.Libelle);

            if (TvaDebitDiversPattern.IsMatch(libelle))
                return One(Pair("436600", CompteBancaire, inDebit));

            if (TvaLeasingPattern.IsMatch(libelle))
                return One(Pair("436600", CompteBancaire, inDebit));

            // COM vs TVA (2026-09-17, precision utilisateur : "si tu trouve le 1er mot com toujours
            // commission 627000, si tu trouve le 1er mot tva c'est un tva 436600") : c'est le mot
            // rencontre EN PREMIER dans le libelle qui tranche, pas simplement la presence de "tva"
            // quelque part - couvre a la fois les formes ou TVA est en tete ("TVA/COMM", "TVA SUR
            // COM(M)", "TVA sur Commission", "TVA" seule) ET le cas signale ou COM est en tete
            // ("COM TVA...", "Com VirE...").
            {
                var comMatch = ComFamillePattern.Match(libelle);
                var tvaMatch = TvaPattern.Match(libelle);
                if (comMatch.Success && (!tvaMatch.Success || comMatch.Index < tvaMatch.Index))
                    return One(Pair("627000", CompteBancaire, inDebit));
                if (tvaMatch.Success)
                    return One(Pair("436600", CompteBancaire, inDebit));
            }

            if (DobctPattern.IsMatch(libelle))
                return One(Pair("437001", CompteBancaire, inDebit));

            if (MouvementCommPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (DebitDiversPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (CreditDiversPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            // Retrait especes / DAB-WIB-GAB-GBH-DBH : regle "normale" (sans acces a la liste des
            // societes speciales Zitouna, geree separement par ZitounaLedgerAccountClassifier avant
            // d'atteindre ce repli) - toujours 461000/532000 ici.
            if (RetraitEspOuDabPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            // Rejet (prelevement/effet/precompte...) : regle comptable validee par l'utilisateur
            // (2026-09-17) - avant, ce cas etait volontairement laisse sans imputation en l'absence
            // de regle confirmee ; desormais classe en frais bancaires (bucket Commission), avant
            // Prelevement/Virement pour ne pas etre intercepte par "effet"/"prelev".
            if (RejetPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            // "Regl prelev" (2026-09-17) : le meme libelle designe tantot le montant principal
            // (regle "Virement/operations courantes", 461000), tantot la ligne de frais qui
            // l'accompagne (regle "Commissions et frais", 627000) - aucun signal textuel ne les
            // distingue (meme libelle, meme colonne dans les releves reels). Disambiguation par
            // seuil de montant (choix pragmatique valide par l'utilisateur) : un petit montant
            // (< 100, ordre de grandeur d'un frais bancaire) est traite comme frais, le reste comme
            // le montant principal.
            if (ReglPrelevPattern.IsMatch(libelle))
            {
                decimal montant = tx.Debit ?? tx.Credit ?? 0m;
                return montant < ReglPrelevSeuilFrais
                    ? One(Pair("627000", CompteBancaire, inDebit))
                    : One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));
            }

            // "Remb anticipe" (2026-09-17) : meme libelle utilise pour la ligne de frais (Debit,
            // regle "Commissions et frais", 627000) et pour le montant principal recu (Credit, regle
            // "Donneur d'ordre et encaissements", sens inverse 532000/461000) - ici la colonne est le
            // seul signal disponible (contrairement au seuil de montant ci-dessus, le libelle ne
            // permet aucune autre distinction). Verifiee avant BlocRembAnticPattern pour ne pas
            // capturer "Bloc/Remb antic" (regle fixe distincte, voir plus bas).
            if (BlocRembAnticPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (RembAnticipePattern.IsMatch(libelle))
                return inDebit
                    ? One(("627000", CompteBancaire))
                    : One((CompteBancaire, CompteFournisseurClient));

            // 437001 reserve au precompte "MIN DES FINANCES" / "DECLARATION" (2026-09-17, precision
            // utilisateur) - un "prelevement"/"prelev" isole (ex. "Prélèv com/ EPS...") n'est plus un
            // declencheur de 437001 : il retombe sur la regle Commission generique juste en dessous
            // (le mot "com" y est deja reconnu), ou sinon sur PrelevementBarePattern (461000) plus
            // bas si aucun mot-cle Commission n'est present.
            if (PrelevementMinFinancesPattern.IsMatch(libelle))
                return One(Pair("437001", CompteBancaire, inDebit));

            if (CommissionPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            // "Prelevement"/"prelev" isole, sans "com" ni "min de finance(s)"/"declaration"
            // (2026-09-17, precision utilisateur : "si tu trouve seulement prelevement tu mets
            // 461000") - regle Virement/operations courantes par defaut.
            if (PrelevementBarePattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            // "Donneur d'ordre" (2026-09-17) : encaissement, sens naturellement inverse (le montant
            // est en Credit dans la quasi-totalite des cas reels) - meme inversion generale que les
            // autres regles (base 461000/532000, inversee en 532000/461000 quand le montant est en
            // Credit).
            if (DonneurDOrdrePattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (MgEngSignatPattern.IsMatch(libelle))
                return One(Pair("627000", CompteBancaire, inDebit));

            if (ReglImpayePattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            if (CertifChegPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            // Motifs anglais (2026-09-17) : formats de releve en anglais, meme logique de priorite
            // TVA > Commission generique que les motifs francais.
            if (TaxOnChargesPattern.IsMatch(libelle))
                return One(Pair("436600", CompteBancaire, inDebit));

            if (ChgsStOrdrRejectnPattern.IsMatch(libelle) || ChargesFeesPattern.IsMatch(libelle))
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

            // "Interet(s) de retard" (2026-09-17, precision utilisateur : phrase complete, pas
            // "interet" seul ni "retard" seul) : 461000, verifiee avant le bucket generique
            // "interets" (651000) ci-dessous pour ne pas y tomber a la place.
            if (InteretDeRetardPattern.IsMatch(libelle))
                return One(Pair(CompteFournisseurClient, CompteBancaire, inDebit));

            // Interets, forme generique (2026-09-17) : couvre notamment "Intérêts créd/déb" (forme
            // combinee/abregee reelle, precedemment laissee volontairement sans imputation faute de
            // regle validee) - l'utilisateur a depuis valide un compte unique, sans distinction
            // debiteur/crediteur. Verifiee apres les regles specifiques ci-dessus : un libelle qui
            // precise explicitement "debiteurs" ou "crediteurs" garde son compte specifique.
            if (InteretsGeneriquePattern.IsMatch(libelle))
                return One(Pair("651000", CompteBancaire, inDebit));

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

        // Applique la regle generale d'inversion : le couple donne represente le cas Debit ; le cas
        // Credit inverse uniquement les 2 comptes.
        internal static (string CompteDebit, string CompteCredit) Pair(string compteDebitSiDebit, string compteCreditSiDebit, bool inDebit)
            => inDebit ? (compteDebitSiDebit, compteCreditSiDebit) : (compteCreditSiDebit, compteDebitSiDebit);

        internal static IReadOnlyList<(string CompteDebit, string CompteCredit)> One((string CompteDebit, string CompteCredit) line)
            => new[] { line };

        private static readonly Regex TvaDebitDiversPattern = new(@"\btva\s+debit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex TvaLeasingPattern = new(@"\btva\s+leasing\b", RegexOptions.Compiled);
        private static readonly Regex TvaPattern = new(@"\btva\b", RegexOptions.Compiled);
        // "com"/"comm"/"commission" (2026-09-17) : utilise pour la comparaison de position avec
        // TvaPattern ci-dessus (voir ClassifyByKeyword) - "frais"/"pdl" restent geres par
        // CommissionPattern plus bas, non concernes par cette comparaison.
        private static readonly Regex ComFamillePattern = new(@"\b(com|comm|commission)\b", RegexOptions.Compiled);
        private static readonly Regex DobctPattern = new(@"\bdobct\s+comptoir\s+de\s+tunis\s+reg\b", RegexOptions.Compiled);
        private static readonly Regex MouvementCommPattern = new(@"\bmouvement\s+comm\b", RegexOptions.Compiled);
        private static readonly Regex DebitDiversPattern = new(@"\bdebit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex CreditDiversPattern = new(@"\bcredit\s+divers\b", RegexOptions.Compiled);
        private static readonly Regex RetraitEspOuDabPattern = new(@"\bretrait\s+esp\b|\b(dab|wib|gab|gbh|dbh)\b", RegexOptions.Compiled);
        // com|comm (2026-09-17) : "COMM" (4 lettres) est la forme la plus frequente sur les vrais
        // releves (ex. "COMM VIREMENT RECU EN TND", "COMM REGLEMENT EFFET") - sans ce complement ces
        // lignes tombaient par accident dans une autre regle (a cause du mot "virement"/"effet"
        // present dans le meme libelle) ou restaient vides.
        private static readonly Regex CommissionPattern = new(@"\b(com|comm|commission|frais|pdl)\b", RegexOptions.Compiled);
        // min de(s) fin... / declaration (2026-09-17, precision utilisateur) : seuls declencheurs de
        // 437001 - couvre "MIN DE FIN", "MIN DES FINANCES", "DECLARATION" (le "PRELEVEMENT"/
        // "PAIEMENT" qui les accompagne n'est pas requis explicitement, ces phrases etant deja tres
        // specifiques a elles seules).
        private static readonly Regex PrelevementMinFinancesPattern = new(@"\bmin\s+des?\s+fin\w*\b|\bdeclaration\b", RegexOptions.Compiled);
        // Prelevement isole (2026-09-17, precision utilisateur) : declencheur de repli 461000 quand
        // ni "com" ni "min de finance(s)"/"declaration" ne sont presents (voir ClassifyByKeyword).
        private static readonly Regex PrelevementBarePattern = new(@"\bprelevement\b|\bprelev\b", RegexOptions.Compiled);
        // "Interet(s) de retard" : phrase complete, distincte du bucket generique "interets" plus bas.
        private static readonly Regex InteretDeRetardPattern = new(@"\binterets?\s+de\s+retard\b", RegexOptions.Compiled);
        private static readonly Regex RejetPattern = new(@"\brejet\b", RegexOptions.Compiled);
        private static readonly Regex VirementPattern = new(@"\b(virement|vir|effet)\b|\breglement\s+cheque\b|\benc\s+cheque\b|\bencaissement\b|\bencaiss\b", RegexOptions.Compiled);
        private static readonly Regex AgiosPattern = new(@"\bagios?\b", RegexOptions.Compiled);
        private static readonly Regex BlocagePattern = new(@"\bblocage\b", RegexOptions.Compiled);
        private static readonly Regex DeblocagePattern = new(@"\bdeblocage\b", RegexOptions.Compiled);
        private static readonly Regex RedressementPattern = new(@"\bredressement\b", RegexOptions.Compiled);
        private static readonly Regex VersementPattern = new(@"\bversement\b", RegexOptions.Compiled);
        private static readonly Regex InteretDebiteursPattern = new(@"\binteret\s+debiteurs\b|\bcharges\s+d\s+interets?\b", RegexOptions.Compiled);
        private static readonly Regex InteretCrediteursPattern = new(@"\binteret\s+crediteurs\b", RegexOptions.Compiled);
        private static readonly Regex InteretsGeneriquePattern = new(@"\binterets?\b", RegexOptions.Compiled);
        // \s* (au lieu de \s+) + principale? (2026-09-17, precision utilisateur : "tous paiement
        // principale", pas seulement la forme exacte avec espace) : couvre aussi la forme reelle
        // accolee "PAIEMENTPRINCIPAL" (sans espace, sans e final).
        private static readonly Regex PaiementPrincipalePattern = new(@"\bpaiement\s*principale?\b", RegexOptions.Compiled);
        private static readonly Regex CotisationCartePattern = new(@"\bcotisation\s+carte\b", RegexOptions.Compiled);
        private static readonly Regex AchatAmiraPattern = new(@"\bachat\s+amira\b", RegexOptions.Compiled);
        private static readonly Regex MutuellePattern = new(@"\bmutuelle\b", RegexOptions.Compiled);

        // --- Motifs ajoutes le 2026-09-17 (demande utilisateur : regles valables pour tous les
        // formats bancaires). ---
        private static readonly Regex ReglPrelevPattern = new(@"\bregl\w*\s+prelev", RegexOptions.Compiled);
        private const decimal ReglPrelevSeuilFrais = 100m;
        // "bloc/remb antic..." verifie avant le motif generique "remb antic..." (dedoublement de
        // role Debit/Credit) pour que ce cas reste une regle fixe distincte.
        private static readonly Regex BlocRembAnticPattern = new(@"\bbloc\s*/?\s*remb\w*\s+antic", RegexOptions.Compiled);
        private static readonly Regex RembAnticipePattern = new(@"\bremb\w*\s+antic", RegexOptions.Compiled);
        private static readonly Regex DonneurDOrdrePattern = new(@"\bdonneur\s+d\s+(ordre|order)\b", RegexOptions.Compiled);
        private static readonly Regex MgEngSignatPattern = new(@"\bmg\s*/\s*eng\s*/\s*signat", RegexOptions.Compiled);
        private static readonly Regex ReglImpayePattern = new(@"\bregl\w*\s+impay", RegexOptions.Compiled);
        private static readonly Regex CertifChegPattern = new(@"\bcertif\s+cheg?\b", RegexOptions.Compiled);
        // Motifs anglais.
        private static readonly Regex TaxOnChargesPattern = new(@"\btax\s+on\s+charges\b", RegexOptions.Compiled);
        private static readonly Regex ChargesFeesPattern = new(@"\bcharges\s*/\s*fees\b", RegexOptions.Compiled);
        private static readonly Regex ChgsStOrdrRejectnPattern = new(@"\bchgs\b.*\breject", RegexOptions.Compiled);

        // Meme technique que LedgerAccountClassifier.NormalizeForMatch (accents/casse ignores), plus
        // tolerance espaces multiples/apostrophes pour les phrases a plusieurs mots (robustesse OCR).
        internal static string NormalizeForMatch(string? value)
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
