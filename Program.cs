using Codeium_Security.Factories;
using Codeium_Security.Interfaces;
using Codeium_Security.OCR;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentClassification;
using Codeium_Security.Services.DocumentExtraction;
using Codeium_Security.Services.DocumentParsers;
using Codeium_Security.Services.Export;
Codeium_Security.Tools.TestParser.RunTests();
Codeium_Security.Tools.TestImageExtraction.RunTests();
Codeium_Security.Tools.TestSkewGrouping.RunTests();


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
// Swagger UI (l'interface visuelle de test)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IOcrService, TesseractOcrService>();
builder.Services.AddScoped<IDocumentParser, BankDocumentParser>();
builder.Services.AddScoped<DocumentAnalysisEngine>();
builder.Services.AddScoped<PdfToImageConverter>();
builder.Services.AddScoped<IDocumentClassifier, KeywordDocumentClassifier>();
builder.Services.AddScoped<IDocumentParser, InvoiceDocumentParser>();
builder.Services.AddScoped<IDocumentParser, ReceiptDocumentParser>();
builder.Services.AddScoped<DocumentParserFactory>();
builder.Services.AddScoped<ImageFormatConverter>();
builder.Services.AddScoped<IDocumentParser, AcademicDocumentParser>();
builder.Services.AddScoped<ImagePreprocessor>();
builder.Services.AddScoped<IDocumentExtractor, PdfDocumentExtractor>();
builder.Services.AddScoped<IDocumentExtractor, ImageDocumentExtractor>();
builder.Services.AddScoped<DocumentProcessingService>();
builder.Services.AddScoped<BankExcelExporter>();


var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

// app.UseHttpsRedirection(); // d�sactiv� temporairement pour �viter l'erreur de port pendant le dev

app.UseAuthorization();
app.MapControllers();

app.Run();