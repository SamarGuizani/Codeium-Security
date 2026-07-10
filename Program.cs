using Codeium_Security.OCR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

// Swagger : génère la doc + l'interface
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddScoped<IOcrService, TesseractOcrService>();

var app = builder.Build();

// Active Swagger UI toujours (pas seulement en Development, pour l'instant, le temps de tester)
app.UseSwagger();
app.UseSwaggerUI();

// On désactive temporairement HTTPS redirect (comme noté dans ton doc, ça bloquait)
// app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();

app.Run();