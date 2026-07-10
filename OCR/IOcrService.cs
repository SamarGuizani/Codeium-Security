using System.Threading.Tasks;
namespace Codeium_Security.OCR
{
   
        public interface IOcrService
        {
            Task<OcrResult> ExtractTextAsync(string imagePath);
        }
    }

