using Codeium_Security.Factories;
using Codeium_Security.Interfaces;
using Codeium_Security.OCR;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentClassification;
using Codeium_Security.Services.DocumentExtraction;
using Codeium_Security.Services.DocumentMetadataExtraction;
using Codeium_Security.Services.DocumentParsers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Codeium_Security.Tests
{
    // Assemble le VRAI pipeline (OCR Tesseract inclus, aucun mock) exactement comme
    // Program.cs le fait via l'injection de dependances, mais sans hote ASP.NET Core.
    // Un seul TesseractEngine est cree pour toute la suite : sa construction est couteuse
    // (chargement des modeles eng+fra+ara) et TesseractOcrService le protege deja par un
    // verrou pour les appels concurrents.
    public class PipelineFixture : IDisposable
    {
        public DocumentProcessingService ProcessingService { get; }

        // Exposes additionnelles (purement additives, ProcessingService reste inchange) pour les
        // tests de diagnostic qui ont besoin de rejouer seulement l'etape de parsing (classification
        // + parsing + sommes) sans refaire l'OCR Tesseract a chaque execution.
        public DocumentAnalysisEngine AnalysisEngine { get; }
        public IDocumentClassifier Classifier { get; }
        public DocumentParserFactory ParserFactory { get; }

        public PipelineFixture()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Valeurs alignees sur appsettings.json du projet principal.
                    ["Ocr:Languages"] = "eng+fra+ara",
                    ["Ocr:PageSegMode"] = "4",
                })
                .Build();

            IOcrService ocrService = new TesseractOcrService(configuration);
            var analysisEngine = new DocumentAnalysisEngine();
            var pdfConverter = new PdfToImageConverter(configuration);

            // Seul l'extracteur PDF est necessaire : tous les documents baseline sont des PDF.
            IEnumerable<IDocumentExtractor> extractors = new IDocumentExtractor[]
            {
                new PdfDocumentExtractor(ocrService, pdfConverter),
            };

            IDocumentClassifier classifier = new KeywordDocumentClassifier();

            // Seul BankDocumentParser est necessaire pour ces tests.
            var parserFactory = new DocumentParserFactory(new IDocumentParser[]
            {
                new BankDocumentParser(),
            });

            ProcessingService = new DocumentProcessingService(
                ocrService,
                analysisEngine,
                extractors,
                classifier,
                parserFactory,
                configuration,
                new GenericDocumentMetadataExtractor());

            AnalysisEngine = analysisEngine;
            Classifier = classifier;
            ParserFactory = parserFactory;
        }

        public void Dispose()
        {
            // TesseractEngine (via IOcrService) n'expose pas de Dispose dans TesseractOcrService
            // actuellement : rien a liberer explicitement ici tant que ce n'est pas ajoute.
        }
    }

    [CollectionDefinition("Pipeline")]
    public class PipelineCollection : ICollectionFixture<PipelineFixture>
    {
    }
}
