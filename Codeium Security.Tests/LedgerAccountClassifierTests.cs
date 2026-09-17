using Codeium_Security.Models;
using Codeium_Security.Services.Export;
using Xunit;

namespace Codeium_Security.Tests
{
    // Verifie LedgerAccountClassifier (toutes banques, y compris BIAT/BTL/BNA/QNB/...) isolement.
    // Couvre en particulier le repli sur AccountingKeywordRules (2026-09-17, demande utilisateur :
    // "les regles que j'ai donner [s'appliquent] pour toutes les banques") : ce repli ne doit
    // jamais ecraser une regle deja validee, et ne se declenche que quand Classify() aurait
    // sinon renvoye (null, null).
    public class LedgerAccountClassifierTests
    {
        private static Transaction DebitTx(string libelle, decimal montant = 100m)
            => new Transaction { Date = "01/01/2026", Libelle = libelle, Debit = montant, Credit = null };

        private static Transaction CreditTx(string libelle, decimal montant = 100m)
            => new Transaction { Date = "01/01/2026", Libelle = libelle, Debit = null, Credit = montant };

        // --- Non-regression : les regles existantes, deja validees, restent inchangees -----------

        [Fact]
        public void Commission_Debit_Toujours_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("COMMISSION"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Encaissement_Credit_Societe_Client_Toujours_411000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(CreditTx("ENCAISSEMENT"), "ROTANA TRAVEL");
            Assert.Equal("532000", cd);
            Assert.Equal("411000", cc);
        }

