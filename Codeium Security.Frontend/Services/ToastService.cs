namespace Codeium_Security.Frontend.Services;

public enum ToastType { Success, Error, Info }

public class ToastMessage
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Text { get; set; } = string.Empty;
    public ToastType Type { get; set; } = ToastType.Info;
}

// Service d'affichage utilitaire purement frontend (aucune donnee reseau).
public class ToastService
{
    public List<ToastMessage> Messages { get; } = new();
    public event Action? OnChange;

    public void Show(string text, ToastType type = ToastType.Info)
    {
        var toast = new ToastMessage { Text = text, Type = type };
        Messages.Add(toast);
        OnChange?.Invoke();
        _ = AutoDismiss(toast.Id);
    }

    private async Task AutoDismiss(Guid id)
    {
        await Task.Delay(4000);
        Dismiss(id);
    }

    public void Dismiss(Guid id)
    {
        Messages.RemoveAll(m => m.Id == id);
        OnChange?.Invoke();
    }
}
