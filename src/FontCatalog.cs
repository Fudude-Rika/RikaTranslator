using System.Drawing.Text;

namespace GameTranslateToolkit;

public static class FontCatalog
{
    public sealed record Entry(string Family, string DisplayName, string[] Aliases);

    public static Entry[] Installed()
    {
        using var collection = new InstalledFontCollection();
        var entries = new List<Entry>();
        foreach (var font in collection.Families)
        {
            using (font)
            {
                string Name(int language) { try { return font.GetName(language); } catch (ArgumentException) { return font.Name; } }
                var family = Name(1033);
                var displayName = Name(2052);
                var aliases = new[] { family, displayName, Name(1028), font.Name }
                    .Where(n => !string.IsNullOrWhiteSpace(n) && n.Length <= 90 && !n.Any(char.IsControl))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (aliases.Length == 0 || !aliases.Contains(family)) continue;
                entries.Add(new(family, displayName, aliases));
            }
        }
        return entries.DistinctBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
