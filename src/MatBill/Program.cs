using System.Globalization;
using MatBill.Configuration;
using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.Services.Auth;
using MatBill.Services.Backup;
using MatBill.Storage;
using MatBill.UiKit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

// The image checks itself (HEALTHCHECK in the Dockerfile): it needs no curl.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

// QuestPDF draws the invoices; its community licence covers companies below 1 million USD of annual revenue.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var dataDir = Environment.GetEnvironmentVariable("MATBILL_DATA") is { Length: > 0 } fromEnv
    ? fromEnv
    : Path.Combine(Directory.GetCurrentDirectory(), "data");
Directory.CreateDirectory(dataDir);
var config = new ConfigStore(dataDir);
Fmt.Configure(config.Current.Server.TimeZone);

var builder = WebApplication.CreateBuilder(args);

// Port 2505; ASPNETCORE_URLS still wins (tests, a reverse proxy on the same machine).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("MATBILL_PORT") ?? "2505"}");
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100L * 1024 * 1024);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Sessions live in the database; the keys that protect the cookie (and secrets) live in the data volume, so a restart keeps everybody signed in.
builder.Services.AddDataProtection()
    .SetApplicationName("MatBill")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddDbContextFactory<MbDbContext>(options => options
    // No automatic retries: a save that fails is shown to the person, not repeated behind their back.
    .UseNpgsql(config.ConnectionString())
    // Soft-deleted customers are refused by the pages before anything refers to them, so the warning about filtered required ends is noise.
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MbDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IconSprite>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddSingleton<TwoFactorTicket>();
builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddScoped<SessionCookieEvents>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PublicUrl>();
builder.Services.AddScoped<FileStoreService>();
builder.Services.AddScoped<ShareService>();
builder.Services.AddScoped<NumberService>();
builder.Services.AddScoped<BrandingService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<RecurringService>();
builder.Services.AddScoped<MailService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<BackupStoreFactory>();
builder.Services.AddScoped<DatabaseArchive>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddHostedService<BillingScheduler>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddScoped<SystemService>();
builder.Services.AddScoped<DemoData>();
builder.Services.AddScoped<NavCounts>();
builder.Services.AddScoped<ShellFactory>();

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

builder.Services.AddAuthentication(SignInService.CookieScheme)
    .AddCookie(SignInService.CookieScheme, options =>
    {
        options.Cookie.Name = ".MatBill";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = config.Current.Server.ForceSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = false;
        options.EventsType = typeof(SessionCookieEvents);
    });

builder.Services.AddAuthorizationBuilder()
    // Everything needs a signed-in user (administrator or user) unless a page says otherwise: a new page is closed by default.
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SignInService.CookieScheme)
        .RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.User)).Build())
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireRole(nameof(UserRole.Admin)))
    .AddPolicy("SignedIn", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireAuthenticatedUser());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AuthorizeFolder("/Branding", "Admin");
    options.Conventions.AuthorizeFolder("/Account", "SignedIn");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/TwoFactor");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Offline");
    options.Conventions.AllowAnonymousToFolder("/Share");
})
// A non-nullable string is not "required" by itself: the pages say what is required (the star, the message, the check).
.AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Numbers in forms and in the query string are always written the invariant way ("12.5"); the pages show them German with Fmt.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture);
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [CultureInfo.InvariantCulture];
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

await DatabaseInitializer.InitializeAsync(app.Services, config, logger);

app.UseForwardedHeaders();
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// A page that is not there (or not for you) is an empty 404/403 for the pipeline; the Error page says it in words.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; " +
        "frame-src 'self' blob:; object-src 'self' blob:; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";
    await next();
});

// The web app manifest has its own type; the service worker must never be cached by the browser, or an update would wait for days.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name is "sw.js" or "manifest.webmanifest")
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
app.UseAuthentication();

// While there is no administrator the only page is the one that makes the first one.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/Account/Setup") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/css")
        && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/ui") && !path.StartsWithSegments("/img") && !path.StartsWithSegments("/Offline")
        && !path.Value!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !path.Value.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase)
        && path.Value != "/sw.js"
        && !await context.RequestServices.GetRequiredService<SetupState>().HasAdminAsync(context.RequestServices))
    {
        context.Response.Redirect("/Account/Setup");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapGet("/health", async (MbDbContext db) =>
{
    await db.Database.CanConnectAsync();
    return Results.Text("ok");
}).AllowAnonymous();

app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, MbDbContext db, SignInService signIn, ICurrentUser user) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var theme = http.Request.Form["theme"].ToString();
    if (theme is not ("system" or "light" or "dark") || user.UserId is not { } id)
    {
        return Results.BadRequest();
    }

    var entity = await db.User.Include(u => u.Totp).FirstAsync(u => u.Id == id);
    entity.Theme = theme;
    await db.SaveChangesAsync();
    await signIn.RefreshAsync(http, entity);
    return Results.NoContent();
}).RequireAuthorization("SignedIn");

