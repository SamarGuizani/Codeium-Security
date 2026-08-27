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

                    lines.Add(joined);
                }
            }

            if (lines.Count == 0)
            {
                // Repli : aucune TableRow exploitable, ou la ligne d'en-tete de tableau est
                // la toute premiere ligne (rien avant elle) - on retombe sur les premieres
                // lignes du texte brut plutot que de ne rien avoir a analyser.
                lines = (fullText ?? "")
                    .Split('\n')
                    .Select(l => l.Trim())
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
            @"Nom\s+du\s+client",
            @"Client",
            @"Soci[ée]t[ée]",
            @"Entreprise",
            @"Compte\s+de",
            @"Account\s*holder",
            @"Customer",
            @"To",
        };

        // Priorite 1 : label + ":" + valeur sur la meme ligne. Le ":" est volontairement
        // obligatoire ici : sans lui, un label comme "Societe" matcherait aussi en debut
        // du nom d'une entreprise qui commence par ce mot (ex. "SOCIETE AUTOSET 7 PIECES
        // AUTO"), en avalant "SOCIETE" comme prefixe de label au lieu de faire partie du nom.
        private static readonly Regex CustomerLabelLineRegex = new(
            $@"^\s*(?:{string.Join("|", CustomerLabelAlternatives)})\b\s*:\s*(.*)$",
            RegexOptions.IgnoreCase);

        // Priorite 2 : la ligne entiere n'est QUE le label (avec ou sans ":" final, sans
        // valeur derriere) - la valeur est alors sur la ligne suivante (ex. "To" / "CH CLEAN").
        private static readonly Regex CustomerLabelOnlyLineRegex = new(
            $@"^\s*(?:{string.Join("|", CustomerLabelAlternatives)})\s*:?\s*$",
            RegexOptions.IgnoreCase);

        // Autres labels d'en-tete connus (numero de compte, RIB, adresse, devise, periode,
        // agence, coordonnees...) : une ligne qui matche l'un d'eux n'est jamais un nom de
        // client/societe.
        private static readonly Regex OtherLabelLineRegex = new(
            @"^\s*(RIB|IBAN|N[°o]?\s*(du\s*|de\s*)?compte|Compte\s*:|Num[ée]ro\s*(du\s*|de\s*)?compte|Adresse|Devise|P[ée]riode|Agence|SWIFT|BIC|T[ée]l|Fax|Email|Extrait\s+de\s+Compte|Relev[ée]\s+de\s+Compte|Code\s*client|CIF|Domiciliation|Solde)\b",
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

        private static string? ExtractCustomerName(List<string> headerLines)
        {
            // Priorite 1 et 2 : label explicite, valeur sur la meme ligne ou (si le label est
            // seul sur sa ligne) sur la ligne suivante.
            for (int i = 0; i < headerLines.Count; i++)
            {
                var m = CustomerLabelLineRegex.Match(headerLines[i]);
                if (m.Success)
                {
                    string sameLine = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(sameLine))
                        return CleanCustomerName(sameLine);
                    continue;
                }

                if (!CustomerLabelOnlyLineRegex.IsMatch(headerLines[i]))
                    continue;

                if (i + 1 < headerLines.Count)
                {
                    string next = headerLines[i + 1].Trim();
                    if (!string.IsNullOrWhiteSpace(next))
                        return CleanCustomerName(next);
                }
            }

            // Priorite 3 : aucun label reconnu explicitement - premiere ligne d'en-tete qui
            // n'est ni un autre label connu, ni une adresse, ni vide de toute lettre. On
            // s'arrete a cette premiere ligne : jamais de concatenation avec les suivantes
            // (donc jamais l'adresse absorbee dans le nom). Si la ligne contient malgre tout
            // un ":" (un label non reconnu mais generiquement present, ex. OCR ayant perdu le
            // mot "Titulaire" avant "du compte :"), on prend la partie APRES le ":" plutot
            // que la ligne entiere - generique, pas specifique a un intitule precis.
            foreach (var line in headerLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (OtherLabelLineRegex.IsMatch(line)) continue;
                if (LooksLikeAddressLine(line)) continue;

                string candidate = line;
                int colonIndex = line.IndexOf(':');
                if (colonIndex >= 0 && colonIndex < line.Length - 1)
                {
                    string afterColon = line[(colonIndex + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(afterColon))
                        candidate = afterColon;
                }

                if (!Regex.IsMatch(candidate, @"[A-Za-zÀ-ÿ]")) continue;

                return CleanCustomerName(candidate);
            }

            return null;
        }

        // ----- ExtractionPeriod ---------------------------------------------------------

        private const string DateToken = @"\d{1,2}[/\-.]\d{1,2}[/\-.]\d{2,4}";

        private static readonly Regex PeriodDuAuRegex = new(
            $@"(?:Op[ée]rations\s+)?[Dd]u\s+({DateToken})\s+(?:[Aa]u|-)\s+({DateToken})",
            RegexOptions.IgnoreCase);

        private static readonly Regex PeriodLabelRegex = new(
            $@"P[ée]riode\s*:?\s*({DateToken})\s*(?:-|[Aa]u)\s*({DateToken})",
            RegexOptions.IgnoreCase);

        private static readonly Regex DuOnlyLineRegex = new($@"^\s*[Dd]u\s+({DateToken})\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex AuOnlyLineRegex = new($@"^\s*[Aa]u\s+({DateToken})\s*$", RegexOptions.IgnoreCase);

        private static ExtractionPeriod? ExtractPeriod(List<string> headerLines)
        {
            string joinedHeader = string.Join("\n", headerLines);

            var m = PeriodDuAuRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = m.Groups[1].Value, End = m.Groups[2].Value };

            m = PeriodLabelRegex.Match(joinedHeader);
            if (m.Success)
                return new ExtractionPeriod { Start = m.Groups[1].Value, End = m.Groups[2].Value };

            // Les deux dates sur deux lignes separees ("Du <date>" puis "Au <date>").
            for (int i = 0; i < headerLines.Count - 1; i++)
            {
                var duMatch = DuOnlyLineRegex.Match(headerLines[i]);
                if (!duMatch.Success) continue;

                var auMatch = AuOnlyLineRegex.Match(headerLines[i + 1]);
                if (auMatch.Success)
                    return new ExtractionPeriod { Start = duMatch.Groups[1].Value, End = auMatch.Groups[1].Value };
            }

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
