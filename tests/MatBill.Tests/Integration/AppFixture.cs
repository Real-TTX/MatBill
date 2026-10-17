using System.Text.RegularExpressions;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace MatBill.Tests.Integration;

/// <summary>
/// A running application against a real PostgreSQL: its own database (made and dropped here) and its own data folder. The tests need a server and
/// skip themselves without one: <c>MATBILL_TEST_DB</c> is a connection string such as <c>Host=localhost;Port=5432;Username=postgres;Password=...</c>.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Variable = "MATBILL_TEST_DB";

    public static bool Available => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable));

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "mb-it-" + Guid.NewGuid().ToString("N")[..8]);

    public string DatabaseName { get; } = "mb_it_" + Guid.NewGuid().ToString("N")[..10];

    // The application reads where it lives from the environment while it starts. The test classes share one collection, so they start one after the other.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var b = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable));
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("MATBILL_DATA", DataDir);
        Environment.SetEnvironmentVariable("MATBILL__Database__Host", b.Host);
        Environment.SetEnvironmentVariable("MATBILL__Database__Port", b.Port.ToString());
        Environment.SetEnvironmentVariable("MATBILL__Database__Username", b.Username);
        Environment.SetEnvironmentVariable("MATBILL__Database__Password", b.Password);
        Environment.SetEnvironmentVariable("MATBILL__Database__Database", DatabaseName);
        return base.CreateHost(builder);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        if (!Available)
        {
            return;
        }

        try
        {
            var b = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable)) { Database = "postgres", Pooling = false };
            await using var connection = new NpgsqlConnection(b.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(DataDir, recursive: true);
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException)
        {
            // a temporary database that stays behind is not worth failing a test run
        }
    }

    // ------------------------------------------------------------------------------------------------ what the tests start from

    /// <summary>Runs something with the services of a request scope (a context, the services of the application).</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> action) => WithScopeAsync<int>(async sp =>
    {
        await action(sp);
        return 0;
    });

    public Task<User> CreateUserAsync(string email, string password, UserRole role)
        => WithScopeAsync(sp => sp.GetRequiredService<UserService>().CreateAsync(email, "Test " + role, role, password));

    /// <summary>Until there is an administrator the only page is the setup; most tests start with one.</summary>
    public async Task EnsureAdminAsync()
    {
        if (!await WithScopeAsync(sp => sp.GetRequiredService<MbDbContext>().User.AnyAsync(u => u.Role == UserRole.Admin)))
        {
            await CreateUserAsync("admin@example.test", "Admin-Passwort-2026", UserRole.Admin);
        }
    }

    /// <summary>A customer with an issued invoice: the invoice has its number and its PDF (the file is in the store and in the table of the files).</summary>
    public Task<(Customer Customer, Invoice Invoice, StoredFile File)> CreateInvoiceWithPdfAsync(string name, decimal price = 100m)
        => WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MbDbContext>();
            var numbers = sp.GetRequiredService<NumberService>();
            var invoices = sp.GetRequiredService<InvoiceService>();
            var customer = new Customer { Number = await numbers.NextCustomerNumberAsync(invoices.Today), Name = "Kunde " + name, Street = "Weg 1", PostalCode = "12345", City = "Stadt", Email = "kunde@example.test" };
            db.Customer.Add(customer);
            await db.SaveChangesAsync();
            var invoice = invoices.NewDraft(customer);
            invoice.Lines.Add(new InvoiceLine { Position = 1, Name = "Leistung " + name, Quantity = 1, UnitPrice = price, TaxRate = 19 });
            InvoiceService.Recalculate(invoice);
            db.Invoice.Add(invoice);
            await db.SaveChangesAsync();
            await invoices.IssueAsync(invoice);
            return (customer, invoice, invoice.PdfFile!);
        });

    // ------------------------------------------------------------------------------------------------ talking to the pages

    public HttpClient NewClient(bool followRedirects = false)
        => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true });

    private static readonly Regex Token = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>The anti-forgery token of the form on a page (the cookie that belongs to it is kept by the client).</summary>
    public static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Token.Match(html) is { Success: true } m ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : throw new InvalidOperationException("No form on " + url);
    }

    /// <summary>Posts a form the way a browser does: loads the page (for the token), then posts the fields.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, IDictionary<string, string> fields, string? postUrl = null)
    {
        var token = await TokenAsync(client, pageUrl);
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(body));
    }

    /// <summary>The path a redirect points to (the address may be relative).</summary>
    public static string LocationPath(HttpResponseMessage response) => new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;

    /// <summary>The text of a page as a person reads it: Razor writes "ü" as an HTML entity.</summary>
    public static async Task<string> TextAsync(HttpResponseMessage response) => System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    public static async Task<string> TextAsync(HttpClient client, string url) => System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(url));

    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var response = await PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection
{
    public const string Name = "integration";
}

/// <summary>A test that needs the PostgreSQL of <see cref="AppFixture.Variable"/>; without it the test is skipped (and says so).</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute()
    {
        if (!AppFixture.Available)
        {
            Skip = $"{AppFixture.Variable} is not set: no PostgreSQL to run the application against.";
        }
    }
}
