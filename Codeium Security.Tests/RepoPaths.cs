namespace Codeium_Security.Tests
{
    // Localise le dossier du projet principal ("Codeium Security", celui qui contient
    // TrainingData/SourceDocuments et TrainingData/RawResults) en remontant depuis le
    // repertoire de sortie de l'assembly de test. Evite tout chemin relatif fige du type
    // "../../../.." qui casse selon la configuration de build (Debug/Release) ou l'IDE.
    internal static class RepoPaths
    {
        private static readonly Lazy<string> MainProjectDirLazy = new(FindMainProjectDir);

        public static string MainProjectDir => MainProjectDirLazy.Value;

        public static string SourceDocumentsDir => Path.Combine(MainProjectDir, "TrainingData", "SourceDocuments");

        public static string RawResultsDir => Path.Combine(MainProjectDir, "TrainingData", "RawResults");

        private static string FindMainProjectDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null)
            {
                if (HasSourcePdfs(dir.FullName))
                    return dir.FullName;

                var sibling = Path.Combine(dir.FullName, "Codeium Security");
                if (HasSourcePdfs(sibling))
                    return sibling;

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                $"Impossible de localiser TrainingData/SourceDocuments (avec de vrais PDF) en remontant depuis '{AppContext.BaseDirectory}'.");
        }

        // Le SDK Web du projet principal copie automatiquement les *.json de
        // TrainingData/RawResults (et un fichier egare) vers bin/<Config>/net8.0 des qu'un
        // projet le reference - mais PAS les *.pdf. Verifier la seule existence du dossier
        // "TrainingData/SourceDocuments" matche donc a tort cette copie partielle (sans PDF)
        // avant meme de commencer a remonter l'arborescence. On exige la presence d'au moins
        // un vrai PDF pour distinguer le vrai dossier source de cette copie de build.
        private static bool HasSourcePdfs(string candidateDir)
        {
            var sourceDocs = Path.Combine(candidateDir, "TrainingData", "SourceDocuments");
            return Directory.Exists(sourceDocs) && Directory.EnumerateFiles(sourceDocs, "*.pdf").Any();
        }
    }
}
