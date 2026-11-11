using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PhoneCam;

static class UiLocale
{
    static readonly ConcurrentDictionary<string, JsonDocument> Dictionaries = new(StringComparer.Ordinal);

    public static bool IsSupported(string? language) => language is "it" or "en" or "fa";

    public static string Normalize(string? language) => IsSupported(language) ? language! : "en";

    public static string Get(string? language, string key, params (string Name, object? Value)[] values)
    {
        string normalized = Normalize(language);
        string template = Find(Dictionaries.GetOrAdd(normalized, Load), key)
            ?? Find(Dictionaries.GetOrAdd("en", Load), key)
            ?? throw new InvalidOperationException($"Missing UI locale key: {key}");

        foreach (var (name, value) in values)
            template = template.Replace($"{{{name}}}", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal);
        return template;
    }

    static JsonDocument Load(string language)
    {
        string expectedName = $"payload/wwwroot/locales/{language}.json";
        Assembly assembly = Assembly.GetExecutingAssembly();
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => string.Equals(name.Replace('\\', '/'), expectedName, StringComparison.Ordinal));
        using Stream stream = resourceName == null
            ? throw new InvalidOperationException($"Missing embedded UI locale: {expectedName}")
            : assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Unable to open embedded UI locale: {resourceName}");
        return JsonDocument.Parse(stream);
    }

    static string? Find(JsonDocument document, string key)
    {
        JsonElement element = document.RootElement;
        foreach (string part in key.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
