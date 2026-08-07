using System.Drawing;
using System.Drawing.Imaging;
using Codeium_Security.Services;
using Codeium_Security.Utilities;

namespace Codeium_Security.Tools
{
    // Programme de test isole (meme esprit que TestParser.cs) : verifie le validateur
    // d'image et le pretraitement sans dependre d'un vrai scan BIAT ni d'un serveur HTTP.
    public static class TestImageExtraction
    {
        public static void RunTests()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "codeium_security_selftest");
            Directory.CreateDirectory(tempDir);

            CheckInvalid("fichier inexistant", ImageFileValidator.Validate(
                Path.Combine(tempDir, "ne_existe_pas.png"), "ne_existe_pas.png"));

            string unsupportedPath = Path.Combine(tempDir, "document.docx");
            File.WriteAllText(unsupportedPath, "contenu factice");
            CheckInvalid("extension non supportee", ImageFileValidator.Validate(unsupportedPath, "document.docx"));

            string emptyPath = Path.Combine(tempDir, "vide.png");
            File.WriteAllBytes(emptyPath, Array.Empty<byte>());
            CheckInvalid("fichier vide", ImageFileValidator.Validate(emptyPath, "vide.png"));

            string validPath = Path.Combine(tempDir, "valide.png");
            using (var bmp = new Bitmap(400, 200))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.DrawString("BIAT TEST 123,456", new Font("Arial", 16), Brushes.Black, 10, 80);
                bmp.Save(validPath, ImageFormat.Png);
            }

            var validResult = ImageFileValidator.Validate(validPath, "valide.png");
            Console.WriteLine(validResult.IsValid
                ? "[TestImageExtraction] PASS image valide correctement acceptee"
                : $"[TestImageExtraction] FAIL image valide rejetee: {validResult.Error}");

            try
            {
                var config = new ConfigurationBuilder().Build();
                var preprocessor = new ImagePreprocessor(config);
                string outputPath = preprocessor.Preprocess(validPath);
                Console.WriteLine(File.Exists(outputPath)
                    ? $"[TestImageExtraction] PASS pretraitement produit un fichier: {outputPath}"
                    : "[TestImageExtraction] FAIL pretraitement n'a produit aucun fichier");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TestImageExtraction] FAIL pretraitement a leve une exception: {ex.Message}");
            }
        }

        private static void CheckInvalid(string label, ImageValidationResult result)
        {
            Console.WriteLine(!result.IsValid
                ? $"[TestImageExtraction] PASS {label} correctement rejete ({result.Error})"
                : $"[TestImageExtraction] FAIL {label} aurait du etre rejete");
        }
    }
}
