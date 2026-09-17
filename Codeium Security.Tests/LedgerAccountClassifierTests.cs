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

        [Fact]
        public void PrelevAbrege_Utilise_Le_Repli_437001_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Prélèv com/ EPS 1202607019539508"));
            Assert.Equal("437001", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void TvaComm_Utilise_Le_Repli_436600_532000()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("TVA/COMM 6752"));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void RejetPrelev_Reste_Vide_Meme_Avec_Le_Repli()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Rejet prélév 2"));
            Assert.Null(cd);
            Assert.Null(cc);
        }

        // "Certif cheg ..." (2026-09-17) : aucune regle comptable donnee par l'utilisateur pour ce
        // libelle - doit rester vide, comme "Rejet prelev" et "Interets cred/deb", en attendant
        // confirmation plutot que de deviner.
        [Fact]
        public void CertifCheg_Reste_Vide_En_Attente_De_Confirmation()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Certif cheg 8868150"));
            Assert.Null(cd);
            Assert.Null(cc);
        }

        [Fact]
        public void InteretsCredDeb_Combine_Reste_Vide()
        {
            var (cd, cc) = LedgerAccountClassifier.Classify(DebitTx("Intéréts créd/déb TEG14.4060"));
            Assert.Null(cd);
            Assert.Null(cc);
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
    }
}
