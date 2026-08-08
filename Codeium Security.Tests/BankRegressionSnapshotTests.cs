using System.Text.Json;
using Codeium_Security.Models;
using Xunit;

namespace Codeium_Security.Tests
{
    // Harnais de non-regression pour les banques deja fonctionnelles (BIAT, BTK, QNB,
    // Attijari, Zitouna, Amen). Fait tourner le VRAI pipeline (OCR Tesseract inclus, aucun
    // mock) sur chaque PDF de reference et compare le resultat au snapshot fige dans
    // TrainingData/RawResults (sortie du pipeline actuel, prise comme reference de
    // non-regression - pas comme verite terrain independante : voir discussion avec
    // l'utilisateur avant creation de ce fichier).
    //
    // Perimetre volontairement limite aux PDF qui ont deja un snapshot exploitable :
    // - present sur disque (verifie manuellement, aucun fichier manquant/vide)
    // - snapshot correspondant a au moins un compte reellement extrait (un snapshot
    //   "Accounts: []" ne protege aucun comportement et a ete exclu : c'est le cas de
    //   "BIAT EXTRAIT 02.pdf", deja en echec aujourd'hui pour la meme raison que les
    //   nouvelles banques - dates au format "04 JAN 24", non supporte par NormalizeDate).
    // - "QNB MED 06-24.pdf" egalement exclu : investigation (voir conversation) a confirme que
    //   le snapshot fige ne contenait qu'1 compte alors que le document en a reellement 2.
    //   Cause identifiee : lastSeenAccountNumber (utilise pour nommer une nouvelle section a
    //   l'ouverture) est pollue par un numero de reference de transaction au meme format que
    //   le numero de compte (\d{2,5}-\d{4,12}-\d{1,4}), imprime a cote de chaque operation dans
    //   ce releve - la 2e section herite alors du numero de la 1ere au lieu du sien. PAS encore
    //   corrige : une premiere tentative de correctif specifique-QNB a ete revertee sur demande
    //   explicite (l'objectif est une refactorisation generique de BankDocumentParser, pas
    //   d'empiler des branches isQnb/isBiat/isBtk supplementaires). A traiter naturellement par
    //   cette refactorisation, sinon en correctif dedie ensuite. Le snapshot existant est donc
    //   lui-meme incomplet ; a re-capturer une fois le bug resolu, avant de reintegrer ce fichier.
    // - "AMEN BQList.pdf" et "BANK-TND (1)btk.pdf" exclus (verifie via DIAG-BuildTable) : un
    //   fragment date isole sur sa propre ligne (ex. "27072025", "30/04/2025"), juste apres une
    //   transaction deja complete, etait auparavant silencieusement perdu par
    //   BankDocumentParser (meme classe de bug que celui corrige pour QNB/BTK plus haut - un
    //   fragment "date seule" ecrasait pendingDate au lieu d'etre traite comme texte). Corrige
    //   aujourd'hui : ce fragment est desormais rattache au libelle de la transaction
    //   precedente - un gain de completude, pas une regression - mais le snapshot fige
    //   (capture avant ce correctif) ne le contient pas encore. A re-capturer avant de
    //   reintegrer ces fichiers. "BANK-TND (1)btk.pdf" etait le dernier fichier BTK du harnais :
    //   la couverture BTK est donc a 0 pour l'instant, a restaurer avec un nouveau snapshot.
    // Ne pas etendre cette liste silencieusement : toute banque/fichier ajoute doit d'abord
    // etre confirme comme produisant reellement des transactions avec le code actuel.
    [Collection("Pipeline")]
    public class BankRegressionSnapshotTests
    {
        private readonly PipelineFixture _fixture;

        public BankRegressionSnapshotTests(PipelineFixture fixture)
        {
            _fixture = fixture;
        }

        public static IEnumerable<object[]> BaselineFiles()
        {
            string[] files =
            {
                // BIAT (11)
                "BIAT -02.pdf",
                "BIAT 01-24.pdf",
                "BIAT 09-24.pdf",
                "biat 01-2023.pdf",
                "biat 02-2023.pdf",
                "biat 03-2023.pdf",
                "biat 05-2023.pdf",
                "biat 06-2023.pdf",
                "biat 07-2023.pdf",
                "biat 09-2023.pdf",
                "biat 10-2023.pdf",
                // BTK (0) - BANK-TNDbtk.pdf et BANK-TND (1)btk.pdf tous deux exclus, voir
                // commentaire au-dessus. Couverture BTK a restaurer avec un nouveau snapshot.
                // QNB (2) - QNB MED 06-24.pdf exclu, voir commentaire au-dessus
                "QNB  MED 05-24.pdf",
                "QNB MED 07-24.pdf",
                // Attijari (2)
                "0082693425_20241202092613 ATTIJARI.pdf",
                "attijari banque.pdf",
                // Zitouna (1)
                "extraitzitouna.pdf",
                // Amen (0) - AMEN BQList.pdf exclu, voir commentaire au-dessus. Couverture Amen
                // a restaurer avec un nouveau snapshot.
            };

            foreach (var f in files)
                yield return new object[] { f };
        }

