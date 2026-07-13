using System.Text.RegularExpressions;

namespace Codeium_Security.Utilities
{
    public static class TextCleaner
    {
        // Retire les marques de direction de texte invisibles (arabe/hébreu RTL, etc.)
        // qui cassent les regex car elles se glissent avant les chiffres/lettres attendus
        public static string RemoveInvisibleMarks(string text)
        {
            return Regex.Replace(text,
                @"[\u200B-\u200F\u202A-\u202E\u2060-\u2064\uFEFF]", "");
        }
    }
}