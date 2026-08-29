using System.Text.RegularExpressions;
using Codeium_Security.Models;

namespace Codeium_Security.Services.DocumentMetadataExtraction
{
    // Extraction generique de metadonnees d'en-tete (nom du client, periode de l'extrait),
    // valable pour n'importe quelle banque. Ne contient AUCUNE regle specifique a une banque
    // (BT, BH, BIAT, BNA...), n'appelle jamais BankDocumentParser et ne modifie rien du
    // parsing des transactions : entierement independant, lecture seule sur fullText/rows
    // deja produits par le pipeline OCR existant.
    public class GenericDocumentMetadataExtractor : IDocumentMetadataExtractor
    {
        // Mots-cles de colonnes de tableau de transactions : des qu'une ligne en matche au
        // moins 2, on considere que le tableau des transactions commence la - tout ce qui
        // precede est la "zone d'en-tete" ou l'on cherche CustomerName/ExtractionPeriod.
        // Purement structurel/positionnel, jamais specifique a une banque.
        private static readonly Regex[] ColumnHeaderKeywords =
        {
            new(@"\bDate\b", RegexOptions.IgnoreCase),
            new(@"Libell[ée]|Description|Op[ée]ration", RegexOptions.IgnoreCase),
            new(@"D[ée]bit", RegexOptions.IgnoreCase),
            new(@"Cr[ée]dit", RegexOptions.IgnoreCase),
            new(@"Solde", RegexOptions.IgnoreCase),
            new(@"R[ée]f[ée]rence", RegexOptions.IgnoreCase),
            new(@"Valeur", RegexOptions.IgnoreCase),
        };

        private static bool LooksLikeColumnHeaderRow(string joined)
        {
            int hits = 0;
            foreach (var pattern in ColumnHeaderKeywords)
                if (pattern.IsMatch(joined))
                    hits++;
            return hits >= 2;
        }

        // Un caractere parasite d'OCR/bordure de tableau ("[", "|", "/", "?", guillemet...) en
        // tete de ligne casse tous les ancrages "^\s*label" utilises plus bas (label de champ,
        // titre de document...), faisant passer une ligne de metadonnee connue (ex. "No du
        // compte") pour un candidat valide. Purement structurel : ne retire que la ponctuation
        // parasite en DEBUT de ligne, jamais le contenu.
        private static string StripLeadingNoise(string line) =>
            Regex.Replace(line, @"^[^\p{L}\p{N}]+", "");

        private static List<string> GetHeaderZoneLines(List<TableRow>? rows, string fullText)
        {
            var lines = new List<string>();

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    string joined = string.Join(" ", row.Cells
                        .Where(c => !c.IsContinuationDetail)
                        .OrderBy(c => c.Left)
                        .Select(c => c.Text)).Trim();

                    if (string.IsNullOrWhiteSpace(joined))
                        continue;

                    if (LooksLikeColumnHeaderRow(joined))
                        break;

                    lines.Add(StripLeadingNoise(joined));
                }
            }

            if (lines.Count == 0)
            {
                // Repli : aucune TableRow exploitable, ou la ligne d'en-tete de tableau est
                // la toute premiere ligne (rien avant elle) - on retombe sur les premieres
                // lignes du texte brut plutot que de ne rien avoir a analyser.
                lines = (fullText ?? "")
                    .Split('\n')
                    .Select(l => StripLeadingNoise(l.Trim()))
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Take(15)
                    .ToList();
            }

