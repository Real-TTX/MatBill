using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatBill.UiKit;

/// <summary>The version of this build: "1.0.42-20261007", "nightly-42-20261007" or "local-20261007" (see Directory.Build.props).</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "local" : informational.Split('+')[0];
    }
}

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, status pills, numbers, cells.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> or in the app's own sprite.</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>
    /// The logo tile of MatBill, by size: small and medium tiles (the menu) draw the plain receipt from the sprite, large ones (sign-in, setup, error pages)
    /// the detailed logo with its positions, total and the badge of recurring invoices; details below about 40 px are only noise.
    /// </summary>
    public static IHtmlContent Logo(string size = "md")
        => size is "lg" or "xl"
            ? new HtmlString($"<span class=\"mat-logo mat-logo--{size} mb-logo--detailed\"><img src=\"/logo.svg\" alt=\"MatBill\" width=\"56\" height=\"56\" /></span>")
            : new HtmlString(
                $"<span class=\"mat-logo mat-logo--{size}\" role=\"img\" aria-label=\"MatBill\">" +
                "<svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-mb-logo\"></use></svg></span>");

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name) => Services.Fmt.Initials(name);

    /// <summary>A status as a pill with a word and, optionally, an icon; <paramref name="tone"/> is ok, warn, error, info, busy, accent, plain or empty.</summary>
    public static IHtmlContent Pill(string text, string tone = "", string? icon = null)
        => new HtmlString($"<span class=\"mat-pill{(string.IsNullOrEmpty(tone) ? "" : " mat-pill--" + tone)}\">{(icon is null ? "" : Icon(icon).ToHtml())}{WebUtility.HtmlEncode(text)}</span>");

    /// <summary>
    /// A number on a card (the facts at the top of a page). <paramref name="note"/> says what it means; with <paramref name="bad"/> the note is a warning.
    /// A card with an address is a link. The icon names the fact for the eye; it stands quietly in the label, not as a tile.
    /// </summary>
    public static IHtmlContent Kpi(string label, string value, string? note, string icon, string tone = "accent", string? href = null, bool bad = false)
    {
        var tag = href is null ? "div" : "a";
        var link = href is null ? "" : $" href=\"{WebUtility.HtmlEncode(href)}\"";
        var noteHtml = string.IsNullOrWhiteSpace(note) ? "" : $"<span class=\"mat-stat__delta{(bad ? " mat-stat__delta--down" : "")}\">{WebUtility.HtmlEncode(note)}</span>";
        return new HtmlString(
            $"<{tag} class=\"mat-card mb-stat{(href is null ? "" : " mb-stat--link")}\"{link}><div class=\"mat-card__body mat-stat\">" +
            $"<span class=\"mat-stat__label\">{Icon(icon).ToHtml()}{WebUtility.HtmlEncode(label)}</span><span class=\"mat-stat__value\">{WebUtility.HtmlEncode(value)}</span>{noteHtml}</div></{tag}>");
    }

    /// <summary>Encoded text for a table cell; empty for null.</summary>
    public static IHtmlContent Text(string? text) => new HtmlString(WebUtility.HtmlEncode(text ?? string.Empty));

    /// <summary>A quiet dash for "nothing here".</summary>
    public static IHtmlContent None() => new HtmlString("<span class=\"mat-muted mb-none\">–</span>");

    /// <summary>A link in a table cell.</summary>
    public static IHtmlContent Link(string text, string href)
        => new HtmlString($"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>");

    /// <summary>A name with a second, quieter line below it.</summary>
    public static IHtmlContent NameWithSub(string text, string? href, string? sub)
    {
        var name = href is null ? WebUtility.HtmlEncode(text) : $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>";
        var small = string.IsNullOrWhiteSpace(sub) ? "" : $"<small class=\"mat-muted mb-sub\">{WebUtility.HtmlEncode(sub)}</small>";
        return new HtmlString($"<div class=\"mat-table__name mb-name\"><span>{name}</span>{small}</div>");
    }

    public static IHtmlContent Mono(string? text) => new HtmlString($"<span class=\"mat-mono\">{WebUtility.HtmlEncode(text ?? string.Empty)}</span>");

    /// <summary>An amount in a cell: in the format of the interface language, with the euro sign, tabular figures.</summary>
    public static IHtmlContent Money(decimal amount) => new HtmlString($"<span class=\"mb-money\">{WebUtility.HtmlEncode(Services.Fmt.Money(amount))}</span>");

    /// <summary>Several pieces of markup after each other (a status pill and a second line, for example).</summary>
    public static IHtmlContent Concat(params IHtmlContent[] parts) => new HtmlString(string.Concat(parts.Select(p => p.ToHtml())));

    /// <summary>The round avatar with the initials of a person; the name is the tooltip and the label.</summary>
    public static IHtmlContent Avatar(string? name, string size = "")
    {
        var cls = string.IsNullOrEmpty(size) ? "mat-avatar" : $"mat-avatar mat-avatar--{size}";
        var shown = string.IsNullOrWhiteSpace(name) ? Localization.Text.T("Ui.Nobody") : name;
        return new HtmlString($"<span class=\"{cls}\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(shown)}\" data-tip=\"{WebUtility.HtmlEncode(shown)}\">{WebUtility.HtmlEncode(Services.Fmt.Initials(name))}</span>");
    }

    /// <summary>A person with the avatar, for a table cell.</summary>
    public static IHtmlContent Person(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? None()
            : new HtmlString($"<span class=\"mb-person\">{Avatar(name, "sm").ToHtml()}<span>{WebUtility.HtmlEncode(name)}</span></span>");

    // The nouns of Resources/Texts/Ui.json ("Ui.Noun.Invoice.One" / ".Many"), found by their plural in any language: "Rechnungen", "invoices" and "Invoices" all name Invoice.
    private static readonly Lazy<Dictionary<string, string>> Nouns = new(() =>
    {
        var nouns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, texts) in Localization.TextCatalog.Entries)
        {
            if (key.StartsWith("Ui.Noun.", StringComparison.Ordinal) && key.EndsWith(".Many", StringComparison.Ordinal))
            {
                var noun = key[..^".Many".Length];
                nouns[noun] = noun;
                foreach (var plural in texts.Values)
                {
                    nouns.TryAdd(plural, noun);
                }
            }
        }

        return nouns;
    });

    /// <summary>
    /// The name of the things in a list for a count, in the language of the interface: "1 invoice", "3 invoices" / "1 Rechnung", "3 Rechnungen".
    /// <paramref name="plural"/> is a plural of Ui.json in any language (or its key without ".Many", "Ui.Noun.Invoice"); a name that is not known is shown as it is.
    /// </summary>
    public static string CountNoun(long count, string plural)
    {
        var word = Nouns.Value.TryGetValue(plural, out var noun) ? Localization.Text.T(noun + (count == 1 ? ".One" : ".Many")) : plural;
        return $"{Services.Fmt.Integer(count)} {word}";
    }

    /// <summary>A phrase with a count: "1 plan active", "3 plans active" (the caller passes both forms, already in the language of the interface).</summary>
    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>The markup of a content as text (a cell that is made of other cells).</summary>
    public static string ToHtml(this IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }
}
