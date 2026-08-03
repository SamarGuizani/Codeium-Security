using Docnet.Core;
using Docnet.Core.Models;
using System.Drawing;
using System.Drawing.Imaging;

namespace Codeium_Security.Services
{
    public class PdfToImageConverter
    {
        private readonly IConfiguration _configuration;

        public PdfToImageConverter(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public List<string> ConvertPdfToImages(string pdfPath, string outputFolder)
        {
            // Nettoyage : supprime les anciennes images d'un test précédent sur ce même fichier
            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }
            Directory.CreateDirectory(outputFolder);

            var imagePaths = new List<string>();

            using var docReader = DocLib.Instance.GetDocReader(
                pdfPath,
                //star hetha besh nafs5ou le 3 aout car btk 
                //new PageDimensions(1920, 2560));
                new PageDimensions(3.0));
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