            return lines;
        }

        // ----- CustomerName ------------------------------------------------------------

        private static readonly string[] CustomerLabelAlternatives =
        {
            @"Titulaire\s+du\s+compte",
            @"Titulaire",
            @"Intitul[ée]\s+(?:du|de)\s+compte",
            @"Intitul[ée]",
            @"Nom\s+du\s+client",
            @"Client",
            @"Soci[ée]t[ée]",
            @"Entreprise",
            @"Compte\s+de",
            @"Account\s*holder",
            @"Customer",
            @"Name",
            @"To",
        };

        // Priorite 1 : label + ":" + valeur sur la meme ligne. Le ":" est volontairement
        // obligatoire ici : sans lui, un label comme "Societe" matcherait aussi en debut
        // du nom d'une entreprise qui commence par ce mot (ex. "SOCIETE AUTOSET 7 PIECES
        // AUTO"), en avalant "SOCIETE" comme prefixe de label au lieu de faire partie du nom.
        private static readonly Regex CustomerLabelLineRegex = new(
            $@"^\s*(?:{string.Join("|", CustomerLabelAlternatives)})\b\s*:\s*(.*)$",
            RegexOptions.IgnoreCase);

        // Variante NON ancree en debut de ligne : le label + ":" peut se retrouver au MILIEU
        // d'une ligne par fusion de colonnes OCR avec un AUTRE champ qui le precede (ex. "IBAN :
        // TN59... Name: MOHAMED ZARRAGA ZPA" - releve QNB reel, ou "IBAN" et "Name" finissent
        // sur la meme ligne physique). On cherche le label n'importe ou et on capture tout ce
        // qui suit son ":", jusqu'a la fin de ligne - meme logique que LegalEntityPrefixRegex
        // pour la forme juridique, appliquee ici aux labels explicites.
        private static readonly Regex CustomerLabelMidLineRegex = new(
            $@"(?:{string.Join("|", CustomerLabelAlternatives)})\b\s*:\s*(.+)$",
            RegexOptions.IgnoreCase);

        // Priorite 2 : la ligne entiere n'est QUE le label (avec ou sans ":" final, sans
        // valeur derriere) - la valeur est alors sur la ligne suivante (ex. "To" / "CH CLEAN").
        private static readonly Regex CustomerLabelOnlyLineRegex = new(
            $@"^\s*(?:{string.Join("|", CustomerLabelAlternatives)})\s*:?\s*$",
            RegexOptions.IgnoreCase);

        // Sous-ensemble de labels surs pour un appariement SANS ":" (label puis espace puis
        // valeur, ex. "TITULAIRE MAROUENE EZZINE" - releve UIB reel ou l'OCR perd le ":").
        // Exclut volontairement "Société"/"Entreprise"/"Compte de" : ce sont des mots
        // generiques qui peuvent legitimement demarrer le nom d'une entreprise elle-meme (ex.
        // "SOCIETE AUTOSET 7 PIECES AUTO") - sans ":", on ne peut pas distinguer le label de
        // ce debut de nom, d'ou l'obligation du ":" pour ceux-la (CustomerLabelLineRegex).
        // "Titulaire"/"Client"/"Customer"/"To" sont des termes administratifs qui ne demarrent
        // jamais un nom d'entreprise reel.
        private static readonly string[] ColonOptionalLabelAlternatives =
        {
            @"Titulaire\s+du\s+compte",
            @"Titulaire",
            @"Intitul[ée]\s+(?:du|de)\s+compte",
            @"Intitul[ée]",
            @"Nom\s+du\s+client",
            @"Client",
            @"Account\s*holder",
            @"Customer",
            @"To",
        };

        // Une regex combinee unique ("label1|label2|...") laisserait le moteur, si le plus
        // long libelle ("Titulaire du compte") ne trouve pas de valeur derriere lui, retomber
        // sur une alternative plus courte ("Titulaire") et capturer la FIN du libelle complet
        // ("du compte") comme si c'etait la valeur - bug observe sur des lignes BIAT
        // ("Titulaire du compte" seul, sans valeur). On evite ce repli implicite en testant
        // chaque libelle INDIVIDUELLEMENT, du plus specifique/long au plus court (ordre du
        // tableau ci-dessus), et on s'arrete au premier qui est un prefixe de la ligne - sans
        // jamais essayer un libelle plus court une fois qu'un plus long a matche en prefixe.
        private static readonly (Regex FullLine, Regex WithValue)[] ColonOptionalLabelRegexes =
            ColonOptionalLabelAlternatives
                .Select(p => (
                    FullLine: new Regex($@"^\s*{p}\s*:?\s*$", RegexOptions.IgnoreCase),
                    WithValue: new Regex($@"^\s*{p}\b[ \t]+(.+)$", RegexOptions.IgnoreCase)))
                .ToArray();

        // Marqueurs generiques de fin de valeur pour un appariement SANS ":" : une date (toutes
        // lettres ou chiffree), une heure, ou la mention "Edité/Edite le" (date d'impression du
        // document) peuvent se retrouver accoles a la valeur sur la meme ligne par fusion de
        // colonnes OCR (ex. "TITULAIRE MAROUENE EZZINE E.Z.B Edité le 02 Mars 2023 a
        // 12:06:18..."). On tronque la valeur au premier de ces marqueurs plutot que de tout
        // rejeter - generique, jamais specifique a un intitule ou une banque.
        private static readonly Regex TrailingPrintMarkerRegex = new(
            @"\bEdit[ée]\b", RegexOptions.IgnoreCase);
        private static readonly Regex TrailingTimeRegex = new(@"\b\d{1,2}:\d{2}(:\d{2})?\b");

        // "Période" (le champ periode d'extraction) suivi immediatement d'un chiffre (sa
        // propre valeur "01/12/2024...") accole a la fin d'une valeur nom-de-client par fusion
        // de colonnes OCR (ex. "SOCIETE GROUPE CHAKROUN D Période: 01/12/2024 31/12/2024",
        // "CH CLEAN Période: 01/06/2025 30/06/2025" - releves reels). Le chiffre juste apres est
        // requis (lookahead) pour ne pas tronquer une simple MENTION du mot "periode" sans
        // valeur derriere (ex. un titre de section "Transactions pour la periode").
        private static readonly Regex TrailingPeriodFieldRegex = new(
            @"\bP[ée]riode\s*:?\s*(?=\d)", RegexOptions.IgnoreCase);

        // Une date chiffree (ex. "01/12/2024") accolee en fin de valeur par fusion de colonnes
        // OCR, meme sans le mot "Periode" devant (ex. reste d'un champ Date d'edition).
        private static readonly Regex TrailingNumericDateRegex = new(DateToken);

        // Marqueur de forme juridique (STE, SOCIETE, SARL, SUARL, SA...) : signal positif
        // generique d'un nom d'entreprise, quelle que soit la banque. Non ancre au debut de
        // ligne : une fusion de colonnes OCR peut accoler ce marqueur a la fin d'une phrase
        // sans rapport (ex. "...bonne reception SOCIETE AUTOSET 7 PIECES AUTO") - on cherche
        // le marqueur n'importe ou sur la ligne et on ne garde que le texte a partir de la
        // (voir usage ci-dessous), jamais la ligne entiere avec son prefixe parasite.
        // "SA" reste volontairement sensible a la casse (via (?-i:...), meme si le reste du
        // motif est insensible a la casse) : contrairement a STE/SOCIETE/SARL/SUARL, "sa" est
        // aussi un mot francais courant (possessif) - cherche n'importe ou sur la ligne (donc
        // sans l'ancrage de debut de ligne qui protegeait avant), un "sa" minuscule donnerait
        // trop de faux positifs. "SA" majuscule reste, lui, un signal fiable de forme juridique.
        private static readonly Regex LegalEntityPrefixRegex = new(
            @"\b(STE\.?|SOCI[ÉE]T[ÉE]|SUARL|SARL|(?-i:SA))\b", RegexOptions.IgnoreCase);

        // Autres labels d'en-tete connus (numero de compte, RIB, adresse, devise, periode,
        // agence, coordonnees...) : une ligne qui matche l'un d'eux n'est jamais un nom de
        // client/societe.
        private static readonly Regex OtherLabelLineRegex = new(
            @"^\s*(RIB|IBAN|N[°o]?\s*(du\s*|de\s*)?compte|Compte\s*:|Num[ée]ro\s*(du\s*|de\s*)?compte|Nature\s*(du\s*|de\s*)?compte|Adresse|Devise|P[ée]riode|Agence|SWIFT|BIC|T[ée]l|Fax|Email|Date|Heure|Extrait\s+de\s+Compte|Relev[ée]\s+de\s+Compte|Code\s*client|CIF|Domiciliation|Solde|Gestionnaire|C(?:om)?pte\s+Courant)\b",
            RegexOptions.IgnoreCase);

        private static bool LooksLikeAddressLine(string line)
        {
            string t = line.Trim();

            // "N°09 RUE ..." / "12 AVENUE ..." / "10 BD ..." (numero + type de voie)
            if (Regex.IsMatch(t, @"^(N[°o]\s*)?\d+[\s,]*(RUE|AVENUE|AV\.?|BD\b|BOULEVARD|ROUTE|IMPASSE|CIT[ée]|R[ée]SIDENCE|LOTISSEMENT)\b", RegexOptions.IgnoreCase))
                return true;

            // "2013 BEN AROUS" (code postal en debut de ligne suivi de la ville)
            if (Regex.IsMatch(t, @"^\d{3,5}\s+[A-ZÀ-Ÿ]", RegexOptions.IgnoreCase))
                return true;

            // "LA GOULETTE 2060" (ville suivie du code postal)
            if (Regex.IsMatch(t, @"^[A-ZÀ-Ÿ].*\b\d{3,5}\s*$", RegexOptions.IgnoreCase))
                return true;

            return false;
        }

        private static string CleanCustomerName(string raw) =>
            Regex.Replace(raw.Trim().Trim(':', '-', ' '), @"\s{2,}", " ");

        // Bloc numerique/code (numero de compte, fraction de periode "1/2024"...) en TETE de
        // ligne, suivi du vrai texte : fusion de colonnes OCR frequente quand la valeur d'un
        // champ label suit directement, sur la MEME ligne, le numero de compte et un fragment
        // de periode (ex. "8901769223 1/2024 1/13 EZB" - le code client "EZB" est le vrai
        // candidat, precede de bruit numerique). On ne garde que le texte apres ce bloc.
        private static readonly Regex LeadingNumericCodeRegex = new(@"^[\d\s./\-:]{4,}");

        private static string StripLeadingNumericCode(string line)
        {
            var m = LeadingNumericCodeRegex.Match(line);
            return m.Success ? line[m.Length..].Trim() : line;
        }

        // Reconnaissance d'un label connu present comme mot isole EN FIN de ligne, avec un
        // eventuel gloss anglais entre parentheses (ex. "Identifiant national de compte
        // bancaire RIB Titulaire du compte (Account Owner)" - releve BTK reel, ou le vrai label
        // "Titulaire du compte" est precede d'un prefixe non reconnu au lieu d'etre seul sur sa
        // ligne). Traite comme CustomerLabelOnlyLineRegex : la valeur est alors recherchee en
        // scannant vers l'avant. Seuls les labels "surs" (ColonOptionalLabelAlternatives, sans
        // "Compte de"/"Société"/"Entreprise" qui peuvent demarrer un nom d'entreprise) sont
        // concernes, pour les memes raisons que plus haut.
        private static readonly Regex[] LabelNearEndOfLineRegexes = ColonOptionalLabelAlternatives
            .Where(p => p != "To") // "To" en fin de ligne est trop ambigu (mot anglais courant en fin de phrase)
            .Select(p => new Regex($@"\b{p}\b\s*(\([^)]{{0,40}}\))?\s*$", RegexOptions.IgnoreCase))
            .ToArray();

        // Un des mots-labels connus present QUELQUE PART sur une ligne COURTE (<=6 mots), sans
        // pour autant que la ligne entiere soit reconnue comme le label pur (CustomerLabelOnlyLineRegex)
        // - typiquement une fusion/duplication OCR autour du mot (ex. "S Titulaire itulai de
        // compte" - releve BTL reel, ou "Titulaire" survit intact au milieu du bruit). Traite
        // comme un label seul sur sa ligne : declenche le meme scan vers l'avant pour la valeur.
        private static readonly Regex LooseLabelWordRegex = new(
            @"\b(Titulaire|Intitul[ée])\b", RegexOptions.IgnoreCase);

        // Tronque une valeur (avec ou sans label explicite) au premier marqueur generique de
        // fin de valeur trouve sur la ligne - date toutes lettres, heure, mention "Edité/Edite
        // le", champ "Période" suivi de sa propre date, date chiffree isolee, ou texte arabe.
        // Ces marqueurs indiquent qu'un AUTRE champ (periode, date d'impression, libelle
        // bilingue arabe...) a ete accole a la valeur par fusion de colonnes OCR - on tronque
        // plutot que de rejeter tout le candidat. Applique de facon identique quelle que soit
        // la strategie qui a produit le candidat (label explicite, marqueur de forme juridique,
        // ou repli sans label) : jamais specifique a un intitule ou une banque.
        private static string TruncateAtTrailingMarker(string value)
        {
            int cut = value.Length;
            foreach (var regex in new[]
                     {
                         SpelledDateRegex, TrailingPrintMarkerRegex, TrailingTimeRegex,
                         TrailingPeriodFieldRegex, TrailingNumericDateRegex, ArabicCharRegex,
                     })
            {
                var m = regex.Match(value);
                if (m.Success && m.Index < cut)
                    cut = m.Index;
            }
            return value[..cut].Trim();
        }

        // ----- Rejet generique de candidats (structure, jamais un nom de banque precis) ---
        //
        // Ces regles s'appliquent a TOUTE valeur candidate (qu'elle vienne d'un label explicite
        // ou du repli sans label) : une valeur qui a la forme d'un titre de document, d'un nom
        // d'etablissement bancaire, d'une date, ou de bruit OCR/texte arabe non latin n'est
        // jamais un nom de client - quelle que soit la banque. Rien ici ne cite un nom de
        // banque particulier (BIAT/BNA/ATB/UIB/...) : les regles portent uniquement sur la
        // FORME de la ligne (longueur, alphabet, presence du mot generique "banque/bank",
        // token court tout-majuscule sans espace, motif de date).

        private static readonly Regex ArabicCharRegex = new("[؀-ۿ]");

        // Titre de document generique, quelle que soit la banque : "Relevé (de/du) Compte",
        // "Extrait (de/du) Compte", "Statement (of Account)", "Bulletin de Compte", "Liste des
        // transactions". Le "de/du" est volontairement optionnel (l'OCR le perd parfois,
        // "RELEVE COMPTE"), et un qualificatif final ("MENSUEL", "ANNUEL"...) est tolere.
        private static readonly Regex DocumentTitleLineRegex = new(
            @"^\s*(RELEV[ÉE]\s*(DE\s+|DU\s+)?COMPTE|EXTRAIT\s*(DE\s+|DU\s+)?COMPTE|BULLETIN\s+DE\s+COMPTE|STATEMENT(\s+OF\s+ACCOUNT)?|ACCOUNT\s+STATEMENT|LISTE\s+DES\s+TRANSACTIONS)(\s+\w+)?\s*[:\-]?\s*$",
            RegexOptions.IgnoreCase);

        // Marqueur de pagination ("Page 7 of 3", "Page 1(28)", "Page 7 sur 4") : jamais un nom.
        private static readonly Regex PageMarkerRegex = new(
            @"^\s*Page\s+\d+\s*(of|sur|/|\()\s*\d+\)?\s*$", RegexOptions.IgnoreCase);

        // Mot generique "banque/bank" et ses derives usuels (bancaire, banking) - pas un nom
        // d'etablissement precis : une ligne qui le contient est l'en-tete de l'etablissement
        // emetteur, jamais le client.
        private static readonly Regex BankWordRegex = new(@"\b(BANQUE|BANCAIRES?|BANK|BANKING)\b", RegexOptions.IgnoreCase);

        private static readonly string[] MonthNames =
        {
            "janvier", "f[ée]vrier", "mars", "avril", "mai", "juin", "juillet",
            "ao[uû]t", "septembre", "octobre", "novembre", "d[ée]cembre",
        };

        private static readonly Regex SpelledDateRegex = new(
            $@"\d{{1,2}}\s+(?:{string.Join("|", MonthNames)})\s+\d{{4}}", RegexOptions.IgnoreCase);

        // allowShortAcronym : quand la valeur vient d'un label EXPLICITE et non ambigu sur la
        // meme ligne (Priorite 1, ex. "Client: EZB"), le label lui-meme garantit deja que ce
        // qui suit est le nom du client - le rejet de "sigle court" ne sert alors qu'a se
        // proteger d'un ramassage SANS label (Priorite 2/3), ou un sigle isole comme "UIB"/
        // "BIAT" est presque toujours celui de la banque plutot que celui d'un client. Sous un
        // label explicite, un identifiant court reste un nom de client legitime (observe en
        // pratique : "Client: EZB").
        private static bool IsRejectableCandidate(string candidate, bool allowShortAcronym = false)
        {
            string c = candidate.Trim();

            // Trop court pour etre un nom plausible (bruit OCR type "au", "g").
            if (c.Length < 3) return true;

            // Texte majoritairement/partiellement arabe : dans ces en-tetes, le nom du client
            // est toujours en caracteres latins - un melange avec de l'arabe est du bruit OCR.
            if (ArabicCharRegex.IsMatch(c)) return true;

            // Titre du document ("Relevé de Compte", "Extrait de Compte", "Statement",
            // "Liste des transactions"...).
            if (DocumentTitleLineRegex.IsMatch(c)) return true;

            // Marqueur de pagination ("Page 7 of 3", "Page 1(28)").
            if (PageMarkerRegex.IsMatch(c)) return true;

            // Nom de l'etablissement emetteur (contient le mot generique "banque"/"bank"/
            // "bancaire"/"banking").
            if (BankWordRegex.IsMatch(c)) return true;

            // Une date (chiffree "30/03/2024" ou en toutes lettres "30 mars 2024") n'est
            // jamais un nom de client.
            if (Regex.IsMatch(c, DateToken) || SpelledDateRegex.IsMatch(c)) return true;

            // Commence par une longue suite de chiffres (numero de compte/RIB/reference) :
            // un identifiant numerique n'est jamais un nom de client.
            if (Regex.IsMatch(c, @"^\d{6,}")) return true;

            // Ligne entierement composee de chiffres/espaces/separateurs (ex. numero de
            // compte imprime en groupes "00 00033029238", "12 000 00 00033029238 16") :
            // aucune lettre nulle part, jamais un nom.
            if (Regex.IsMatch(c, @"^[\d\s./-]{6,}$")) return true;

            // Forme IBAN (2 lettres + 2 chiffres + suite alphanumerique, ex. "TN59 12 000
            // 00 00033029238 16") : identifiant bancaire standardise, jamais un nom.
            if (Regex.IsMatch(c, @"^[A-Z]{2}\d{2}[\dA-Z\s]{6,}$")) return true;

            // Phrase (accroche/boilerplate) plutot qu'un nom : tous les noms de clients valides
            // observes font au plus 7 mots (ex. "UNION BANCAIRE POUR LE COMMERCE ET
            // LINDUSTRIE" = 7 mots) ; une ligne plus longue est une phrase, pas un nom propre.
            if (c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 9) return true;

            // Libelle compose ("Titulaire DU COMPTE", "Nom DU CLIENT"...) dont la reconnaissance
            // a ete cassee par un caractere parasite OCR au milieu ("Titulaire du 'compte'", ou
            // l'apostrophe empeche CustomerLabelOnlyLineRegex de matcher la ligne entiere, qui
            // atterrit alors telle quelle en Priorite 3b) : un residu court de la forme
            // "[Titulaire/Intitulé] (du|de|des|le|la) [quote] compte/client" n'est jamais un nom,
            // c'est le libelle du champ lui-meme, pas sa valeur. Le prefixe de label est
            // optionnel : couvre aussi bien le residu APRES qu'un label a deja ete strippe
            // ailleurs que la ligne de label brute non reconnue.
            if (c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3
                && Regex.IsMatch(c, @"^(Titulaire\s+|Intitul[ée]\s+)?(du|de|des|le|la|l['’])\s*['’""]?\s*(compte|client)\b", RegexOptions.IgnoreCase))
                return true;

            // Meme idee mais pour un residu qui se TERMINE par "compte"/"client" plutot que de
            // commencer par lui (ex. "e Compte" - releve BTL reel, reste de "le Compte"/"de
            // Compte" mange par le bruit OCR autour du vrai label) : aucun nom de client reel
            // n'observe ne se termine par ce mot - c'est toujours la fin du libelle du champ,
            // jamais sa valeur. Borne a un candidat court (<=4 mots) pour ne jamais rejeter un
            // nom d'entreprise legitime qui contiendrait "compte"/"client" en milieu de phrase.
            if (c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
                && Regex.IsMatch(c, @"(compte|client)['’""]?\s*$", RegexOptions.IgnoreCase))
                return true;

            // Sigle court tout-majuscule sans espace (ex. "ATB", "UIB", "BIAT") ramasse SANS
            // label explicite : presque toujours le sigle/logo de la banque plutot qu'un nom
            // de client (les 5 exemples de reference sont tous multi-mots). Ne s'applique pas
            // quand un label explicite garantit deja qu'il s'agit du client (voir allowShortAcronym).
            if (!allowShortAcronym && !c.Contains(' ') && c.Length <= 5 && Regex.IsMatch(c, @"^[A-Z0-9'.\-]+$"))
                return true;

            // Formule de politesse d'ouverture de lettre ("Cher client,", "Cher Client, nous
            // avons l'honneur...") : toujours du texte d'accroche, jamais un nom - generique a
            // tous les releves bancaires tunisiens observes (BIAT, AMEN, ABC...).
            if (Regex.IsMatch(c, @"^Cher\s+[Cc]lient\b", RegexOptions.IgnoreCase)) return true;

            // Reste isole d'un titre de document coupe par l'OCR sur deux lignes (ex. "RELEVE
            // COMPTE" / "DE MENSUEL" - le titre complet "RELEVE DE COMPTE MENSUEL" a ete
            // scinde, DocumentTitleLineRegex ne matche que la premiere moitie) : un qualificatif
            // de periodicite seul (avec ou sans "DE/DU" residuel devant) n'est jamais un nom.
            if (Regex.IsMatch(c, @"^\s*(DE|DU|DES)?\s*(MENSUEL|ANNUEL|TRIMESTRIEL)\.?\s*$", RegexOptions.IgnoreCase))
                return true;

            // Titre de section d'export "Transactions pour la période"/"Transactions for the
            // period" (fusionne par l'OCR avec le label "To" qui le precede sur les releves au
            // format export - ex. "To Transactions pour la période") : jamais un nom, c'est
            // l'intitule d'une section du document.
            if (Regex.IsMatch(c, @"Transactions?\b.{0,20}\b(p[ée]riode|period)\b", RegexOptions.IgnoreCase))
                return true;

            // Ni un mot d'au moins 3 lettres consecutives (un nom - personne ou societe -
            // contient toujours un tel mot), ni un sigle ponctue de la forme "E.Z.B"/"E .Z.B"
            // (lettres isolees separees par des points, motif de code client legitime observe
            // en pratique) : le candidat est alors du bruit OCR pur (ponctuation/caracteres
            // isoles, ex. "i,)", ou un mot outil court comme "au" accole a un chiffre "5 au").
            bool hasSubstantialWord = Regex.IsMatch(c, @"\p{L}{3,}");
            bool isDottedInitialism = Regex.IsMatch(c, @"^[A-Za-z](\s*[.\s]\s*[A-Za-z]){1,}\.?$");
            if (!hasSubstantialWord && !isDottedInitialism) return true;

            return false;
        }

        private static string? ExtractCustomerName(List<string> headerLines)
        {
            // Priorite 1 et 2 : label explicite, valeur sur la meme ligne ou (si le label est
            // seul sur sa ligne) sur la ligne suivante. Une valeur rejetee par le filtre
            // generique (titre de document, nom de banque, date, bruit OCR) est ignoree et la
            // recherche continue plutot que de s'arreter dessus.
            for (int i = 0; i < headerLines.Count; i++)
            {
                var m = CustomerLabelLineRegex.Match(headerLines[i]);
                if (m.Success)
                {
                    string sameLine = TruncateAtTrailingMarker(m.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(sameLine) && !IsRejectableCandidate(sameLine, allowShortAcronym: true))
                        return CleanCustomerName(sameLine);
                    continue;
                }

                // Label + ":" present mais PAS en debut de ligne (fusion de colonnes OCR avec
                // un AUTRE champ qui le precede, ex. "IBAN : TN59... Name: MOHAMED ZARRAGA
                // ZPA"). Meme validation que le cas ancre ci-dessus.
                var midLineMatch = CustomerLabelMidLineRegex.Match(headerLines[i]);
                if (midLineMatch.Success)
                {
                    string midLineValue = TruncateAtTrailingMarker(midLineMatch.Groups[1].Value.Trim());
                    if (!string.IsNullOrWhiteSpace(midLineValue) && !IsRejectableCandidate(midLineValue, allowShortAcronym: true))
                        return CleanCustomerName(midLineValue);
                    continue;
                }

                // Meme label, mais sans ":" (l'OCR l'a perdu) - uniquement pour le
                // sous-ensemble de labels surs sans ambiguite avec un debut de nom
                // d'entreprise (voir ColonOptionalLabelAlternatives). La valeur peut avoir un
                // autre champ (date d'impression, heure) accole par fusion de colonnes : on la
                // tronque au premier marqueur generique avant validation.
                bool triedValueForColonOptionalLabel = false;
                foreach (var (fullLine, withValue) in ColonOptionalLabelRegexes)
                {
                    if (fullLine.IsMatch(headerLines[i]))
                    {
                        // Le libelle complet (le plus specifique en premier) occupe toute la
                        // ligne sans valeur derriere : ne pas tenter un libelle plus court sur
                        // cette meme ligne - laisser CustomerLabelOnlyLineRegex plus bas s'en
                        // charger via le scan vers l'avant (meme comportement que pour "To").
                        break;
                    }

                    var withValueMatch = withValue.Match(headerLines[i]);
                    if (withValueMatch.Success)
                    {
                        triedValueForColonOptionalLabel = true;
                        string sameLine = TruncateAtTrailingMarker(withValueMatch.Groups[1].Value.Trim());
                        if (!string.IsNullOrWhiteSpace(sameLine) && !IsRejectableCandidate(sameLine, allowShortAcronym: true))
                            return CleanCustomerName(sameLine);
                        break;
                    }
                }
                if (triedValueForColonOptionalLabel) continue;

                // Le label occupe (a peu pres) toute la ligne, sans valeur derriere - trois
                // formes tolerees : la ligne EST exactement le label (CustomerLabelOnlyLineRegex,
                // ex. "To"), le label apparait en FIN de ligne avec un eventuel gloss anglais
                // entre parentheses (ex. "...RIB Titulaire du compte (Account Owner)" - releve
                // BTK reel), ou le mot-label est present quelque part sur une ligne courte sans
                // etre reconnu comme le libelle exact (ex. "S Titulaire itulai de compte" -
                // releve BTL reel, fusion/duplication OCR autour du mot). Dans les trois cas, la
                // valeur est recherchee en scannant vers l'avant.
                bool looksLikeLabelOnly = CustomerLabelOnlyLineRegex.IsMatch(headerLines[i])
                    || LabelNearEndOfLineRegexes.Any(r => r.IsMatch(headerLines[i]))
                    || (LooseLabelWordRegex.IsMatch(headerLines[i])
                        && headerLines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6);
                if (!looksLikeLabelOnly)
                    continue;

                // Le label est seul sur sa ligne : sa valeur n'est pas forcement sur la toute
                // prochaine ligne - une mise en page a colonnes fortement melangee par l'OCR
                // (ex. "TITULAIRE" suivi de plusieurs AUTRES labels avant la vraie valeur,
                // observe sur un releve UIB reel) peut l'eloigner de plusieurs lignes. On
                // scanne donc vers l'avant dans le reste de la zone d'en-tete et on applique
                // les memes filtres que la Priorite 3 (autre label connu, adresse, candidat
                // rejetable) plutot que de prendre aveuglement headerLines[i+1]. Un bloc
                // numerique/code en tete de la ligne candidate (numero de compte, fraction de
                // periode...) est retire avant validation : la vraie valeur peut le suivre
                // directement sur la meme ligne, fusionnee par l'OCR (ex. "8901769223 1/2024
                // 1/13 EZB" - releve BTL reel). allowShortAcronym: le label deja identifie
                // (meme imparfaitement) garantit que ce qui suit est le client, comme pour un
                // label explicite classique.
                for (int j = i + 1; j < headerLines.Count; j++)
                {
                    string next = headerLines[j].Trim();
                    if (string.IsNullOrWhiteSpace(next)) continue;
                    if (OtherLabelLineRegex.IsMatch(next)) continue;
                    if (LooksLikeAddressLine(next)) continue;

                    string nextCandidate = StripLeadingNumericCode(TruncateAtTrailingMarker(next));
                    if (string.IsNullOrWhiteSpace(nextCandidate)) continue;
                    if (IsRejectableCandidate(nextCandidate, allowShortAcronym: true)) continue;

                    return CleanCustomerName(nextCandidate);
                }
            }

            // Priorite 3a : un marqueur de forme juridique (STE, SOCIETE, SARL, SUARL, SA...)
            // present sur une ligne de la zone d'en-tete est un signal positif fort de nom
            // d'entreprise, quelle que soit sa position - on le prefere donc a n'importe
            // quelle autre ligne non marquee qui le precederait (ex. une ligne de bruit
            // d'en-tete de banque). Le marqueur n'est pas forcement en debut de ligne : une
            // fusion de colonnes OCR peut l'accoler a la fin d'une phrase sans rapport (ex.
            // "...bonne reception SOCIETE AUTOSET 7 PIECES AUTO") - on ne garde alors que le
            // texte a partir du marqueur, jamais le prefixe parasite qui le precede.
            // Structurel, jamais specifique a une banque.
            foreach (var line in headerLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var legalMatch = LegalEntityPrefixRegex.Match(line);
                if (!legalMatch.Success) continue;

                string candidateFromMarker = TruncateAtTrailingMarker(line[legalMatch.Index..].Trim());
                if (OtherLabelLineRegex.IsMatch(candidateFromMarker)) continue;
                if (LooksLikeAddressLine(candidateFromMarker)) continue;
                if (IsRejectableCandidate(candidateFromMarker)) continue;

                return CleanCustomerName(candidateFromMarker);
            }

            // Priorite 3b : aucun label reconnu explicitement - premiere ligne d'en-tete qui
            // n'est ni un autre label connu, ni une adresse, ni rejetee par le filtre
            // generique (titre de document / nom de banque / date / bruit OCR-arabe / sigle
            // court). On s'arrete a cette premiere ligne valide : jamais de concatenation avec
            // les suivantes (donc jamais l'adresse absorbee dans le nom). Si la ligne contient
            // malgre tout un ":" (un label non reconnu mais generiquement present, ex. OCR
            // ayant perdu le mot "Titulaire" avant "du compte :"), on prend la partie APRES le
            // ":" plutot que la ligne entiere - generique, pas specifique a un intitule precis.
            foreach (var line in headerLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Un label connu (RIB, Devise, N°/Numero de compte...) suivi directement d'un
                // code numerique peut, par fusion de colonnes OCR, avoir le vrai nom du client
                // accole juste apres ce code sur la MEME ligne (ex. "RIB : 08 105 00075 10
                // 00855 8 03 ZOUHEIR TRIMECHE MOBIL KRAM"). On tente d'extraire ce reste
                // alphabetique plutot que de jeter toute la ligne - generique, base uniquement
                // sur la structure "label + code numerique + texte", jamais sur un intitule
                // ou une banque precise.
                var labelPrefix = LabelThenNumericCodeRegex.Match(line);
                if (labelPrefix.Success)
                {
                    string trailing = TruncateAtTrailingMarker(line[labelPrefix.Length..].Trim());
                    if (!string.IsNullOrWhiteSpace(trailing)
                        && Regex.IsMatch(trailing, @"[A-Za-zÀ-ÿ]{2,}")
                        && !OtherLabelLineRegex.IsMatch(trailing)
                        && !LooksLikeAddressLine(trailing)
                        && !IsRejectableCandidate(trailing))
                        return CleanCustomerName(trailing);
                    continue;
                }

                // Tronque d'abord au premier marqueur generique (periode+date, date chiffree,
                // texte arabe...) avant meme les filtres label/adresse : sans cela, une date ou
                // un code postal accole en fin de ligne par fusion de colonnes OCR peut faire
                // ressembler a tort la ligne entiere a une adresse (LooksLikeAddressLine) alors
                // que seul le TEXTE APRES troncature (le vrai nom) doit etre juge sur ce critere
                // (ex. "CH CLEAN Période: 01/06/2025 30/06/2025" -> "CH CLEAN").
                string truncatedLine = TruncateAtTrailingMarker(line);

                if (OtherLabelLineRegex.IsMatch(truncatedLine)) continue;
                if (LooksLikeAddressLine(truncatedLine)) continue;

                string candidate = truncatedLine;
                int colonIndex = truncatedLine.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < truncatedLine.Length - 1)
                {
                    string afterColon = TruncateAtTrailingMarker(truncatedLine[(colonIndex + 1)..].Trim());
                    if (!string.IsNullOrWhiteSpace(afterColon))
                        candidate = afterColon;
                }

                if (!Regex.IsMatch(candidate, @"[A-Za-zÀ-ÿ]")) continue;

                // Le split apres ":" peut lui-meme reveler un AUTRE champ connu que le libelle
                // d'origine ne laissait pas voir (ex. "Categorie de Compte : COMPTE COURANT EN
                // TND" - "Categorie de Compte" n'est pas un label reconnu, mais sa valeur en
                // est un descriptif de type de compte generique) : on revalide le candidat
                // final avec les memes filtres que la ligne d'origine, pas seulement le filtre
                // generique de rejet.
                if (OtherLabelLineRegex.IsMatch(candidate)) continue;
                if (LooksLikeAddressLine(candidate)) continue;
                if (IsRejectableCandidate(candidate)) continue;

                return CleanCustomerName(candidate);
            }

            return null;
        }

        // Label de champ numerique connu (RIB/IBAN/Devise/N° ou Numero de compte), suivi
        // directement de son code (chiffres/espaces/ponctuation de separation) - utilise pour
        // detecter un eventuel nom de client accole juste apres par fusion de colonnes OCR.
        private static readonly Regex LabelThenNumericCodeRegex = new(
            @"^\s*(RIB|IBAN|Devise|N[°o]?\s*(du\s*|de\s*)?compte|Num[ée]ro\s*(du\s*|de\s*)?compte)\b\s*:?\s*[\d./\s-]*",
            RegexOptions.IgnoreCase);

        // ----- ExtractionPeriod ---------------------------------------------------------

        private const string DateToken = @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}";

        // Filler tolere entre "du"/"au" et leur date respective : des mots intercales par une
        // fusion de colonnes OCR (texte arabe, ponctuation parasite...) ne doivent pas
        // empecher de retrouver la date qui suit - borne (30 caracteres, jamais au-dela d'une
        // ligne via \n exclu) pour ne jamais deborder sur une autre partie du document.
        private const string DateFiller = @"[^\d\n]{0,30}?";

        private static readonly Regex PeriodDuAuRegex = new(
            $@"(?:Op[ée]rations\s+)?[Dd]u\b{DateFiller}({DateToken}){DateFiller}(?:[Aa]u|-){DateFiller}({DateToken})",
            RegexOptions.IgnoreCase);

        private static readonly Regex PeriodLabelRegex = new(
            $@"P[ée]riode\s*:?{DateFiller}({DateToken})\s*(?:-|[Aa]u){DateFiller}({DateToken})",
            RegexOptions.IgnoreCase);

        private static readonly Regex DuOnlyLineRegex = new($@"^\s*[Dd]u\s+({DateToken})\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex AuOnlyLineRegex = new($@"^\s*[Aa]u\s+({DateToken})\s*$", RegexOptions.IgnoreCase);

        // Variante ou l'OCR place la date AVANT le mot "Au" ("01/12/2025 Au" au lieu de "Au
        // 01/12/2025"), la seconde date se retrouvant seule sur la ligne suivante (ex. releve
        // UIB reel avec colonnes fortement melangees).
        private static readonly Regex DateThenAuOnlyLineRegex = new($@"^\s*({DateToken})\s*[Aa]u\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex LoneDateLineRegex = new($@"^\s*({DateToken})\s*$");

        // Periode mensuelle ("Du mois de Décembre 2024", "Mois de Décembre 2024") : un seul
        // mois nomme plutot que deux dates - Start/End sont calcules comme le 1er et le
        // dernier jour de ce mois (generique, aucune banque precise).
        private static readonly Regex MonthlyPeriodRegex = new(
            $@"(?:[Dd]u\s+)?[Mm]ois\s+de\s+({string.Join("|", MonthNames)})\s+(\d{{4}})",
            RegexOptions.IgnoreCase);

        // Date d'arret unique ("Relevé au 31/05/2025", "Solde au 30/04/2025") : pas
        // d'intervalle, seule une date de fin est imprimee - Start reste null.
        private static readonly Regex StatementAsOfDateRegex = new(
            $@"(?:Relev[ée]|Solde)\s+au\s*:?\s*({DateToken})", RegexOptions.IgnoreCase);

        // Dernier recours absolu : un simple "au <date>" (avec filler tolere), sans le "du
        // <date>" qui le precede normalement - couvre le cas ou la date de DEBUT est
        // completement detruite par l'OCR (aucun motif ne peut la retrouver, elle n'existe
        // plus dans le texte) mais la date de FIN, elle, a survecu. Start reste null.
        private static readonly Regex BareAuDateRegex = new(
            $@"\b[Aa]u\b{DateFiller}({DateToken})", RegexOptions.IgnoreCase);

        // Retrouve l'index 1-12 du nom de mois capture (accent-tolerant, insensible a la
        // casse) dans MonthNames - retourne 0 si non reconnu (ne devrait pas arriver, le
        // motif appelant est construit a partir de ce meme tableau).
        private static int MonthNameToNumber(string monthName)
        {
            for (int idx = 0; idx < MonthNames.Length; idx++)
                if (Regex.IsMatch(monthName, $@"^(?:{MonthNames[idx]})$", RegexOptions.IgnoreCase))
                    return idx + 1;
            return 0;
        }

        private static ExtractionPeriod? ExtractPeriod(List<string> headerLines)
        {
            string joinedHeader = string.Join("\n", headerLines);

            var m = PeriodDuAuRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = m.Groups[1].Value, End = m.Groups[2].Value };

            m = PeriodLabelRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = m.Groups[1].Value, End = m.Groups[2].Value };

            m = MonthlyPeriodRegex.Match(joinedHeader);
            if (m.Success)
            {
                int month = MonthNameToNumber(m.Groups[1].Value);
                int year = int.Parse(m.Groups[2].Value);
                if (month >= 1 && month <= 12)
                {
                    int lastDay = DateTime.DaysInMonth(year, month);
                    return new ExtractionPeriod
                    {
                        Start = $"01/{month:D2}/{year:D4}",
                        End = $"{lastDay:D2}/{month:D2}/{year:D4}"
                    };
                }
            }

            // Les deux dates sur deux lignes separees ("Du <date>" puis "Au <date>").
            for (int i = 0; i < headerLines.Count - 1; i++)
            {
                var duMatch = DuOnlyLineRegex.Match(headerLines[i]);
                if (!duMatch.Success) continue;

                var auMatch = AuOnlyLineRegex.Match(headerLines[i + 1]);
                if (auMatch.Success)
                    return new ExtractionPeriod { Start = duMatch.Groups[1].Value, End = auMatch.Groups[1].Value };
            }

            // Variante inversee : "<date> Au" sur une ligne, puis la seconde date seule sur
            // la ligne suivante.
            for (int i = 0; i < headerLines.Count - 1; i++)
            {
                var dateAuMatch = DateThenAuOnlyLineRegex.Match(headerLines[i]);
                if (!dateAuMatch.Success) continue;

                var loneDateMatch = LoneDateLineRegex.Match(headerLines[i + 1]);
                if (loneDateMatch.Success)
                    return new ExtractionPeriod { Start = dateAuMatch.Groups[1].Value, End = loneDateMatch.Groups[1].Value };
            }

            // Dernier recours : une seule date d'arret imprimee ("Relevé au ...", "Solde au
            // ..."), sans intervalle - Start reste null, seul End est renseigne.
            m = StatementAsOfDateRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = null, End = m.Groups[1].Value };

            m = BareAuDateRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = null, End = m.Groups[1].Value };

            return null;
        }

        // ----- Point d'entree -------------------------------------------------------------

        public DocumentMetadata Extract(string fullText, List<TableRow> rows)
        {
            var headerLines = GetHeaderZoneLines(rows, fullText);

            var customerName = ExtractCustomerName(headerLines);
            var period = ExtractPeriod(headerLines);

            Console.WriteLine($"[METADATA] CustomerName='{customerName}' Period.Start='{period?.Start}' Period.End='{period?.End}' (headerLines={headerLines.Count})");

            return new DocumentMetadata
            {
                CustomerName = customerName,
                Period = period
            };
        }
    }
}
