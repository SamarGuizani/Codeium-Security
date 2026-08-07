using Codeium_Security.Interfaces;
using Codeium_Security.OCR;
using Codeium_Security.Utilities;
using Tesseract;

namespace Codeium_Security.Services.DocumentExtraction
{
    // Meme resultat (OcrResult avec List<OcrWord>) que PdfDocumentExtractor : le parser
    // ne fait aucune difference entre les deux sources.
    public class ImageDocumentExtractor : IDocumentExtractor
    {
        private readonly IOcrService _ocrService;
        private readonly DocumentAnalysisEngine _analysisEngine;
        private readonly ImageFormatConverter _formatConverter;
        private readonly ImagePreprocessor _preprocessor;
        private readonly PdfToImageConverter _pdfConverter;

        public ImageDocumentExtractor(
            IOcrService ocrService,
            DocumentAnalysisEngine analysisEngine,
            ImageFormatConverter formatConverter,
            ImagePreprocessor preprocessor,
            PdfToImageConverter pdfConverter)
        {
            _ocrService = ocrService;
            _analysisEngine = analysisEngine;
            _formatConverter = formatConverter;
            _preprocessor = preprocessor;
            _pdfConverter = pdfConverter;
        }

        // Prend en charge tout ce qui n'est pas un PDF (comportement identique a l'ancien
        // "else" de DocumentProcessingService.RunOcrOnlyAsync).
        public bool CanHandle(string fileName) => !_pdfConverter.IsPdf(fileName);

        public async Task<OcrResult> ExtractAsync(string filePath, string originalFileName)
        {
            var validation = ImageFileValidator.Validate(filePath, originalFileName);
            if (!validation.IsValid)
            {
                Console.WriteLine($"[ImageDocumentExtractor] ERREUR validation '{originalFileName}': {validation.Error}");
                throw new InvalidDataException(validation.Error);
            }

            if (_formatConverter.NeedsConversion(originalFileName))
            {
                filePath = _formatConverter.ConvertToPng(filePath);
            }

            string preprocessedPath = _preprocessor.Preprocess(filePath);

            var swOcr = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[TIMER] --> Debut OCR image: {preprocessedPath}");
            var ocrResult = await _ocrService.ExtractTextAsync(preprocessedPath);
            ocrResult = await CorrectRotationIfNeeded(preprocessedPath, ocrResult);
            ocrResult.SuggestedVerticalTolerance = _analysisEngine.EstimateVerticalTolerance(ocrResult.Words);
            Console.WriteLine($"[TIMER] <-- Fin OCR image: {preprocessedPath} : {swOcr.ElapsedMilliseconds} ms");

            ocrResult.PageTexts = new List<string> { ocrResult.FullText };
            ocrResult.PageCount = 1;
            return ocrResult;
        }

        // Detecte une rotation ~90 degres via une propriete purement geometrique des boites OCR :
        // un mot latin/arabe horizontal est presque toujours plus large que haut. Si une nette
        // majorite des mots sont plus hauts que larges, l'image est tres probablement pivotee.
        // Aucune valeur specifique a une image/banque -- fonctionne pour n'importe quel scan.
        // Mesure sur des cas reels : image correcte = 5-11% de mots "etroits", image pivotee a
        // 90 = 76%. Sert UNIQUEMENT a decider s'il faut tenter une rotation -- la SELECTION du
        // meilleur candidat se fait par confiance OCR (voir plus bas), pas par ce ratio : un
        // ratio "ameliore" ne garantit pas un texte lisible (teste, une mauvaise rotation peut
        // avoir des boites moins "etroites" tout en produisant un texte totalement illisible).
        private async Task<OcrResult> CorrectRotationIfNeeded(string imagePath, OcrResult original)
        {
            if (!LooksRotated(original.Words))
                return original;

            Console.WriteLine("[ImageDocumentExtractor] Signature de rotation detectee (mots plus hauts que larges), tentative de correction...");

            OcrResult best = original;
            float bestConfidence = original.Confidence;

            foreach (int direction in new[] { 1, -1 })
            {
                string? rotatedPath = TryRotateImage(imagePath, direction);
                if (rotatedPath == null) continue;

                try
                {
                    var rotatedResult = await _ocrService.ExtractTextAsync(rotatedPath);
                    Console.WriteLine($"[ImageDocumentExtractor] Apres rotation {direction * 90} degres : confiance OCR={rotatedResult.Confidence:F1} (reference avant correction: {bestConfidence:F1})");
                    if (rotatedResult.Confidence > bestConfidence)
                    {
                        best = rotatedResult;
                        bestConfidence = rotatedResult.Confidence;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ImageDocumentExtractor] Echec OCR apres rotation {direction * 90} degres: {ex.Message}");
                }
            }

            return best;
        }

        private static bool LooksRotated(List<OcrWord> words)
        {
            if (words.Count < 10) return false;
            return NarrowWordFraction(words) > 0.5;
        }

        private static double NarrowWordFraction(List<OcrWord> words)
        {
            var valid = words.Where(w => (w.Right - w.Left) > 0 && (w.Bottom - w.Top) > 0).ToList();
            if (valid.Count == 0) return 0;
            int narrow = valid.Count(w => (double)(w.Right - w.Left) / (w.Bottom - w.Top) < 0.8);
            return (double)narrow / valid.Count;
        }

        private static string? TryRotateImage(string sourcePath, int direction)
        {
            try
            {
                using var pix = Pix.LoadFromFile(sourcePath);
                using var rotated = pix.Rotate90(direction);
                string outputPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".",
                    Path.GetFileNameWithoutExtension(sourcePath) + $"_rot{direction}.png");
                rotated.Save(outputPath, ImageFormat.Png);
                return outputPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImageDocumentExtractor] Echec rotation image: {ex.Message}");
                return null;
            }
        }
    }
}
