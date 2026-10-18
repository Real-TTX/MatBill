using System.Net;
using MatBill.Data;
using MatBill.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>The interface speaks the language of the browser until somebody signs in, then the language of their account; the choice is kept.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class LanguageTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public LanguageTests(AppFixture app) => _app = app;

    private static string En(string key) => TextCatalog.Find(key, "en")!;

    private static string De(string key) => TextCatalog.Find(key, "de")!;

    [DatabaseFact]
    public async Task The_sign_in_page_follows_the_browser_and_the_switch()
    {
        await _app.EnsureAdminAsync();
        using var english = _app.NewClient(language: "en-US");
        var page = await AppFixture.TextAsync(english, "/Account/Login");
        Assert.Contains("lang=\"en\"", page);

        using var german = _app.NewClient(language: "de-DE");
        Assert.Contains("lang=\"de\"", await AppFixture.TextAsync(german, "/Account/Login"));

        // the switch below the card: the English browser chooses German, and German it stays
        var switched = await AppFixture.PostFormAsync(english, "/Account/Login", new Dictionary<string, string> { ["language"] = "de", ["returnUrl"] = "/Account/Login" }, "/language");
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Contains("lang=\"de\"", await AppFixture.TextAsync(english, "/Account/Login"));
    }

    [DatabaseFact]
    public async Task A_signed_in_person_reads_the_language_of_their_account_and_changes_it()
    {
        await _app.EnsureAdminAsync();
        var user = await _app.CreateUserAsync("sprache@example.test", "Sprache-Passwort-2026", Data.Entities.UserRole.User);
        await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "en")));

        // a German browser, an English account: the account wins
        using var client = _app.NewClient(language: "de-DE");
        await AppFixture.LoginAsync(client, "sprache@example.test", "Sprache-Passwort-2026");
        var english = await AppFixture.TextAsync(client, "/");
        Assert.Contains("lang=\"en\"", english);
        Assert.Contains(En("Shell.Nav.NewInvoice"), english);

        await AppFixture.PostFormAsync(client, "/Account/Profile", new Dictionary<string, string> { ["language"] = "de", ["returnUrl"] = "/" }, "/language");
        var german = await AppFixture.TextAsync(client, "/");
        Assert.Contains("lang=\"de\"", german);
        Assert.Contains(De("Shell.Nav.NewInvoice"), german);
        Assert.Equal("de", await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.Where(u => u.Id == user.Id).Select(u => u.Language).FirstAsync()));
    }

    [DatabaseFact]
    public async Task An_issued_invoice_stays_german_in_the_english_interface()
    {
        await _app.EnsureAdminAsync();
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync("Sprache", 50m);
        await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.Where(u => u.Email == "admin@example.com").ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "en")));
        try
        {
            using var client = _app.NewClient(language: "en-GB");
            await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
            var page = await AppFixture.TextAsync(client, $"/Invoices/Edit?id={invoice.Id}");
            Assert.Contains("lang=\"en\"", page);
            Assert.Contains("Rechnungsnummer", page);
            Assert.Contains("Gesamtbetrag", page);
        }
        finally
        {
            await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.Where(u => u.Email == "admin@example.com").ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "de")));
        }
    }
}
