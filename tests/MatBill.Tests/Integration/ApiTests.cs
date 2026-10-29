using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>
/// The API for other programs: a client with a key asks for invoices. Keys (none, wrong, revoked), rights (create, read, issue directly), the idempotent
/// reference, the customer found by the client's key of it, the client's own number range, and the documents of an issued invoice.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ApiTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public ApiTests(AppFixture app) => _app = app;

    private async Task<(ApiClient Client, string Key)> ClientAsync(bool canIssue = false, bool canRead = true, bool canCreate = true, string? pattern = null)
    {
        await _app.EnsureAdminAsync();
        return await _app.WithScopeAsync(sp => sp.GetRequiredService<ApiClientService>()
            .CreateAsync("Test " + Guid.NewGuid().ToString("N")[..6], canCreate, canRead, canIssue, pattern));
    }

    private HttpClient Api(string? key)
    {
        var http = _app.NewClient();
        if (key is not null)
        {
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        return http;
    }

    private static object Request(string reference, string externalRef, bool issue = false, string street = "Hauptstr. 1", decimal price = 29m) => new
    {
        reference,
        issue,
        note = "Partner-Abrechnung Oktober",
        customer = new { externalRef, name = "Partner " + externalRef, addressLines = new[] { street }, postcode = "12345", city = "Berlin", country = "DE", email = "billing@example.test", vatId = "DE123456789" },
        lines = new object[]
        {
            new { text = "Matmon Business", quantity = 2, unit = "Monat", unitPrice = price, taxRate = 19, servicePeriodFrom = "2026-10-01", servicePeriodTo = "2026-10-31" },
        },
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string NewRef() => "test:" + Guid.NewGuid().ToString("N");

    [DatabaseFact]
    public async Task Without_a_valid_key_nothing_is_answered()
    {
        var (client, key) = await ClientAsync();
        foreach (var presented in new string?[] { null, "mbk_wrongwrongwrongwrongwrongwrongwrongwrongw", key[..^1] + (key[^1] == 'A' ? 'B' : 'A') })
        {
            using var http = Api(presented);
            var response = await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "x"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("invalid_key", (await JsonAsync(response)).GetProperty("error").GetString());
        }

        // the key also works in X-Api-Key, and stops working the moment the client is revoked
        using (var other = _app.NewClient())
        {
            other.DefaultRequestHeaders.Add("X-Api-Key", key);
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/v1/invoice-requests?reference=nothing")).StatusCode);
        }

        await _app.WithScopeAsync(async sp =>
        {
            var tracked = await sp.GetRequiredService<MbDbContext>().ApiClient.FirstAsync(c => c.Id == client.Id);
            await sp.GetRequiredService<ApiClientService>().RevokeAsync(tracked);
        });
        using var revoked = Api(key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await revoked.GetAsync("/api/v1/invoice-requests?reference=nothing")).StatusCode);

        // only the hash is stored
        var stored = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().ApiClient.AsNoTracking().FirstAsync(c => c.Id == client.Id));
        Assert.Equal(ApiClientService.Hash(key), stored.KeyHash);
        Assert.DoesNotContain(key, JsonSerializer.Serialize(stored));
    }

    [DatabaseFact]
    public async Task A_request_becomes_a_draft_and_the_same_reference_twice_is_the_same_invoice()
    {
        var (client, key) = await ClientAsync();
        using var http = Api(key);
        var reference = NewRef();
        var externalRef = "matmon:org:" + Guid.NewGuid();

        var first = await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(reference, externalRef));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        var body = await JsonAsync(first);
        Assert.Equal("draft", body.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("number").ValueKind);
        Assert.Equal(69.02m, body.GetProperty("grossTotal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("links").GetProperty("pdf").ValueKind);
        var id = body.GetProperty("id").GetInt64();

        var again = await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(reference, externalRef, price: 1000m));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(id, (await JsonAsync(again)).GetProperty("id").GetInt64());

        var byReference = await JsonAsync(await http.GetAsync("/api/v1/invoice-requests?reference=" + Uri.EscapeDataString(reference)));
        Assert.Equal(id, byReference.GetProperty("id").GetInt64());
        Assert.Equal(id, (await JsonAsync(await http.GetAsync($"/api/v1/invoice-requests/{id}"))).GetProperty("id").GetInt64());

        // a draft has no document yet
        Assert.Equal(HttpStatusCode.Conflict, (await http.GetAsync($"/api/v1/invoices/{id}/pdf")).StatusCode);

        var invoice = await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.AsNoTracking().Include(i => i.Lines).Include(i => i.Customer).FirstAsync(i => i.Id == id));
        Assert.Equal(1, await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.CountAsync(i => i.ApiClientId == client.Id && i.ApiReference == reference)));
        Assert.Equal(client.Id, invoice.ApiClientId);
        Assert.Equal("Partner-Abrechnung Oktober", invoice.ApiNote);
        Assert.Equal(new DateOnly(2026, 10, 1), invoice.ServiceFrom);
        Assert.Equal(new DateOnly(2026, 10, 31), invoice.ServiceTo);
        Assert.Contains("Leistungszeitraum: 01.10.2026 – 31.10.2026", invoice.Lines[0].Description);
        Assert.Equal("Deutschland", invoice.RecipientCountry);
        Assert.Equal(externalRef, invoice.Customer!.ExternalRef);
        Assert.Equal("DE123456789", invoice.Customer.VatId);

        // the owner sees where it came from
        using var owner = _app.NewClient();
        await AppFixture.LoginAsync(owner, "admin@example.com", "Start12345");
        var page = await AppFixture.TextAsync(owner, $"/Invoices/Edit?id={id}");
        Assert.Contains("von " + client.Name, page);
        Assert.Contains(reference, page);
        Assert.Contains("von " + client.Name, await AppFixture.TextAsync(owner, "/Invoices?origin=api"));
    }

    [DatabaseFact]
    public async Task The_customer_is_found_by_the_key_of_the_client_and_takes_the_new_address()
    {
        var (client, key) = await ClientAsync();
        var (other, otherKey) = await ClientAsync();
        var externalRef = "matmon:org:" + Guid.NewGuid();
        using var http = Api(key);
        using var otherHttp = Api(otherKey);

        var a = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), externalRef)));
        var b = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), externalRef, street: "Neue Str. 9")));
        var c = await JsonAsync(await otherHttp.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), externalRef)));

        var customerA = a.GetProperty("customer").GetProperty("id").GetInt64();
        Assert.Equal(customerA, b.GetProperty("customer").GetProperty("id").GetInt64());
        // the same key of another client is another customer
        Assert.NotEqual(customerA, c.GetProperty("customer").GetProperty("id").GetInt64());

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            Assert.Equal("Neue Str. 9", (await db.Customer.FirstAsync(x => x.Id == customerA)).Street);
            // the first draft keeps the address it was written with
            var firstDraft = await db.Invoice.FirstAsync(i => i.Id == a.GetProperty("id").GetInt64());
            Assert.Equal("Hauptstr. 1", firstDraft.RecipientStreet);
            Assert.Equal(1, await db.Customer.CountAsync(x => x.ApiClientId == client.Id && x.ExternalRef == externalRef));
            Assert.Equal(1, await db.Customer.CountAsync(x => x.ApiClientId == other.Id && x.ExternalRef == externalRef));
        });
    }

    [DatabaseFact]
    public async Task Rights_are_checked_issuing_needs_its_own()
    {
        var (_, key) = await ClientAsync(canIssue: false);
        using var http = Api(key);
        var reference = NewRef();
        var refused = await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(reference, "x:" + reference, issue: true));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("issue_not_allowed", (await JsonAsync(refused)).GetProperty("error").GetString());
        Assert.False(await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.AnyAsync(i => i.ApiReference == reference)));

        var (_, readOnly) = await ClientAsync(canCreate: false);
        using var reader = Api(readOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "y"))).StatusCode);

        var (_, writeOnly) = await ClientAsync(canRead: false);
        using var writer = Api(writeOnly);
        var created = await JsonAsync(await writer.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "z")));
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync($"/api/v1/invoice-requests/{created.GetProperty("id").GetInt64()}")).StatusCode);

        var invalid = await http.PostAsJsonAsync("/api/v1/invoice-requests", new { customer = new { name = "" }, lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problems = (await JsonAsync(invalid)).GetProperty("problems").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains(problems, p => p!.StartsWith("reference", StringComparison.Ordinal));
        Assert.Contains(problems, p => p!.StartsWith("customer.externalRef", StringComparison.Ordinal));
        Assert.Contains(problems, p => p!.StartsWith("lines", StringComparison.Ordinal));

        var broken = await http.PostAsync("/api/v1/invoice-requests", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Equal("invalid_json", (await JsonAsync(broken)).GetProperty("error").GetString());
    }

    [DatabaseFact]
    public async Task A_client_with_its_own_range_issues_directly_and_counts_on_its_own()
    {
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var pattern = $"RE-{tag}-{{YYYY}}-{{NNNN}}";
        var (client, key) = await ClientAsync(canIssue: true, pattern: pattern);
        var (_, strangerKey) = await ClientAsync();
        var year = DateTime.Now.Year;
        var normalBefore = await _app.WithScopeAsync(sp => sp.GetRequiredService<NumberService>().PeekInvoiceNumberAsync(sp.GetRequiredService<InvoiceService>().Today));
        using var http = Api(key);

        var first = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "c:" + tag, issue: true)));
        var second = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "c:" + tag, issue: true)));
        Assert.Equal("open", first.GetProperty("status").GetString());
        Assert.Equal($"RE-{tag}-{year}-0001", first.GetProperty("number").GetString());
        Assert.Equal($"RE-{tag}-{year}-0002", second.GetProperty("number").GetString());
        // the normal range did not move
        Assert.Equal(normalBefore, await _app.WithScopeAsync(sp => sp.GetRequiredService<NumberService>().PeekInvoiceNumberAsync(sp.GetRequiredService<InvoiceService>().Today)));

        var id = first.GetProperty("id").GetInt64();
        var pdf = await http.GetAsync(first.GetProperty("links").GetProperty("pdf").GetString());
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
        var xml = await http.GetAsync($"/api/v1/invoices/{id}/xml");
        Assert.Equal(HttpStatusCode.OK, xml.StatusCode);
        Assert.Contains($"RE-{tag}-{year}-0001", await xml.Content.ReadAsStringAsync());

        // another client sees nothing of it
        using var stranger = Api(strangerKey);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/invoice-requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/v1/invoices/{id}/pdf")).StatusCode);

        // a draft of the client issued in MatBill and a cancellation take the client's range too
        var draft = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "c:" + tag)));
        var (issued, storno) = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var tracked = await invoices.FindAsync(draft.GetProperty("id").GetInt64());
            await invoices.IssueAsync(tracked!);
            var cancellation = await invoices.CancelAsync((await invoices.FindAsync(id))!, "Test");
            return (tracked!.Number, cancellation.Number);
        });
        Assert.Equal($"RE-{tag}-{year}-0003", issued);
        Assert.Equal($"RE-{tag}-{year}-0004", storno);
        var cancelled = await JsonAsync(await http.GetAsync($"/api/v1/invoice-requests/{id}"));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(storno, cancelled.GetProperty("cancellation").GetProperty("number").GetString());
        Assert.Equal(client.Id, await _app.WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().Invoice.Where(i => i.Number == storno).Select(i => i.ApiClientId).FirstAsync()));
    }

    [DatabaseFact]
    public async Task The_settings_make_a_client_show_its_key_once_and_set_its_range()
    {
        await _app.EnsureAdminAsync();
        using var admin = _app.NewClient();
        await AppFixture.LoginAsync(admin, "admin@example.com", "Start12345");
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var name = "Matmon " + tag;

        var refused = await AppFixture.PostFormAsync(admin, "/Admin/Api/Edit", new Dictionary<string, string>
        {
            ["Input.Name"] = name, ["Input.CanCreate"] = "true", ["Input.CanRead"] = "true", ["Input.CanIssue"] = "false", ["Input.NumberPattern"] = "RE-{JJJJ}", ["Input.Next"] = "1",
        });
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("genau einen Zähler", await AppFixture.TextAsync(refused));

        var created = await AppFixture.PostFormAsync(admin, "/Admin/Api/Edit", new Dictionary<string, string>
        {
            ["Input.Name"] = name, ["Input.CanCreate"] = "true", ["Input.CanRead"] = "true", ["Input.CanIssue"] = "false",
            ["Input.NumberPattern"] = $"RE-{tag}-{{JJJJ}}-{{NNNN}}", ["Input.Next"] = "3",
        });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var page = await AppFixture.TextAsync(admin, created.Headers.Location!.ToString());
        var key = System.Text.RegularExpressions.Regex.Match(page, "value=\"(mbk_[A-Za-z0-9_-]+)\"").Groups[1].Value;
        Assert.StartsWith("mbk_", key);
        Assert.Contains($"RE-{tag}-{DateTime.Now.Year}-0003", page);
        // shown once: the next visit has the prefix only
        Assert.DoesNotContain(key, await AppFixture.TextAsync(admin, created.Headers.Location!.ToString()));

        // the key works, a request becomes a draft, issuing it in MatBill gives the third number of the range
        using var http = Api(key);
        var draft = await JsonAsync(await http.PostAsJsonAsync("/api/v1/invoice-requests", Request(NewRef(), "ui:" + tag)));
        var number = await _app.WithScopeAsync(async sp =>
        {
            var invoices = sp.GetRequiredService<InvoiceService>();
            var tracked = await invoices.FindAsync(draft.GetProperty("id").GetInt64());
            await invoices.IssueAsync(tracked!);
            return tracked!.Number;
        });
        Assert.Equal($"RE-{tag}-{DateTime.Now.Year}-0003", number);
    }

    [DatabaseFact]
    public async Task A_pattern_of_its_own_must_differ_from_the_others()
    {
        var tag = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (client, _) = await ClientAsync(pattern: $"X-{tag}-{{NNNN}}");
        await _app.WithScopeAsync(async sp =>
        {
            var clients = sp.GetRequiredService<ApiClientService>();
            var normal = sp.GetRequiredService<Configuration.ConfigStore>().Current.Invoicing.InvoiceNumberPattern;
            Assert.Equal("Api.Pattern.SameAsInvoices", await clients.PatternProblemAsync(normal, null));
            Assert.Equal("Api.Pattern.SameAsOther", await clients.PatternProblemAsync($"X-{tag}-{{NNNN}}", null));
            Assert.Null(await clients.PatternProblemAsync($"X-{tag}-{{NNNN}}", client.Id));
            Assert.Null(await clients.PatternProblemAsync("", null));
            Assert.Equal("NumberPattern.Counter", await clients.PatternProblemAsync("X-{YYYY}", null));
        });
    }
}
