using Microsoft.JSInterop;

namespace Codeium_Security.Frontend.Services;

// Bascule visuelle claire/sombre : manipule uniquement l'attribut data-theme du DOM.
// Aucun appel reseau, aucune dependance backend.
public class ThemeService
{
    private readonly IJSRuntime _js;
    public bool IsDark { get; private set; }
    public event Action? OnChange;

    public ThemeService(IJSRuntime js) => _js = js;

    public async Task SetDarkAsync(bool isDark)
    {
        IsDark = isDark;
        await _js.InvokeVoidAsync(
            "document.documentElement.setAttribute",
            "data-theme",
            isDark ? "dark" : "light");
        OnChange?.Invoke();
    }
}
