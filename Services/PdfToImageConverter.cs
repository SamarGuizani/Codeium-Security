using Docnet.Core;
using Docnet.Core.Models;
using System.Drawing;
using System.Drawing.Imaging;

namespace Codeium_Security.Services
{
    public class PdfToImageConverter
    {
        // Convertit chaque page d'un PDF en fichier PNG, retourne la liste des chemins créés
        public List<string> ConvertPdfToImages(string pdfPath, string outputFolder)
        {
            var imagePaths = new List<string>();

            Directory.CreateDirectory(outputFolder);

            using var docReader = DocLib.Instance.GetDocReader(
                pdfPath,
                new PageDimensions(1920, 2560)); // haute résolution pour un meilleur OCR

            int pageCount = docReader.GetPageCount();

            for (int i = 0; i < pageCount; i++)
            {
                using var pageReader = docReader.GetPageReader(i);
                var rawBytes = pageReader.GetImage();
                int width = pageReader.GetPageWidth();
                int height = pageReader.GetPageHeight();

                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, bitmapData.Scan0, rawBytes.Length);
                bitmap.UnlockBits(bitmapData);

                string outputPath = Path.Combine(outputFolder, $"page_{i + 1}.png");
                bitmap.Save(outputPath, ImageFormat.Png);

                imagePaths.Add(outputPath);
            }

            return imagePaths;
        }

        public bool IsPdf(string fileName) =>
            Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
    }
}