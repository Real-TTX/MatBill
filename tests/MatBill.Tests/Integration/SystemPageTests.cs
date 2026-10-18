using System.Net;

namespace MatBill.Tests.Integration;

/// <summary>The system page asks <c>SystemService</c> about the database instead of the context: it still shows the server, the size and the row counts, and can clean up.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class SystemPageTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public SystemPageTests(AppFixture app) => _app = app;

    [DatabaseFact]
    public async Task The_system_page_shows_the_database_and_what_is_in_it_and_cleans_up_on_request()
    {
        await _app.EnsureAdminAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.com", "Start12345");

        var page = await AppFixture.TextAsync(client, "/Admin/System");
        Assert.Contains("PostgreSQL", page);
        Assert.Contains("Benutzer", page);
        Assert.Contains("Freigabe-Links", page);

        var maintain = await AppFixture.PostFormAsync(client, "/Admin/System", new Dictionary<string, string>(), "/Admin/System?handler=Maintain");
        Assert.Equal(HttpStatusCode.Redirect, maintain.StatusCode);
        Assert.Equal("/Admin/System", AppFixture.LocationPath(maintain));
    }
}
