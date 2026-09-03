using Codeium_Security.Services;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Xunit;

namespace Codeium_Security.Tests
{
    // Tests purs (aucun OCR) pour GenericDocumentMetadataExtractor : construit des TableRow
    // a la main pour simuler la zone d'en-tete d'un releve, et verifie CustomerName /
    // ExtractionPeriod. N'exerce jamais BankDocumentParser.
    public class GenericDocumentMetadataExtractorTests
    {
        private readonly GenericDocumentMetadataExtractor _extractor = new();

        private static TableRow Row(string text)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell { Text = text, Left = 0 });
            return row;
        }

        private static List<TableRow> RowsFromLines(params string[] lines) =>
            lines.Select(Row).ToList();

        [Fact]
        public void CustomerName_Label_TitulaireDuCompte_SameLine()
        {
            var rows = RowsFromLines(
                "N° du compte : 14034034101700148178",
                "Titulaire du compte : AL BARAKA RENT A CAR",
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract(string.Join("\n", rows.SelectMany(r => r.Cells.Select(c => c.Text))), rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
        }

        [Fact]
        public void CustomerName_Label_Client_SameLine_ShortValueNotRejectedAsAcronym()
        {
            // Reproduit un releve BTL reel ("Client: EZB") : une valeur courte tout-majuscule
            // ne doit PAS etre rejetee comme un sigle de banque quand elle vient d'un label
            // explicite et non ambigu - contrairement au ramassage sans label (voir
            // CustomerName_NoLabel_* ci-dessous, ou un sigle isole est bien rejete).
            var rows = RowsFromLines(
                "Numéro de Compte : 8901769223",
                "Client: EZB",
                "Agence : Agence Ben Arous",
                "Devise : TND",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("EZB", result.CustomerName);
        }

        [Fact]
        public void CustomerName_Label_NomDuClient_SameLine()
        {
            var rows = RowsFromLines(
                "Nom du client : BLUE TUNISIE",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
        }

        [Fact]
        public void CustomerName_Label_To_ValueOnNextLine()
        {
            var rows = RowsFromLines(
                "To",
                "CH CLEAN",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("CH CLEAN", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_StopsBeforeStreetAddress()
        {
            var rows = RowsFromLines(
                "SOCIETE AUTOSET 7 PIECES AUTO",
                "AVENUE FRANCE N 70",
                "2013 BEN AROUS",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("SOCIETE AUTOSET 7 PIECES AUTO", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_StopsAtFirstLine_DoesNotConcatenate()
        {
            var rows = RowsFromLines(
                "STE GROUPE CHAKROUN DE COMMERCE",
                "TUNISIE",
                "LA MARSA",
                "N8 RUE HAFSIDES",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("STE GROUPE CHAKROUN DE COMMERCE", result.CustomerName);
        }

        [Fact]
        public void CustomerName_NoLabel_SkipsNumberedStreetLine()
        {
            var rows = RowsFromLines(
                "COM'UP AGENCY",
                "N°09 RUE HAMADI BEN AMMAR",
                "LA GOULETTE 2060",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("COM'UP AGENCY", result.CustomerName);
        }

        [Fact]
        public void ExtractionPeriod_OperationsDuAu_SameLine()
        {
            var rows = RowsFromLines(
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_DuAu_WithoutOperationsPrefix()
        {
            var rows = RowsFromLines(
                "Du 01-02-2026 au 28-02-2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01-02-2026", result.Period!.Start);
            Assert.Equal("28-02-2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_PeriodeLabel_DashSeparator()
        {
            var rows = RowsFromLines(
                "Période : 01.02.2026 - 28.02.2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01.02.2026", result.Period!.Start);
            Assert.Equal("28.02.2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_DatesOnSeparateLines()
        {
            var rows = RowsFromLines(
                "Du 01/02/2026",
                "Au 28/02/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_NeverReadsTransactionDates()
        {
            var rows = RowsFromLines(
                "Titulaire du compte : AL BARAKA RENT A CAR",
                "Date Libellé opération Débit Crédit Solde",
                "02/02/2026 VRST. 4325674 03/02/2026 18120",
                "03/02/2026 PRLV. AIL 30/01/2026 3664891");

            var result = _extractor.Extract("", rows);

            Assert.Null(result.Period);
        }

        [Fact]
        public void CustomerNameAndPeriod_NameThenPeriod_OrderDoesNotMatter()
        {
            var rows = RowsFromLines(
                "Nom du client : BLUE TUNISIE",
                "Période du 01/11/2024 au 30/11/2024",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/11/2024", result.Period!.Start);
            Assert.Equal("30/11/2024", result.Period!.End);
        }

        [Fact]
        public void CustomerNameAndPeriod_PeriodThenName_OrderDoesNotMatter()
        {
            var rows = RowsFromLines(
                "Période du 01/01/2024 au 31/05/2024",
                "Nom du client : BLUE TUNISIE",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("BLUE TUNISIE", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/01/2024", result.Period!.Start);
            Assert.Equal("31/05/2024", result.Period!.End);
        }

        [Fact]
        public void CustomerName_UnlabeledWithArabicTitleAndLoneDate_StopsAtFirstLine()
        {
            var rows = RowsFromLines(
                "STE. CATERING CONCEPT",
                "14 IMEN ABOU HANIFA",
                "B 5 - LA MARSA",
                "Au :",
                "RELEVE DE COMPTE حساب كشف",
                "31/05/2025",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("STE. CATERING CONCEPT", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Uib_LabelValueFarApart_SkipsOtherLabelsAndAddress()
        {
            // Reproduit un releve UIB reel dont l'OCR melange fortement les colonnes : le
            // label "TITULAIRE" est separe de sa valeur reelle ("COM'UP AGENCY") par
            // plusieurs AUTRES labels (Agence, Code client, Gestionnaire, references
            // bancaires, RIB, IBAN, type de compte...), et la periode est ecrite "<date> Au"
            // puis la seconde date seule sur la ligne suivante.
            var rows = RowsFromLines(
                "UIB",
                "GROUPE SOCIETE GENERALE",
                "TITULAIRE",
                "AGENCE U.I.B CENTRALE",
                "CODE CLIENT",
                "Gestionnaire OUMAIMA OUERTANI",
                "REFERENCES BANCAIRES",
                "N°Compte",
                "00 00033029238",
                "12 000 00 00033029238 16",
                "TN59 12 000 00 00033029238 16",
                "CPTE COURANT COMMERCIAL PRIVE",
                "RELEVE D'IDENTITE BANCAIRE",
                "COM'UP AGENCY",
                "00033029238-16",
                "01/12/2025 Au",
                "19/01/2026",
                "N°09 RUE HAMADI BEN AMMAR",
                "LA GOULETTE",
                "2060",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("COM'UP AGENCY", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/12/2025", result.Period!.Start);
            Assert.Equal("19/01/2026", result.Period!.End);
        }

        [Fact]
        public void RealWorldSample_ExtraitBancaireBT2_CustomerNameAndPeriod()
        {
            // Reproduit fidelement l'en-tete reel de EXTRAIT BANCAIRE_BT2.pdf.
            var rows = RowsFromLines(
                "Extrait de Compte Bancaire",
                "No du compte - 14034034101700148178",
                "du compte : AL BARAKA RENT A CAR",
                "Opérations du 01/02/2026 au 28/02/2026",
                "Date opération Libellé opération Date valeur Débit");

            var result = _extractor.Extract("", rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
            Assert.NotNull(result.Period);
            Assert.Equal("01/02/2026", result.Period!.Start);
            Assert.Equal("28/02/2026", result.Period!.End);
        }

        [Fact]
        public void CustomerName_LegalEntityPrefix_PreferredOverEarlierNoise()
        {
            // La ligne SARL/SA/STE peut ne pas etre la toute premiere ligne de l'en-tete
            // (ex. une ligne de bruit d'agence avant elle) : elle doit tout de meme etre
            // preferee au reste, quelle que soit sa position.
            var rows = RowsFromLines(
                "Agence Centre Ville",
                "SARL NOUVELLE GENERATION TRADING",
                "12 RUE DE LA LIBERTE",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("SARL NOUVELLE GENERATION TRADING", result.CustomerName);
        }

        [Fact]
        public void ExtractionPeriod_MonthlyWording_ComputesFirstAndLastDayOfMonth()
        {
            var rows = RowsFromLines(
                "Du mois de Décembre 2024",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/12/2024", result.Period!.Start);
            Assert.Equal("31/12/2024", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_MonthlyWording_DuVariant_ComputesFirstAndLastDayOfMonth()
        {
            // Releve Al Baraka reel ("Mois du Juillet 2024", pas "Mois de").
            var rows = RowsFromLines(
                "Mois du Juillet 2024",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/07/2024", result.Period!.Start);
            Assert.Equal("31/07/2024", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_StatementFromTo_EnglishWording()
        {
            // Releve ATB reel ("Statement from 01/07/2026 to 31/07/2026").
            var rows = RowsFromLines(
                "Statement from 01/07/2026 to 31/07/2026",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/07/2026", result.Period!.Start);
            Assert.Equal("31/07/2026", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_StatementAsOfSingleDate_ReleveAu()
        {
            var rows = RowsFromLines(
                "Relevé au 31/05/2025",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Null(result.Period!.Start);
            Assert.Equal("31/05/2025", result.Period!.End);
        }

        [Fact]
        public void ExtractionPeriod_StatementAsOfSingleDate_SoldeAu()
        {
            var rows = RowsFromLines(
                "Solde au 30/04/2025",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Null(result.Period!.Start);
            Assert.Equal("30/04/2025", result.Period!.End);
        }

        [Fact]
        public void CustomerName_LegalEntityMarker_MergedAtEndOfUnrelatedSentence()
        {
            // Reproduit un releve reel (25047000000121760245.pdf) : la fusion de colonnes OCR
            // accole "SOCIETE AUTOSET 7 PIECES AUTO" a la fin d'une phrase d'accroche sans
            // rapport, sur la MEME ligne physique. Seul le texte a partir du marqueur doit
            // devenir CustomerName - jamais la phrase d'accroche qui le precede.
            var rows = RowsFromLines(
                "RIB 25047000000121760245 Date du 01/01/2025 au 31/01/2025",
                "Cher client, nous avons l'honneur de vous adresser, ci-après, le relevé des",
                "opérations portées à votre compte en vous souhaitant bonne réception SOCIETE AUTOSET 7 PIECES AUTO",
                "AVENUE FRANCE N 70 2013 BEN AROUS",
                "Date Libellé Opération Date valeur Débit Crédit");

            var result = _extractor.Extract("", rows);

            Assert.Equal("SOCIETE AUTOSET 7 PIECES AUTO", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Abc_DestinataireLabel_SlashSeparator_IbanMergedWithName()
        {
            // Reproduit l'en-tete reel de RLV ABC.pdf : la banque ABC imprime ses labels
            // suivis d'un "/" au lieu d'un ":" ("RIB/", "IBAN/", "Destinataire/"), et la valeur
            // IBAN se retrouve fusionnee par l'OCR avec le nom du destinataire sur la meme
            // ligne physique juste en dessous du label.
            var rows = RowsFromLines(
                "Du/ 01.05.2026 Au/ 31.05.2026 Relevé de compte mensuel",
                "RIB/ Devise /",
                "28009035547100000148 TND",
                "IBAN/ Destinataire/",
                "TN5928009035547100000148 PROFESSIONNELLE DISTRIBUTION",
                "ROUTE MORNAG KM 3 BOUJARDGA",
                "BOUMHAL - BEN AROUS",
                "TUNISIE",
                "2097",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("PROFESSIONNELLE DISTRIBUTION", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Tsb_TitulaireDuCompte_TrailingArabicGloss_ValueMergedWithRibAndDevise()
        {
            // Reproduit l'en-tete reel de TSB_relevé.pdf : la ligne d'en-tete de tableau
            // ("R.I.B / CODE DEVISE / TITULAIRE DU COMPTE") porte un gloss arabe accole en fin
            // de ligne par l'OCR, et la ligne de valeur suivante fusionne RIB + devise + nom du
            // titulaire.
            var rows = RowsFromLines(
                "R.I.B CODE DEVISE TITULAIRE DU COMPTE الحساب صاحب",
                "21014014404700244107 TND PROFESSIONAL SERVICE PARTS",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("PROFESSIONAL SERVICE PARTS", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Bh_NoLabel_NameMergedAfterDateCurrencyAndPageNumber()
        {
            // Reproduit l'en-tete reel de releve bh.pdf : aucun label "Titulaire" n'est
            // present du tout - le nom du client apparait nu, fusionne par l'OCR juste apres
            // la date d'edition, la devise et le numero de page ("1/17"). Une ligne precedente
            // fusionne le numero de compte avec le label "Agence" et ne doit jamais etre prise
            // pour le nom.
            var rows = RowsFromLines(
                "14034034101700148178 Agence : HAMMAM CHATT",
                "30-06-26 TND 1/17 AL BARAKA RENT A CAR",
                "1164",
                "HAMMAM CHATT",
                "72 RUE DE L'ENVIRONNEMENT",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_ExtraitTsb_ValueContinuationOnRowPrecedingLabelRow()
        {
            // Reproduit fidelement l'en-tete OCR reel de EXTRAIT TSB.pdf : le tableau de lignes
            // reconstruit par le pipeline place le fragment "SERVICE PARTS (PSF) RIB : 7" AVANT
            // la ligne du label "Intitulé : PROFESSIONAL", alors qu'il la suit visuellement (un
            // meme texte imprime segmente en deux TableRow, dont l'ordre dans le tableau n'est
            // pas garanti egal a l'ordre visuel). Le nom doit malgre tout etre recompose en
            // entier, "(PSF)" inclus (c'est ce que l'OCR lit reellement - le corriger en "(PSP)"
            // serait corriger une erreur de lecture de caractere, hors de portee de ce module).
            var rows = RowsFromLines(
                "8 Extrait de compte Edité le: 14/04/2026 15:49",
                "Tunision Saudi Bank Page ! sur !",
                "Compte N° 0144047002441 Agence : EL MOUROUJ",
                ":",
                "SERVICE PARTS (PSF) RIB : 7",
                "Intitulé : PROFESSIONAL",
                "Devise: TND",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("PROFESSIONAL SERVICE PARTS (PSF)", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_ExtraitCompteStb_OrphanedDomiciliationValueNeverPickedAsName()
        {
            // Reproduit fidelement l'en-tete OCR reel de EXTRAIT DE COMPTE STB.pdf (confirme via
            // /api/Ocr/debug-html) : "Domiciliation" et sa valeur "BEN AROUS" (la VILLE, pas le
            // client) atterrissent sur deux lignes/TableRow SEPARES - contrairement au cas TSB ou
            // le fragment orphelin devait etre RATTACHE, ici il doit etre REJETE : "BEN AROUS" ne
            // doit jamais devenir CustomerName.
            //
            // Le vrai nom du client (AYOUSSI ANIS B.FAREH) est lui-meme fragmente sur PLUSIEURS
            // lignes non adjacentes, avec une ligne "Devise compte" intercalee entre les deux
            // morceaux - une reconstruction complete demanderait un balayage multi-lignes bien
            // au-dela du correctif minimal ici (voir le fragment "AYOUSSI D" obtenu ci-dessous,
            // qui est le premier candidat non rejete APRES le rejet de "BEN AROUS" - pas une
            // reconstruction volontaire du nom complet).
            var rows = RowsFromLines(
                "57820 SOCIETE TUNISIENNE DE BANQUE",
                "BANK S.A. au capital de 776.875.000 Dinars",
                "EXTRAIT DE COMPTE",
                "Numéro compte : 0871034416788 Heure : 11:38:58",
                "Domiciliation Utilisateur 4536E",
                "BEN AROUS",
                "AYOUSSI D",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.NotEqual("BEN AROUS", result.CustomerName);
            Assert.Equal("AYOUSSI D", result.CustomerName);
        }

        [Fact]
        public void RealWorldSample_Bh_LetterSpacedBankMastheadNeverPickedAsName()
        {
            // Reproduit fidelement l'en-tete OCR reel de releve bh.pdf (confirme via les logs
            // serveur) : le masthead "BANK RELEVE DE COMPTE" est OCR'ise "B A N K RELEVE DE
            // COMPTE" (le mot "BANK" du logo espace lettre par lettre par l'OCR) - ni
            // \bBANK\b ni le motif de titre de document (ancre sur "RELEVE" en debut de ligne)
            // ne le reconnaissaient avant le fix, le laissant filer jusqu'a CustomerName. Le vrai
            // nom du client ("AL BARAKA RENT A CAR") est sur une ligne plus bas, sans aucun label.
            var rows = RowsFromLines(
                "كشف حساب",
                "B A N K RELEVE DE COMPTE",
                "CODE BANQUE CODE AGENCE NUMERO DE COMPTE قتع",
                "Agence : HAMMAM CHATT",
                "o 14034034101700148178",
                "AL BARAKA RENT A CAR",
                "Date Libellé opération Débit Crédit Solde");

            var result = _extractor.Extract("", rows);

            Assert.Equal("AL BARAKA RENT A CAR", result.CustomerName);
        }

        [Fact]
        public void ExtractionPeriod_DuAu_ToleratesArabicFillerBetweenLabelAndDates()
        {
            var rows = RowsFromLines(
                "Date du ???????? ?? . 01/01/2025 au ??????? 31/01/2025",
                "Date Libellé Opération Date valeur Débit Crédit");

            var result = _extractor.Extract("", rows);

            Assert.NotNull(result.Period);
            Assert.Equal("01/01/2025", result.Period!.Start);
            Assert.Equal("31/01/2025", result.Period!.End);
        }
    }
}
