using System.Globalization;
using System.Security.Claims;
using MatBill.Services;
using Microsoft.AspNetCore.Localization;

namespace MatBill.Localization;

/// <summary>
/// The languages of the interface. The language comes from the signed-in person (their account), else from the choice on the sign-in page (a cookie),
/// else from the browser (Accept-Language), else English. Only the UI culture changes: the request culture stays invariant, so numbers and dates
/// in forms and query strings are always read the same way.
/// </summary>
public static class Languages
{
    public const string Default = "en";

    public const string CookieName = ".MatBill.Language";

    /// <summary>Code, short label, name in its own language.</summary>
    public static readonly IReadOnlyList<(string Code, string Label, string Name)> Supported = [("en", "EN", "English"), ("de", "DE", "Deutsch")];

    public static IReadOnlyList<string> Codes { get; } = [.. Supported.Select(s => s.Code)];

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    // Dates as 18/10/2026, amounts as 1,234.50 €: the British way, as close as English gets to the European habits of an invoice.
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    public static bool IsSupported(string? code) => code is not null && Codes.Contains(code, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? code)
    {
        var two = code is { Length: >= 2 } ? code[..2].ToLowerInvariant() : "";
        return IsSupported(two) ? two : Default;
    }

    /// <summary>The language of the current request ("en" or "de").</summary>
    public static string Current => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "iv" ? Default : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public static bool IsGerman => Current == "de";

    /// <summary>The culture that writes dates and numbers of the interface in the current language.</summary>
    public static CultureInfo FormatCulture => IsGerman ? German : English;

    public static CultureInfo UiCulture(string code) => CultureInfo.GetCultureInfo(Normalize(code));

    /// <summary>The choice of somebody who is not signed in (the sign-in page), kept for a year.</summary>
    public static void SetCookie(HttpResponse response, string code, bool secure)
        => response.Cookies.Append(CookieName, Normalize(code), new CookieOptions
        {
            Expires = DateTimeOffset.UtcNow.AddYears(1),
            IsEssential = true,
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

    /// <summary>Runs <paramref name="action"/> in another language (a notification is written in the language of each recipient).</summary>
    public static T In<T>(string code, Func<T> action)
    {
        var before = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = UiCulture(code);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }
}

/// <summary>Account first, then the cookie of the sign-in page; Accept-Language and the default follow in the pipeline.</summary>
public sealed class AccountLanguageProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var code = httpContext.User.FindFirstValue(AppClaims.Language);
        if (!Languages.IsSupported(code))
        {
            code = httpContext.Request.Cookies[Languages.CookieName];
        }

        return Task.FromResult(Languages.IsSupported(code)
            ? new ProviderCultureResult(CultureInfo.InvariantCulture.Name, Languages.Normalize(code))
            : null);
    }
}
