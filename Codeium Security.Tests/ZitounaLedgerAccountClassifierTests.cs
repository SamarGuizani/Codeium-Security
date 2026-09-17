using Codeium_Security.Models;
using Codeium_Security.Services.Export;
using Xunit;

namespace Codeium_Security.Tests
{
    // Verifie ZitounaLedgerAccountClassifier isolement (pas d'export Excel, pas de pipeline OCR) :
    // couvre les regles du cahier des charges utilisateur (2026-09-17) - voir le commentaire en tete
    // de ZitounaLedgerAccountClassifier.cs pour les decisions prises sur les points ambigus
    // (dedoublement 580000/541000 volontairement rare, societe = titulaire du compte).
    public class ZitounaLedgerAccountClassifierTests
    {
        private static Transaction DebitTx(string libelle, decimal montant = 100m)
            => new Transaction { Date = "01/01/2026", Libelle = libelle, Debit = montant, Credit = null };

        private static Transaction CreditTx(string libelle, decimal montant = 100m)
            => new Transaction { Date = "01/01/2026", Libelle = libelle, Debit = null, Credit = montant };

        private static (string CompteDebit, string CompteCredit) Single(Transaction tx, string? customerName = null)
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(tx, customerName);
            Assert.Single(lignes);
            return lignes[0];
        }

        // --- Commission / frais / TVA -------------------------------------------------------

        [Theory]
        [InlineData("COMMISSION BANCAIRE")]
        [InlineData("FRAIS DE TENUE DE COMPTE")]
        [InlineData("PDL")]
        [InlineData("COM")]
        public void Commission_Debit_Donne_627000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Commission_Credit_Inverse_Les_Comptes_Sans_Deplacer_Le_Montant()
        {
            var tx = CreditTx("COMMISSION", 100m);
            var (cd, cc) = Single(tx);
            Assert.Equal("532000", cd);
            Assert.Equal("627000", cc);
            Assert.Null(tx.Debit);
            Assert.Equal(100m, tx.Credit);
        }

