using Codeium_Security.OCR;
using Codeium_Security.Services;
using Codeium_Security.Services.DocumentParsers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// Swagger UI (l'interface visuelle de test)
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<IOcrService, TesseractOcrService>();
builder.Services.AddScoped<BankDocumentParser>();
builder.Services.AddScoped<DocumentAnalysisEngine>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseSwagger();
app.UseSwaggerUI();

// app.UseHttpsRedirection(); // désactivé temporairement pour éviter l'erreur de port pendant le dev

app.UseAuthorization();
app.MapControllers();

app.Run();