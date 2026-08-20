using Codeium_Security.Frontend.Models;

namespace Codeium_Security.Frontend.Services;

// Historique conserve en memoire pour la duree de la session frontend.
// Aucune donnee n'est pre-remplie : tant que le backend n'est pas connecte,
// cette liste reste vide plutot que d'afficher des documents inventes.
// Pas de persistance, pas d'appel backend.
public class MockHistoryService
{
    private readonly List<HistoryItem> _items = new();

    public List<HistoryItem> GetAll() => _items;

    public void Remove(Guid id) => _items.RemoveAll(i => i.Id == id);
}