// The switch of the invoice editor between the document and the form: remembered per person.
app.MapPost("/api/editor-view", async (HttpContext http, IAntiforgery antiforgery, MbDbContext db, ICurrentUser user) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var view = http.Request.Form["view"].ToString();
    if (view is not ("document" or "form") || user.UserId is not { } id)
    {
        return Results.BadRequest();
    }

    await db.User.Where(u => u.Id == id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EditorView, view));
    return Results.NoContent();
});

// The recipient picker of the editor creates a customer from a name (FORM-16): the answer is the new option.
app.MapPost("/api/customers", async (HttpContext http, IAntiforgery antiforgery, MbDbContext db, NumberService numbers, InvoiceService invoices) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var name = http.Request.Form["name"].ToString().Trim();
    if (name.Length is 0 or > 200)
    {
        return Results.BadRequest(new { error = "Bitte gib einen Namen ein." });
    }

    var customer = new Customer { Name = name, Number = await numbers.NextCustomerNumberAsync(invoices.Today) };
    db.Customer.Add(customer);
    await db.SaveChangesAsync();
    return Results.Ok(new { value = customer.Id.ToString(CultureInfo.InvariantCulture), name = customer.Name, description = customer.Number, customer = CustomerJson.Of(customer) });
});

app.MapGet("/api/customers/{id:long}", async (long id, MbDbContext db) =>
    await db.Customer.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id) is { } c ? Results.Ok(CustomerJson.Of(c)) : Results.NotFound());

app.MapGet("/api/notifications/count", async (ICurrentUser user, NotificationService notifications)
    => Results.Ok(new { unread = user.UserId is { } id ? await notifications.UnreadCountAsync(id) : 0 }));

// The PDF of an invoice: the stored one of an issued invoice, a fresh one of a draft. ?download=1 saves it, otherwise the browser shows it.
app.MapGet("/invoices/{id:long}/pdf", async (long id, HttpContext http, InvoiceService invoices) =>
{
    var invoice = await invoices.FindAsync(id, http.RequestAborted);
    if (invoice is null)
    {
        return Results.NotFound();
    }

    var bytes = await invoices.PdfAsync(invoice, http.RequestAborted);
    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(bytes, "application/pdf", http.Request.Query.ContainsKey("download") ? InvoiceService.PdfName(invoice) : null);
});

// The PDF of the template of a series, as its next invoice would look.
app.MapGet("/recurring/{id:long}/preview", async (long id, HttpContext http, RecurringService recurring, InvoiceService invoices, ConfigStore cfg) =>
{
    var series = await recurring.LoadAsync(id, http.RequestAborted);
    if (series is null)
    {
        return Results.NotFound();
    }

    var preview = RecurringPreview.Of(series, invoices, cfg.Current.Company.SmallBusiness);
    var bytes = await invoices.RenderAsync(preview, http.RequestAborted);
    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(bytes, "application/pdf");
});

// The logo of the company (the token changes with every upload, so it may be cached for long). An SVG cannot run anything here.
app.MapGet("/branding/logo/{token:guid}", async (Guid token, HttpContext http, MbDbContext db, FileStoreService files) =>
{
    var file = await db.StoredFile.AsNoTracking().FirstOrDefaultAsync(f => f.Token == token);
    if (file is null || !file.ContentType.StartsWith("image/", StringComparison.Ordinal))
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
    http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'";
    return Results.File(await files.ReadAllAsync(file, http.RequestAborted), file.ContentType);
});

// The PDF behind a share link: only while the link is valid and its password (if any) was given.
app.MapGet("/s/{token:guid}/pdf", async (Guid token, HttpContext http, ShareService shares, InvoiceService invoices) =>
{
    var resolution = await shares.ResolveAsync(token, http.RequestAborted);
    if (resolution.State != ShareState.Active || resolution.Link?.Invoice is not { } invoice)
    {
        return Results.StatusCode(resolution.State == ShareState.Unknown ? 404 : 410);
    }

    if (shares.NeedsPassword(resolution.Link, http))
    {
        return Results.Redirect($"/s/{token}");
    }

    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(await invoices.PdfAsync(invoice, http.RequestAborted), "application/pdf", InvoiceService.PdfName(invoice));
}).AllowAnonymous();

app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>Whether the first administrator exists: the question is asked on every request until the answer is yes, then never again.</summary>
public sealed class SetupState
{
    private volatile bool _done;

    public async Task<bool> HasAdminAsync(IServiceProvider services)
    {
        if (_done)
        {
            return true;
        }

        var db = services.GetRequiredService<MbDbContext>();
        _done = await db.User.AnyAsync(u => u.Role == UserRole.Admin);
        return _done;
    }

    public void MarkDone() => _done = true;
}

/// <summary>The command <c>dotnet MatBill.dll --healthcheck</c>: asks the running site; exit code 0 means healthy.</summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("MATBILL_PORT") ?? "2505";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}

/// <summary>Makes the entry point visible to the tests (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
