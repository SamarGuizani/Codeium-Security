using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Codeium_Security.Frontend;
using Codeium_Security.Frontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<AppStateService>();
builder.Services.AddScoped<MockAnalysisService>();
builder.Services.AddScoped<MockHistoryService>();

// HttpClient vers le backend reel (Codeium Security.csproj, profil "http" -> voir
// Properties/launchSettings.json du backend). Utilise par OcrApiService pour les pages
// Excel (/) et OCR (/ocr) : le reste du frontend (Documents/Analyse/Resultats/Historique)
// reste sur les services mock existants.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri("http://localhost:5268/") });
builder.Services.AddScoped<OcrApiService>();

await builder.Build().RunAsync();
