using System.Net;
using System.Text.Json;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MatBill.Tests.Integration;

/// <summary>The pickers of the editor ask the database a few at a time, and a number range can go on at a chosen counter without giving a number twice.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class LookupTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public LookupTests(AppFixture app) => _app = app;

    [DatabaseFact]
    public async Task The_customer_search_finds_by_every_word_and_pages()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            for (var i = 0; i < 25; i++)
            {
                db.Customer.Add(new Customer { Number = $"K-LU-{i:00}", Name = $"Suchkunde {i:00} Lookup", City = i % 2 == 0 ? "Bremen" : "Kiel", Kind = i < 5 ? CustomerKind.Person : CustomerKind.Company });
            }

            await db.SaveChangesAsync();
        });

        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");
        var first = await PageAsync(client, "/api/customers?q=suchkunde%20lookup&take=8");
        Assert.Equal(25, first.Total);
        Assert.Equal(8, first.Names.Count);
        var next = await PageAsync(client, "/api/customers?q=suchkunde%20lookup&take=8&skip=8");
        Assert.Empty(first.Names.Intersect(next.Names));

        Assert.Equal(13, (await PageAsync(client, "/api/customers?q=lookup&city=bremen")).Total);
        Assert.Equal(5, (await PageAsync(client, "/api/customers?q=lookup&kind=person")).Total);
        Assert.Equal("Suchkunde 07 Lookup", (await PageAsync(client, "/api/customers?q=K-LU-07")).Names.Single());

        // the editor no longer carries the customers in the page
        var editor = await AppFixture.TextAsync(client, "/Invoices/Edit");
        Assert.DoesNotContain("Suchkunde 07", editor);
        Assert.Contains("data-lookup-dialog", editor);

        // without signing in there is nothing to find
        using var anonymous = _app.NewClient();
        Assert.NotEqual(HttpStatusCode.OK, (await anonymous.GetAsync("/api/customers?q=lookup")).StatusCode);
    }

    [DatabaseFact]
    public async Task A_range_goes_on_at_a_chosen_counter_but_never_back_over_a_used_number()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var numbers = sp.GetRequiredService<NumberService>();
            const string pattern = "LU-{YYYY}-{NNNN}";
            var day = new DateOnly(2003, 5, 1);
            Assert.Equal(1, await numbers.NextCounterAsync("lookup-test", pattern, day));

            await numbers.SetNextCounterAsync(NumberService.ExpenseRange, pattern, day, 57);
            Assert.Equal("LU-2003-0057", await numbers.NextAsync(NumberService.ExpenseRange, pattern, day));

            // 57 is on a receipt now: back to 50 is refused, forward is fine
            var db = sp.GetRequiredService<MbDbContext>();
            db.Expense.Add(new Expense { Number = "LU-2003-0057", ExpenseDate = day, Supplier = "Laden" });
            await db.SaveChangesAsync();
            Assert.False(await numbers.CanContinueAtAsync(NumberService.ExpenseRange, pattern, day, 50));
            Assert.True(await numbers.CanContinueAtAsync(NumberService.ExpenseRange, pattern, day, 58));
            Assert.True(await numbers.CanContinueAtAsync(NumberService.ExpenseRange, pattern, day, 100));

            // a counter set too high can come back as long as nothing was given out in between
            await numbers.SetNextCounterAsync(NumberService.ExpenseRange, pattern, day, 100);
            Assert.True(await numbers.CanContinueAtAsync(NumberService.ExpenseRange, pattern, day, 58));
        });
    }

    private static async Task<(int Total, List<string> Names)> PageAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("total").GetInt32(), [.. json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()!)]);
    }
}
