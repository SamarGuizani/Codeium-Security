using Codeium_Security.Models;
using Codeium_Security.Services;
using Xunit;

namespace Codeium_Security.Tests
{
    // Verifie, via le VRAI pipeline (DocumentProcessingService.ProcessFileAsync, OCR
    // Tesseract inclus), que DocumentMetadata.BankName reprend exactement la valeur deja
    // calculee par BankDocumentParser (BankDocument.BankName) - aucune seconde detection de
    // banque, juste le branchement fait dans DocumentProcessingService.
    [Collection("Pipeline")]
    public class MetadataBankNameWiringTest
    {
        private readonly PipelineFixture _fixture;
        public MetadataBankNameWiringTest(PipelineFixture fixture) => _fixture = fixture;

        private const string SamplePath = @"C:\Users\USER\OneDrive\Desktop\Allfiles\0082693425_20241202092613 ATTIJARI.pdf";

        [Fact]
        public async Task Metadata_BankName_MatchesBankDocument_BankName()
        {
            if (!File.Exists(SamplePath))
                return; // Fichier d'echantillon absent sur cette machine : ne bloque pas la suite.

            var result = await _fixture.ProcessingService.ProcessFileAsync(SamplePath, Path.GetFileName(SamplePath));

            var bankDoc = Assert.IsType<BankDocument>(result.Document);
            Assert.Equal("Attijari Bank", bankDoc.BankName);
            Assert.NotNull(result.Metadata);
            Assert.Equal(bankDoc.BankName, result.Metadata!.BankName);
            Assert.Equal("BLUE TUNISIE", result.Metadata.CustomerName);
        }
    }
}