        [Fact]
        public void Libelle_Sans_MotCle_Generique_Ni_Complementaire_Reste_Vide()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("OPERATION DIVERSE XYZ"));
            Assert.Null(cd);
            Assert.Null(cc);
        }

        // --- Repli AccountingKeywordRules : cas reel BNA (releve.pdf, 2026-09-17) ------------------
        // Ces libelles ne matchent aucune regle generique existante (CommissionPattern generique
        // exige "comm"/"commission"/"frais"/"rem", PrelevementPattern generique exige un mot entier
        // "prelevement") -> avant le repli, ils restaient vides ; avec le repli, ils utilisent
        // AccountingKeywordRules.

        // "Prélèv com/..." (2026-09-17, correction utilisateur) : commission (627000), pas
        // prelevement (437001) - remplace la version precedente de ce test.
        [Fact]
        public void PrelevAbrege_Avec_Com_Donne_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Prélèv com/ EPS 1202607019539508"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // 437001 desormais reserve au precompte "MIN DES FINANCES" (2026-09-17, correction
        // utilisateur).
        [Fact]
        public void PrelevementMinFinances_Utilise_Le_Repli_437001_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("PAIEMENT FT24023NCSGJ PRELEVEMENT MIN DES FINANCES"));
            Assert.Equal("437001", cd);
            Assert.Equal("532000", cc);
        }

        // Regle comptable validee par l'utilisateur le 2026-09-17 (remplace le test precedent qui
        // verifiait l'absence d'imputation, a l'epoque en attente de confirmation).
        [Theory]
        [InlineData("Rejet prélév 2")]
        [InlineData("Rejet effet 009383399275")]
        public void Rejet_Donne_627000_532000(string libelle)
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx(libelle));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // "Certif cheg ..." (2026-09-17) : regle comptable validee ("Virement/operations courantes,
        // sens normal") - remplace le test precedent qui verifiait l'absence d'imputation.
        [Fact]
        public void CertifCheg_Donne_461000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Certif cheg 8868150"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void InteretsCredDeb_Combine_Donne_651000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Intéréts créd/déb TEG14.4060"));
            Assert.Equal("651000", cd);
            Assert.Equal("532000", cc);
        }

        // "Com / decision ..." : "com" est un mot-cle Commission generique deja reconnu par
        // AccountingKeywordRules (pas une regle nouvelle specifique).
        [Fact]
        public void ComDecision_Utilise_Le_Repli_Commission_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Com / décision 0102607022495021"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Regles a priorite absolue (2026-09-17) : doivent primer sur les regles historiques
        // ci-dessus (CommissionPattern/PrelevementPattern), verifiees en tete de LedgerAccountClassifier.Classify --

        // TVA, y compris "TVA sur COM(M)"/"TVA/COMM"/"Tax on Charges" (2026-09-17, confirme par
        // l'utilisateur apres une correction intermediaire vers 627000, revenue en arriere) : reste
        // 436600, prime sur CommissionPattern generique.
        [Theory]
        [InlineData("Tva sur Commission 202608240011|2026-08-26 8808")]
        [InlineData("TVA/COMM 6752")]
        [InlineData("TVA CHG2401243846")]
        [InlineData("Tax on Charges 12345")]
        public void TvaFamille_Prime_Sur_Commission_Generique_436600_532000(string libelle)
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx(libelle));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void PaiementParCarte_Prime_Sur_Paiement_Generique_461000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Paiement par Carte FT262310865 2026-08-19 ORANGE TUNISIE"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // Non-regression : ces libelles deja corrects avant les nouvelles regles doivent le rester.
        [Fact]
        public void CommissionTransfert_Reste_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Commission transfert _ 202608240011|2026-08-26 recu 8808"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void VirementEmisAutres_Reste_461000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Virement Emis autres FT262380T9X 2026-08-26 banques"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void ComVirAut_Donne_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Com VirE Aut.Ag et FT26238000V 2026-08-26 Aut. BQ C7L9Y"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Nouveaux libelles valides le 2026-09-17 (via le repli AccountingKeywordRules) --------

        [Fact]
        public void DonneurDOrdre_Credit_Donne_532000_461000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(CreditTx("Donneur d'order : 202608240011|2026-08-26 1/META PLATFORMS"));
            Assert.Equal("532000", cd);
            Assert.Equal("461000", cc);
        }

        [Fact]
        public void MgEngSignat_Donne_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("MG/eng/signat 0102607022497219"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void ReglImpaye_Donne_461000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Règl impayé 0102607022338197"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void ChargesFees_Donne_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Charges/Fees"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void ChgsStOrdr_Donne_627000_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("CHGS-ST.ORDR/UTILITY PAY. REJECTN"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // "Regl prelev" : meme libelle pour le principal (montant eleve) et pour la ligne de frais
        // (petit montant) - disambiguation par seuil de montant (100, choix pragmatique valide par
        // l'utilisateur le 2026-09-17).
        [Fact]
        public void ReglPrelev_PetitMontant_Donne_627000_Frais()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Règl prélèv 87", montant: 2.000m));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void ReglPrelev_GrandMontant_Donne_461000_Principal()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Règl prélèv 87", montant: 7119.032m));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // "Remb anticipe" : role different selon la colonne (frais en Debit, principal recu en
        // Credit) - meme libelle dans les 2 cas.
        [Fact]
        public void RembAnticipe_Debit_Donne_627000_Frais()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Remb anticipé 0102607022338605", montant: 50.000m));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void RembAnticipe_Credit_Donne_532000_461000_Principal()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(CreditTx("Remb anticipé 0102607022338605", montant: 616.661m));
            Assert.Equal("532000", cd);
            Assert.Equal("461000", cc);
        }

        // "Bloc/Remb antic" (regle fixe, distincte de "Remb anticipe" seul) : ne doit pas etre
        // capturee par le dedoublement de role Debit/Credit ci-dessus.
        [Fact]
        public void BlocRembAntic_Donne_461000_532000_Quelle_Que_Soit_La_Colonne()
        {
            var (cdDebit, ccDebit) = LedgerAccountClassifier.Classify(DebitTx("Bloc/Remb antic 0102607022338497"));
            Assert.Equal("461000", cdDebit);
            Assert.Equal("532000", ccDebit);
        }
    }
}
