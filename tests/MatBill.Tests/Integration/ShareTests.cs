using System.Net;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>The role "Anonym": a link shows one invoice read only, with its PDF, for a limited time, behind a password when it has one.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class ShareTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public ShareTests(AppFixture app) => _app = app;

    private async Task<(Invoice Invoice, ShareLink Link)> InvoiceWithLinkAsync(string name, string? password = null)
    {
        await _app.EnsureAdminAsync();
        var (_, invoice, _) = await _app.CreateInvoiceWithPdfAsync(name);
        var link = await _app.WithScopeAsync(async sp =>
        {
            var tracked = await sp.GetRequiredService<InvoiceService>().FindAsync(invoice.Id);
            return await sp.GetRequiredService<ShareService>().CreateAsync(tracked!, "Rechnung " + invoice.Number, "Hallo " + name, 7, password, null);
        });
        return (invoice, link);
    }

    [DatabaseFact]
    public async Task Anybody_with_the_link_sees_the_invoice_and_gets_its_pdf()
    {
        var (invoice, link) = await InvoiceWithLinkAsync("Offen");
        using var visitor = _app.NewClient();

        var response = await visitor.GetAsync($"/s/{link.Token}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await AppFixture.TextAsync(response);
        Assert.Contains(invoice.Number!, text);
        Assert.Contains("Hallo Offen", text);
        Assert.Contains("Leistung Offen", text);
        Assert.DoesNotContain("/Invoices/Edit", text);

        var pdf = await visitor.GetAsync($"/s/{link.Token}/pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        var counted = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().ShareLink.AsNoTracking().FirstAsync(l => l.Id == link.Id));
        Assert.Equal(1, counted.ViewCount);
    }

    [DatabaseFact]
    public async Task A_draft_cannot_be_shared()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var draft = invoices.NewDraft();
            draft.RecipientName = "Entwurf";
            await Assert.ThrowsAsync<InvoiceException>(() => sp.GetRequiredService<ShareService>().CreateAsync(draft, "x", null, 7, null, null));
        });
    }

    [DatabaseFact]
    public async Task A_password_is_asked_for_checked_and_remembered_and_guards_the_pdf()
    {
        var (invoice, link) = await InvoiceWithLinkAsync("Geschützt", "Passwort-123");
        using var visitor = _app.NewClient();

        var gate = await AppFixture.TextAsync(visitor, $"/s/{link.Token}");
        Assert.Contains("Für diese Rechnung brauchst du ein Passwort.", gate);
        Assert.DoesNotContain("Leistung Geschützt", gate);
        Assert.Equal(HttpStatusCode.Redirect, (await visitor.GetAsync($"/s/{link.Token}/pdf")).StatusCode);

        var wrong = await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "falsch" }, $"/s/{link.Token}?handler=Password");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Das Passwort stimmt nicht.", await AppFixture.TextAsync(wrong));

        var right = await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "Passwort-123" }, $"/s/{link.Token}?handler=Password");
        Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
        Assert.Contains("Leistung Geschützt", await AppFixture.TextAsync(visitor, $"/s/{link.Token}"));
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/s/{link.Token}/pdf")).StatusCode);

        // somebody else still sees the gate
        using var other = _app.NewClient();
        Assert.Contains("Passwort", await AppFixture.TextAsync(other, $"/s/{link.Token}"));
        _ = invoice;
    }

    [DatabaseFact]
    public async Task Too_many_wrong_passwords_shut_the_link_for_a_while()
    {
        var (_, link) = await InvoiceWithLinkAsync("Gesperrt", "Passwort-123");
        using var visitor = _app.NewClient();
        for (var i = 0; i < ShareService.MaxWrongPasswords; i++)
        {
            await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "falsch" + i }, $"/s/{link.Token}?handler=Password");
        }

        var right = await AppFixture.PostFormAsync(visitor, $"/s/{link.Token}", new Dictionary<string, string> { ["Password"] = "Passwort-123" }, $"/s/{link.Token}?handler=Password");
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Contains("Zu viele falsche Versuche", await AppFixture.TextAsync(right));
    }

    [DatabaseFact]
    public async Task A_revoked_or_expired_link_says_so_and_an_unknown_one_is_not_found()
    {
        var (_, revoked) = await InvoiceWithLinkAsync("Widerrufen");
        var (_, expired) = await InvoiceWithLinkAsync("Abgelaufen");
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            (await db.ShareLink.FirstAsync(l => l.Id == revoked.Id)).RevokedDate = DateTime.UtcNow;
            (await db.ShareLink.FirstAsync(l => l.Id == expired.Id)).ExpiresDate = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });

        using var visitor = _app.NewClient();
        var r = await visitor.GetAsync($"/s/{revoked.Token}");
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode);
        Assert.Contains("widerrufen", await AppFixture.TextAsync(r));
        Assert.Equal(HttpStatusCode.Gone, (await visitor.GetAsync($"/s/{revoked.Token}/pdf")).StatusCode);

        var e = await visitor.GetAsync($"/s/{expired.Token}");
        Assert.Equal(HttpStatusCode.Gone, e.StatusCode);
        Assert.Contains("abgelaufen", await AppFixture.TextAsync(e));

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/s/{Guid.NewGuid()}")).StatusCode);
    }
}