        [Fact]
        public void Tva_Debit_Donne_436600_532000()
        {
            var (cd, cc) = Single(DebitTx("TVA"));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Tva_Credit_Inverse()
        {
            var (cd, cc) = Single(CreditTx("TVA"));
            Assert.Equal("532000", cd);
            Assert.Equal("436600", cc);
        }

        // "TVA sur COM(M)"/"TVA/COMM" (2026-09-17, confirme par l'utilisateur apres une correction
        // intermediaire vers 627000, revenue en arriere) : reste de la TVA, 436600 - prime sur la
        // regle Commission generique.
        [Theory]
        [InlineData("TVA SUR COM")]
        [InlineData("TVA / COM")]
        [InlineData("TVA/COM")]
        [InlineData("TVA/COMM")]
        [InlineData("TVA / COMM")]
        [InlineData("tva/comm")]
        [InlineData("TVA /COM")]
        [InlineData("TVA/ COMM")]
        public void TvaSurCom_Donne_436600_532000_Et_Prime_Sur_La_Regle_Commission(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void TvaLeasing_Donne_436600_532000()
        {
            var (cd, cc) = Single(DebitTx("TVA LEASING"));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void TvaDebitDivers_Prime_Sur_TvaGenerique_Et_Sur_DebitDivers()
        {
            var (cd, cc) = Single(DebitTx("TVA DEBIT DIVERS"));
            Assert.Equal("436600", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void DebitDivers_Sans_Tva_Donne_627000_532000()
        {
            var (cd, cc) = Single(DebitTx("DEBIT DIVERS"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Prelevement -------------------------------------------------------------------

        // 437001 reserve au precompte "MIN DES FINANCES" (2026-09-17, correction utilisateur :
        // "437001 une seule fois par mois, seulement si paiement prelevement min de finance") -
        // remplace la version precedente de ce test, qui attendait aussi 437001 pour "PRELEVEMENT"
        // et "DECLARATION" seuls avant cette correction (voir tests ci-dessous pour leur nouveau
        // comportement).
        [Theory]
        [InlineData("MIN DE FIN")]
        [InlineData("PRELEVEMENT MIN DES FINANCES")]
        public void PrelevementMinFinances_Donne_437001_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("437001", cd);
            Assert.Equal("532000", cc);
        }

        // "Prélèv com/..." (2026-09-17, correction utilisateur) : traite comme une commission
        // (627000), pas comme un prelevement (437001) - remplace la version precedente de ce test.
        [Theory]
        [InlineData("Prélèv com/ EPS 120260701953508")]
        [InlineData("PRÉLÈV COM")]
        public void PrelevAbrege_Avec_Com_Donne_627000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        // "PRELEVEMENT"/"PRELEV"/"DECLARATION" isoles (sans "com" ni "min de finance(s)") ne
        // declenchent plus aucune regle depuis la correction du 2026-09-17 - restent sans imputation
        // plutot que de deviner.
        [Theory]
        [InlineData("PRELEVEMENT")]
        [InlineData("PRELEV")]
        [InlineData("DECLARATION")]
        public void PrelevementIsole_Reste_Non_Classifie(string libelle)
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(DebitTx(libelle), null);
            Assert.Empty(lignes);
        }

        // "REJET PRELEV..." (ex. "Rejet prélèv 3315") : regle comptable validee par l'utilisateur
        // le 2026-09-17 (bucket Commissions et frais bancaires) - remplace la version precedente de
        // ce test qui verifiait l'absence d'imputation (en attente de confirmation a l'epoque).
        [Theory]
        [InlineData("Rejet prélèv 3315")]
        [InlineData("Rejet prélèv 1403908")]
        public void RejetPrelev_Donne_627000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Dobct_Comptoir_De_Tunis_Reg_Donne_437001_532000()
        {
            var (cd, cc) = Single(DebitTx("DOBCT COMPTOIR DE TUNIS REG"));
            Assert.Equal("437001", cd);
            Assert.Equal("532000", cc);
        }

        // --- Virement / encaissement ---------------------------------------------------------

        [Theory]
        [InlineData("VIREMENT RECU")]
        [InlineData("VIR SEPA")]
        [InlineData("REGLEMENT CHEQUE N123")]
        [InlineData("ENC CHEQUE")]
        [InlineData("EFFET RECU")]
        [InlineData("ENCAISSEMENT")]
        [InlineData("ENCAISS CLIENT")]
        public void Virement_Debit_Donne_461000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Virement_Credit_Inverse()
        {
            var (cd, cc) = Single(CreditTx("VIREMENT RECU"));
            Assert.Equal("532000", cd);
            Assert.Equal("461000", cc);
        }

        // --- Agios / blocage / deblocage / redressement --------------------------------------

        [Fact]
        public void Agios_Donne_651500_532000()
        {
            var (cd, cc) = Single(DebitTx("AGIOS"));
            Assert.Equal("651500", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Blocage_Donne_511700_532000()
        {
            var (cd, cc) = Single(DebitTx("BLOCAGE"));
            Assert.Equal("511700", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Deblocage_Donne_532000_511700_Et_Ne_Matche_Pas_Blocage()
        {
            var (cd, cc) = Single(DebitTx("DEBLOCAGE"));
            Assert.Equal("532000", cd);
            Assert.Equal("511700", cc);
        }

        [Fact]
        public void Deblocage_Credit_Inverse()
        {
            var (cd, cc) = Single(CreditTx("DEBLOCAGE"));
            Assert.Equal("511700", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Redressement_Donne_461000_532000()
        {
            var (cd, cc) = Single(DebitTx("REDRESSEMENT"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Interets --------------------------------------------------------------------------

        [Theory]
        [InlineData("INTERET DEBITEURS")]
        [InlineData("CHARGES D'INTERETS")]
        public void InteretDebiteurs_Donne_651000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("651000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void InteretCrediteurs_Donne_532000_651000()
        {
            var (cd, cc) = Single(DebitTx("INTERET CREDITEURS"));
            Assert.Equal("532000", cd);
            Assert.Equal("651000", cc);
        }

        // "Intérêts créd/déb" (forme reelle abregee et combinee, 2026-09-17) : l'utilisateur a
        // depuis valide un compte unique (651000/532000), sans distinction debiteur/crediteur -
        // remplace la version precedente de ce test qui verifiait l'absence d'imputation.
        [Fact]
        public void InteretsCredDeb_Combine_Donne_651000_532000()
        {
            var (cd, cc) = Single(DebitTx("Intérêts créd/déb"));
            Assert.Equal("651000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Autres regles a mot-cle unique ----------------------------------------------------

        [Fact]
        public void PaiementPrincipale_Donne_461000_532000()
        {
            var (cd, cc) = Single(DebitTx("PAIEMENT PRINCIPALE"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void CotisationCarte_Donne_627000_532000()
        {
            var (cd, cc) = Single(DebitTx("COTISATION CARTE"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void MouvementComm_Donne_627000_532000()
        {
            var (cd, cc) = Single(DebitTx("MOUVEMENT COMM"));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Mouvement_Seul_Ne_Doit_Pas_Etre_Considere_Comme_Une_Commission()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(DebitTx("MOUVEMENT"), null);
            Assert.Empty(lignes);
        }

        [Fact]
        public void AchatAmira_Donne_461000_532000()
        {
            var (cd, cc) = Single(DebitTx("ACHAT AMIRA"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Mutuelle_Donne_461000_532000()
        {
            var (cd, cc) = Single(DebitTx("MUTUELLE"));
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // --- DAB / WIB / GAB / GBH / DBH : titulaire hors liste, aucun signal special ----------
        // (dedoublement volontairement rare - voir commentaire de Classify) -> regle simple.

        [Theory]
        [InlineData("RETRAIT DAB AGENCE CENTRALE")]
        [InlineData("RETRAIT WIB")]
        [InlineData("RETRAIT GAB")]
        [InlineData("RETRAIT GBH")]
        [InlineData("RETRAIT DBH")]
        public void Dab_SansSocieteSpeciale_Donne_461000_532000(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle), customerName: "SARL ORDINAIRE");
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Societes speciales : sections 5 a 9 ------------------------------------------------

        [Fact]
        public void Virement_Titulaire_De_La_Liste_Remplace_461000_Par_411000_Debit()
        {
            var (cd, cc) = Single(DebitTx("VIREMENT RECU"), customerName: "ROTANA TRAVEL");
            Assert.Equal("411000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Virement_Titulaire_De_La_Liste_Remplace_461000_Par_411000_Credit()
        {
            var (cd, cc) = Single(CreditTx("VIR SEPA"), customerName: "ROYAL LUMIERE");
            Assert.Equal("532000", cd);
            Assert.Equal("411000", cc);
        }

        [Fact]
        public void RemCommercant_Titulaire_De_La_Liste_Applique_411000()
        {
            var (cd, cc) = Single(DebitTx("REM COMMERCANT"), customerName: "SOFOMEKA");
            Assert.Equal("411000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Virement_Titulaire_Hors_Liste_Garde_461000()
        {
            var (cd, cc) = Single(DebitTx("VIREMENT RECU"), customerName: "SARL ORDINAIRE");
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Societe_Non_Listee_Ne_Recoit_Jamais_411000()
        {
            var (cd, cc) = Single(DebitTx("ENCAISSEMENT CHEQUE"), customerName: "ENTREPRISE QUELCONQUE");
            Assert.Equal("461000", cd);
            Assert.NotEqual("411000", cd);
        }

        // --- VERSEMENT ESP -----------------------------------------------------------------------

        [Fact]
        public void VersementEsp_Titulaire_De_La_Liste_Debit()
        {
            var (cd, cc) = Single(DebitTx("VERSEMENT ESP"), customerName: "SOFOMEKA");
            Assert.Equal("532000", cd);
            Assert.Equal("411000", cc);
        }

        [Fact]
        public void VersementEsp_Titulaire_De_La_Liste_Credit()
        {
            var (cd, cc) = Single(CreditTx("VERSEMENT ESP"), customerName: "SOFOMEKA");
            Assert.Equal("411000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void VersementEsp_TitulaireHorsListe_SansSignal_Donne_Regle_Versement_Normale()
        {
            // Aucun signal special (titulaire hors liste, libelle ne cite aucune des 44 societes)
            // -> comportement conservateur, comme un VERSEMENT normal (411000/532000).
            var (cd, cc) = Single(DebitTx("VERSEMENT ESP"), customerName: "SARL ORDINAIRE");
            Assert.Equal("411000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void VersementEsp_TitulaireHorsListe_AvecSocieteCiteeDansLibelle_Dedouble()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(
                DebitTx("VERSEMENT ESP POUR ROTANA TRAVEL"), customerName: "SARL ORDINAIRE");

            Assert.Equal(2, lignes.Count);
            Assert.Equal(("532000", "580000"), lignes[0]);
            Assert.Equal(("580000", "541000"), lignes[1]);
        }

        [Fact]
        public void VersementEsp_TitulaireHorsListe_AvecSocieteCiteeDansLibelle_Credit_Inverse()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(
                CreditTx("VERSEMENT ESP POUR ROTANA TRAVEL"), customerName: "SARL ORDINAIRE");

            Assert.Equal(2, lignes.Count);
            Assert.Equal(("580000", "532000"), lignes[0]);
            Assert.Equal(("541000", "580000"), lignes[1]);
        }

        // --- RETRAIT ESP / DAB hors liste avec signal special dans le libelle -------------------

        [Fact]
        public void RetraitEsp_TitulaireHorsListe_AvecSocieteCiteeDansLibelle_Dedouble_Debit()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(
                DebitTx("RETRAIT ESP POUR ROTANA TRAVEL"), customerName: "SARL ORDINAIRE");

            Assert.Equal(2, lignes.Count);
            Assert.Equal(("580000", "532000"), lignes[0]);
            Assert.Equal(("541000", "580000"), lignes[1]);
        }

        [Fact]
        public void RetraitEsp_TitulaireHorsListe_AvecSocieteCiteeDansLibelle_Dedouble_Credit()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(
                CreditTx("RETRAIT ESP POUR ROTANA TRAVEL"), customerName: "SARL ORDINAIRE");

            Assert.Equal(2, lignes.Count);
            Assert.Equal(("532000", "580000"), lignes[0]);
            Assert.Equal(("580000", "541000"), lignes[1]);
        }

        [Fact]
        public void Dab_TitulaireHorsListe_AvecSocieteCiteeDansLibelle_Dedouble()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(
                DebitTx("RETRAIT DAB SOFOMEKA"), customerName: "SARL ORDINAIRE");

            Assert.Equal(2, lignes.Count);
            Assert.Equal(("580000", "532000"), lignes[0]);
            Assert.Equal(("541000", "580000"), lignes[1]);
        }

        [Fact]
        public void Dab_TitulaireDeLaListe_Meme_Avec_Nom_Different_Dans_Libelle_Garde_Regle_Normale()
        {
            // Le titulaire fait partie de la liste -> pas de dedoublement, peu importe le libelle.
            var (cd, cc) = Single(DebitTx("RETRAIT DAB AGENCE CENTRALE"), customerName: "SOFOMEKA");
            Assert.Equal("461000", cd);
            Assert.Equal("532000", cc);
        }

        // --- Aucune regle ne correspond -> cellules vides ----------------------------------------

        [Fact]
        public void Libelle_Sans_MotCle_Reconnu_Ne_Retourne_Aucune_Ligne()
        {
            var lignes = ZitounaLedgerAccountClassifier.Classify(DebitTx("OPERATION DIVERSE XYZ"), null);
            Assert.Empty(lignes);
        }

        // --- Robustesse OCR : casse, accents, espaces multiples -----------------------------------

        [Theory]
        [InlineData("commission")]
        [InlineData("Commission   Bancaire")]
        [InlineData("COMMISSION")]
        public void Commission_Robuste_A_La_Casse_Et_Aux_Espaces(string libelle)
        {
            var (cd, cc) = Single(DebitTx(libelle));
            Assert.Equal("627000", cd);
            Assert.Equal("532000", cc);
        }

        [Fact]
        public void Deblocage_Robuste_Aux_Accents()
        {
            var (cd, cc) = Single(DebitTx("DÉBLOCAGE"));
            Assert.Equal("532000", cd);
            Assert.Equal("511700", cc);
        }

        // --- IsZitounaDocument -------------------------------------------------------------------

        [Fact]
        public void IsZitounaDocument_Detecte_Via_RawSectionText_Quand_BankName_Est_Vide()
        {
            var account = new BankAccountSection { RawSectionText = "BANQUE ZITOUNA - RELEVE DE COMPTE" };
            Assert.True(ZitounaLedgerAccountClassifier.IsZitounaDocument(account, bankName: ""));
        }

        [Fact]
        public void IsZitounaDocument_Faux_Pour_Une_Autre_Banque()
        {
            var account = new BankAccountSection { RawSectionText = "BIAT - RELEVE DE COMPTE" };
            Assert.False(ZitounaLedgerAccountClassifier.IsZitounaDocument(account, bankName: "BIAT"));
        }

        // Cas reel (2026-09-17) : BankDocument.BankName reste vide pour Zitouna (ExtractBankName ne
        // connait pas "ZITOUNA"), et RawSectionText (section 5 du releve) ne contient pas non plus
        // l'en-tete - c'est DocumentMetadata.BankName (repli ajoute dans DocumentProcessingService,
        // a partir du meme texte OCR complet que celui deja utilise en interne par
        // BankDocumentParser) qui doit permettre la detection en pratique.
        [Fact]
        public void IsZitounaDocument_Vrai_Quand_Seul_MetadataBankName_Porte_Zitouna()
        {
            var account = new BankAccountSection { RawSectionText = "01/01/2026 COMMISSION 100.000" };
            Assert.True(ZitounaLedgerAccountClassifier.IsZitounaDocument(account, bankName: "Banque Zitouna"));
        }
    }
}
