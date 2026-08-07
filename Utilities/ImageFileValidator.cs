using Tesseract;

namespace Codeium_Security.Utilities
{
    public class ImageValidationResult
    {
        public bool IsValid { get; set; }
        public string Error { get; set; } = "";
    }

    public static class ImageFileValidator
    {
        private static readonly string[] SupportedExtensions =
            { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp" };

        public static ImageValidationResult Validate(string filePath, string originalFileName)
        {
            string ext = Path.GetExtension(originalFileName).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext))
                return Invalid($"Format d'image non supporte : '{ext}'.");

            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
                return Invalid($"Fichier introuvable : {filePath}");

            if (fileInfo.Length == 0)
                return Invalid("Le fichier image est vide (0 octet).");

            // Tenter un chargement reel via Leptonica est le moyen le plus fiable de
            // detecter un fichier corrompu ou tronque (une extension valide ne garantit rien).
            try
            {
                using var pix = Pix.LoadFromFile(filePath);
                if (pix.Width <= 0 || pix.Height <= 0)
                    return Invalid("Image invalide : dimensions nulles.");
            }
            catch (Exception ex)
            {
                return Invalid($"Image corrompue ou illisible : {ex.Message}");
            }

            return new ImageValidationResult { IsValid = true };
        }

        private static ImageValidationResult Invalid(string error) =>
            new() { IsValid = false, Error = error };
    }
}