        [Theory]
        [MemberData(nameof(BaselineFiles))]
        public async Task Parsing_Matches_Frozen_Snapshot(string fileName)
        {
            string pdfPath = Path.Combine(RepoPaths.SourceDocumentsDir, fileName);
            Assert.True(File.Exists(pdfPath), $"PDF baseline introuvable : {pdfPath}");

            var expectedAccounts = LoadExpectedAccounts(fileName);
            Assert.True(expectedAccounts.Count > 0,
                $"Aucun snapshot exploitable pour '{fileName}' - ce fichier ne devrait pas faire partie du harnais.");

            var result = await _fixture.ProcessingService.ProcessFileAsync(pdfPath, fileName);

            Assert.True(result.Document is BankDocument,
                $"[{fileName}] Le document n'a pas ete classifie/parse comme document bancaire (Document = {result.Document?.GetType().Name ?? "null"}).");
            var bankDoc = (BankDocument)result.Document!;

            Assert.True(bankDoc.Accounts.Count == expectedAccounts.Count,
                $"[{fileName}] Nombre de comptes/sections different : attendu {expectedAccounts.Count} " +
                $"({string.Join(", ", expectedAccounts.Select(e => Key(e.Account.AccountNumber)))}), obtenu {bankDoc.Accounts.Count} " +
                $"({string.Join(", ", bankDoc.Accounts.Select(a => Key(a.AccountNumber)))}).");

            // Comptes non-vides mis en correspondance par numero de compte (stable, independant
            // de tout algorithme de nommage de fichier). Comptes a numero vide ("compte_inconnu")
            // mis en correspondance par ordre d'apparition, seul critere disponible dans ce cas.
            var expectedByAccount = expectedAccounts.Where(e => !string.IsNullOrWhiteSpace(e.Account.AccountNumber))
                .ToDictionary(e => e.Account.AccountNumber, e => e);
            var expectedEmpty = expectedAccounts.Where(e => string.IsNullOrWhiteSpace(e.Account.AccountNumber)).ToList();
            int emptyIndex = 0;

            foreach (var actual in bankDoc.Accounts)
            {
                SnapshotFile expected;
                if (!string.IsNullOrWhiteSpace(actual.AccountNumber))
                {
                    Assert.True(expectedByAccount.TryGetValue(actual.AccountNumber, out expected!),
                        $"[{fileName}] Aucun snapshot ne correspond au numero de compte obtenu '{actual.AccountNumber}'. " +
                        $"Comptes attendus : {string.Join(", ", expectedByAccount.Keys)}.");
                }
                else
                {
                    Assert.True(emptyIndex < expectedEmpty.Count,
                        $"[{fileName}] Compte sans numero obtenu en trop (position {emptyIndex}), aucun snapshot correspondant.");
                    expected = expectedEmpty[emptyIndex++];
                }

                AssertAccountMatches(fileName, expected, actual, bankDoc.BankName);
            }
        }

