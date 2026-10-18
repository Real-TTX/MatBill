using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace MatBill.Localization;

/// <summary>The marker type of the interface texts: <c>IStringLocalizer&lt;SharedResource&gt;</c>, injected into every view as <c>L</c>.</summary>
public sealed class SharedResource;

/// <summary>
/// The texts of the interface, English and German side by side, from the JSON files in <c>Resources/Texts</c> (embedded in the assembly).
/// One file per area; a key is <c>Area.Thing</c> and lives in exactly one file:
/// <code>{ "Invoices.Title": { "en": "Invoices", "de": "Rechnungen" } }</code>
/// Placeholders are <c>{0}</c>, <c>{1}</c> (string.Format). A key that is missing comes back as the key itself, which a test catches before it ships.
/// </summary>
public static class TextCatalog
{
    private static readonly Lazy<FrozenDictionary<string, FrozenDictionary<string, string>>> All = new(Load);

    /// <summary>Key → (language → text).</summary>
    public static FrozenDictionary<string, FrozenDictionary<string, string>> Entries => All.Value;

    public static string? Find(string key, string language)
    {
        if (!All.Value.TryGetValue(key, out var texts))
        {
            return null;
        }

        return texts.TryGetValue(language, out var text) ? text : texts.GetValueOrDefault(Languages.Default);
    }

    /// <summary>Every file and its keys (the tests check that each key has every language and lives in one file only).</summary>
    public static IEnumerable<(string File, string Key, IReadOnlyDictionary<string, string> Texts)> Files()
    {
        var assembly = typeof(TextCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(IsText).Order(StringComparer.Ordinal))
        {
            foreach (var (key, texts) in Read(assembly, name))
            {
                yield return (name, key, texts);
            }
        }
    }

    private static bool IsText(string resource) => resource.Contains(".Resources.Texts.", StringComparison.Ordinal) && resource.EndsWith(".json", StringComparison.Ordinal);

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var result = new Dictionary<string, FrozenDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (_, key, texts) in Files())
        {
            result[key] = texts.ToFrozenDictionary(StringComparer.Ordinal);
        }

        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static Dictionary<string, Dictionary<string, string>> Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? [];
    }
}

/// <summary>Reads <see cref="TextCatalog"/> in the language of the current request (<see cref="CultureInfo.CurrentUICulture"/>).</summary>
public sealed class CatalogLocalizer : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            var text = TextCatalog.Find(name, Languages.Current);
            return new LocalizedString(name, text ?? name, resourceNotFound: text is null);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = this[name];
            return new LocalizedString(name, string.Format(Languages.FormatCulture, found.Value, arguments), found.ResourceNotFound);
        }
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        => TextCatalog.Entries.Keys.Select(k => this[k]);
}

public sealed class CatalogLocalizer<T> : IStringLocalizer<T>
{
    private readonly CatalogLocalizer _inner = new();

    public LocalizedString this[string name] => _inner[name];

    public LocalizedString this[string name, params object[] arguments] => _inner[name, arguments];

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => _inner.GetAllStrings(includeParentCultures);
}

/// <summary>Every localizer of the application (views, data annotations, services) reads the one catalog.</summary>
public sealed class CatalogLocalizerFactory : IStringLocalizerFactory
{
    private static readonly CatalogLocalizer Shared = new();

    public IStringLocalizer Create(Type resourceSource) => Shared;

    public IStringLocalizer Create(string baseName, string location) => Shared;
}

/// <summary>
/// The texts for code that has no injection at hand (static helpers, enums, exceptions): <c>Text.L["Common.Save"]</c>.
/// It reads the language of the current request; outside of a request (the schedulers) the default language.
/// </summary>
public static class Text
{
    public static IStringLocalizer L { get; } = new CatalogLocalizer();

    /// <summary>Shorthand: <c>Text.T("Invoices.Issued", number)</c>.</summary>
    public static string T(string key, params object[] arguments) => arguments.Length == 0 ? L[key].Value : L[key, arguments].Value;
}
