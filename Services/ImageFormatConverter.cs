using System.Drawing;
using System.Drawing.Imaging;

namespace Codeium_Security.Services
{
    public class ImageFormatConverter
    {
        // Formats que Tesseract sait lire nativement
        private static readonly string[] SupportedExtensions =
            { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif" };

        public bool NeedsConversion(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return !SupportedExtensions.Contains(ext);
        }

        public string ConvertToPng(string sourcePath)
        {
            string outputPath = Path.ChangeExtension(sourcePath, ".png");

            using var image = Image.FromFile(sourcePath);
            image.Save(outputPath, ImageFormat.Png);

            return outputPath;
        }
    }
}