        private static void AssertAccountMatches(string fileName, SnapshotFile expected, BankAccountSection actual, string actualBankName)
        {
            string ctx = $"[{fileName} / compte {Key(expected.Account.AccountNumber)}]";

            Assert.True(expected.BankName == actualBankName,
                $"{ctx} BankName: attendu '{expected.BankName}', obtenu '{actualBankName}'.");

            Assert.True(expected.Account.Currency == actual.Currency,
                $"{ctx} Currency: attendu '{expected.Account.Currency}', obtenu '{actual.Currency}'.");

            Assert.True(expected.Account.Rib == actual.Rib,
                $"{ctx} Rib: attendu '{expected.Account.Rib}', obtenu '{actual.Rib}'.");

            Assert.True(expected.Account.SoldeInitial == actual.SoldeInitial,
                $"{ctx} SoldeInitial: attendu {Fmt(expected.Account.SoldeInitial)}, obtenu {Fmt(actual.SoldeInitial)}.");

            Assert.True(expected.Account.SoldeFinal == actual.SoldeFinal,
                $"{ctx} SoldeFinal: attendu {Fmt(expected.Account.SoldeFinal)}, obtenu {Fmt(actual.SoldeFinal)}.");

            Assert.True(expected.Account.Transactions.Count == actual.Transactions.Count,
                $"{ctx} Nombre de transactions: attendu {expected.Account.Transactions.Count}, obtenu {actual.Transactions.Count}.");

            int n = Math.Min(expected.Account.Transactions.Count, actual.Transactions.Count);
            for (int i = 0; i < n; i++)
            {
                var e = expected.Account.Transactions[i];
                var a = actual.Transactions[i];
                Assert.True(e.Date == a.Date, $"{ctx} Transaction #{i} Date: attendu '{e.Date}', obtenu '{a.Date}'.");

                // Tolerance sur Libelle uniquement : confirme sur BANK-TNDbtk.pdf que le meme
                // glyphe isole (avant "VIREMENT EMIS AUT BQ") est lu differemment ("?&", "5", "?B"
                // ...) MEME PLUSIEURS FOIS DANS LE MEME RUN OCR - bruit OCR pur sur un caractere
                // non-textuel, jamais une erreur de logique de parsing. Date/Debit/Credit restent
                // en egalite stricte : ce sont les champs qui comptent financierement.
                int libelleDistance = LevenshteinDistance(e.Libelle, a.Libelle);
                Assert.True(libelleDistance <= 2,
                    $"{ctx} Transaction #{i} Libelle: attendu '{e.Libelle}', obtenu '{a.Libelle}' (distance d'edition {libelleDistance} > 2).");

                Assert.True(e.Debit == a.Debit, $"{ctx} Transaction #{i} Debit: attendu {Fmt(e.Debit)}, obtenu {Fmt(a.Debit)}.");
                Assert.True(e.Credit == a.Credit, $"{ctx} Transaction #{i} Credit: attendu {Fmt(e.Credit)}, obtenu {Fmt(a.Credit)}.");
            }
        }

        private static string Fmt(decimal? v) => v.HasValue ? v.Value.ToString("0.###") : "null";
        private static string Key(string accountNumber) => string.IsNullOrWhiteSpace(accountNumber) ? "<sans numero>" : accountNumber;

        private static int LevenshteinDistance(string a, string b)
        {
            a ??= "";
            b ??= "";
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[a.Length, b.Length];
        }

        // Charge tous les snapshots "un compte par fichier" pour ce PDF et les deduplique par
        // numero de compte (en gardant la version la plus recente) : certains fichiers de
        // TrainingData/RawResults sont des reexecutions successives du meme document, pas des
        // comptes distincts (verifie manuellement : QNB MED 06-24_..._v2.json est un doublon
        // byte-identique de QNB MED 06-24_....json).
        private static List<SnapshotFile> LoadExpectedAccounts(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var byAccountKey = new Dictionary<string, (DateTime Mtime, SnapshotFile Snapshot)>();
            int anonymousCounter = 0;

            foreach (var path in Directory.EnumerateFiles(RepoPaths.RawResultsDir, $"{stem}_*.json"))
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<SnapshotFile>(json);
                if (snapshot is null || snapshot.Account is null)
                    continue; // forme "0 compte" (DocumentProcessingResult complet) : rien a comparer

                // Limite connue : si un meme document anonyme (numero de compte vide) etait un
                // jour reexecute plusieurs fois (comme QNB MED 06-24 ci-dessus pour un compte
                // nomme), chaque fichier serait ici traite comme un compte anonyme DISTINCT au
                // lieu d'etre deduplique. Non applicable aux 20 fichiers de ce harnais (verifie
                // manuellement : un seul fichier "*_compte_inconnu.json" par stem), a revisiter
                // si un futur fichier anonyme avec plusieurs snapshots est ajoute.
                string key = string.IsNullOrWhiteSpace(snapshot.Account.AccountNumber)
                    ? $"__anonyme_{anonymousCounter++}"
                    : snapshot.Account.AccountNumber;

                var mtime = File.GetLastWriteTimeUtc(path);
                if (!byAccountKey.TryGetValue(key, out var existing) || mtime > existing.Mtime)
                    byAccountKey[key] = (mtime, snapshot);
            }

            return byAccountKey.Values
                .OrderBy(v => v.Snapshot.Account.AccountNumber)
                .Select(v => v.Snapshot)
                .ToList();
        }
    }
}
