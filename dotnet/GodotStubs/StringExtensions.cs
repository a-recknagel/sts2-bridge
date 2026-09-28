namespace Godot;

public static class StringExtensions
{
    // Preserve Godot resource/user prefixes rather than using OS path semantics.
    public static string PathJoin(this string instance, string file)
    {
        if (instance.Length == 0) return file;
        if (instance.EndsWith('/') || file.StartsWith('/')) return instance + file;
        return instance + "/" + file;
    }

    // Godot's String.capitalize: split snake_case and camelCase into words, title-case each, join with spaces.
    public static string Capitalize(this string instance)
    {
        var spaced = System.Text.RegularExpressions.Regex.Replace(instance.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
        return string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
    }
}
