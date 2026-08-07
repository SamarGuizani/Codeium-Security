using Tesseract;

namespace Codeium_Security.Services
{
    // Pretraitement d'image pour les scans (JPG/PNG) avant OCR : niveaux de gris,
    // binarisation adaptative (contraste + bruit) et redressement (deskew).
    // Reutilise Leptonica, deja embarque dans le package Tesseract existant :
    // aucune nouvelle dependance d'imagerie n'est introduite.
    public class ImagePreprocessor
    {
        private readonly bool _enabled;
        private readonly int _minWidth;
        private readonly int _sauvolaWindowHalfSize;
        private readonly float _sauvolaFactor;
        private readonly bool _deskew;

        public ImagePreprocessor(IConfiguration configuration)
        {
            _enabled = configuration.GetValue("Ocr:Preprocessing:Enabled", true);
            _minWidth = configuration.GetValue("Ocr:Preprocessing:MinWidth", 1500);
            _sauvolaWindowHalfSize = configuration.GetValue("Ocr:Preprocessing:SauvolaWindowHalfSize", 25);
            _sauvolaFactor = configuration.GetValue("Ocr:Preprocessing:SauvolaFactor", 0.35f);
            _deskew = configuration.GetValue("Ocr:Preprocessing:Deskew", true);
        }

        // Retourne le chemin d'une image PNG pretraitee. Si le pretraitement echoue pour
        // une raison quelconque (image trop inhabituelle pour une etape Leptonica, etc.),
        // on retombe sur le fichier source original plutot que de bloquer l'OCR.
        public string Preprocess(string sourcePath)
        {
            if (!_enabled)
                return sourcePath;

            Pix? original = null, scaled = null, gray = null, binarized = null, deskewed = null;
            try
            {
                original = Pix.LoadFromFile(sourcePath);
                Pix current = original;

                if (current.Width > 0 && current.Width < _minWidth)
                {
                    float scale = (float)_minWidth / current.Width;
                    scaled = current.Scale(scale, scale);
                    current = scaled;
                }

                if (current.Depth > 8)
                {
                    gray = current.ConvertRGBToGray();
                    current = gray;
                }
            
                binarized = current.BinarizeSauvola(_sauvolaWindowHalfSize, _sauvolaFactor, false);
                current = binarized;
                // Pas de binarisation pour le test
                binarized = null;
                if (_deskew)
                 {
                     deskewed = current.Deskew();
                     current = deskewed;
                 }
               

                string outputPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                    Path.GetFileNameWithoutExtension(sourcePath) + "_preprocessed.png");
                current.Save(outputPath, ImageFormat.Png);

                Console.WriteLine($"[ImagePreprocessor] {Path.GetFileName(sourcePath)} pretraite -> {Path.GetFileName(outputPath)}");
                return outputPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImagePreprocessor] WARN pretraitement echoue pour {sourcePath}, OCR sur l'original. Raison: {ex.Message}");
                return sourcePath;
            }
            finally
            {
                original?.Dispose();
                scaled?.Dispose();
                gray?.Dispose();
                binarized?.Dispose();
                deskewed?.Dispose();
            }
        }
    }
}
