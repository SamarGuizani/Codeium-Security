namespace Codeium_Security.Models
{
    public class LineOutput
    {
        public string LineText { get; set; } = "";
        public List<WordCoordinate> Words { get; set; } = new();
    }
}