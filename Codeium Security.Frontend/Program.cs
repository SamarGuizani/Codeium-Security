using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Codeium_Security.Frontend;
using Codeium_Security.Frontend.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Services frontend uniquement : aucune donnee ne provient d'un appel reseau.
// La connexion au backend (HttpClient + endpoints API) sera ajoutee plus tard,
// voir Codeium Security.Frontend/README.md (section "TODO - Backend Integration").
builder.Services.AddScoped<ThemeService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<AppStateService>();
builder.Services.AddScoped<MockAnalysisService>();
builder.Services.AddScoped<MockHistoryService>();

await builder.Build().RunAsync();
