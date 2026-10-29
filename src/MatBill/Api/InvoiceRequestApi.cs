using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;

namespace MatBill.Api;

/// <summary>
/// The API for other programs of the owner (the first: Matmon.Cloud, which bills its partners), under /api/v1, with a key instead of a sign-in
/// ("Authorization: Bearer mbk_…" or "X-Api-Key: mbk_…"). A client asks for invoices; MatBill stays the one place where invoices live and numbers are given.
/// No cookie, no anti-forgery token, answers are JSON and never cached, at most <see cref="PermitsPerMinute"/> calls a minute per address.
/// Described in docs/API.md.
/// </summary>
public static class InvoiceRequestApi
{
    public const string RatePolicy = "api";

    public const int PermitsPerMinute = 120;

    private const string ClientItem = "mb.apiclient";

    private const long MaxBodyBytes = 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>The limiter of the API: a fixed window per client address (a key that is wrong costs its attempts too).</summary>
    public static void AddInvoiceRequestApi(this IServiceCollection services)
    {
        services.AddScoped<ApiClientService>();
        services.AddScoped<InvoiceRequestService>();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "rate_limited", message = $"More than {PermitsPerMinute} calls a minute. Try again in a minute." }, Json, ct);
            };
            options.AddPolicy(RatePolicy, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PermitsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });
    }

    public static void MapInvoiceRequestApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RatePolicy);
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var ip = http.Connection.RemoteIpAddress?.ToString();
            var client = await http.RequestServices.GetRequiredService<ApiClientService>().ValidateAsync(ApiClientService.Presented(http.Request), ip, http.RequestAborted);
            if (client is null)
            {
                http.Response.Headers.WWWAuthenticate = "Bearer";
                return Error(401, "invalid_key", "No valid API key. Send it as \"Authorization: Bearer mbk_…\" or \"X-Api-Key: mbk_…\".");
            }

            http.Items[ClientItem] = client;
            // what the client does is logged under its name (activity log, audit columns)
            using var actor = http.RequestServices.GetRequiredService<ICurrentUser>().RunAs(null, "API: " + client.Name, UserRole.User);
            try
            {
                return await next(context);
            }
            catch (ApiRequestException ex)
            {
                return Error(ex.Status, ex.Code, ex.Message, ex.Problems);
            }
        });

        api.MapPost("/invoice-requests", CreateAsync);
        api.MapGet("/invoice-requests", ByReferenceAsync);
        api.MapGet("/invoice-requests/{id:long}", GetAsync);
        api.MapGet("/invoices/{id:long}/pdf", PdfAsync);
        api.MapGet("/invoices/{id:long}/xml", XmlAsync);

        // anything else under /api/v1 is a JSON 404, never the HTML error page
        api.Map("/{**rest}", () => Error(404, "not_found", "There is no such API call. See docs/API.md."));
    }

    private static ApiClient Client(HttpContext http) => (ApiClient)http.Items[ClientItem]!;

    private static IResult Error(int status, string code, string message, IReadOnlyList<string>? problems = null)
        => Results.Json(problems is { Count: > 0 } ? new { error = code, message, problems } : (object)new { error = code, message }, Json, statusCode: status);

    private static IResult? MayRead(HttpContext http)
        => Client(http).CanRead ? null : Error(403, "read_not_allowed", "This API client may not read invoices.");

    /// <summary>POST /invoice-requests: 201 with the new invoice, 200 with the one the reference already stands for.</summary>
    private static async Task<IResult> CreateAsync(HttpContext http, InvoiceRequestService requests, PublicUrl urls)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        InvoiceRequestInput? input;
        try
        {
            input = await JsonSerializer.DeserializeAsync<InvoiceRequestInput>(http.Request.Body, Json, http.RequestAborted);
        }
        catch (JsonException ex)
        {
            return Error(400, "invalid_json", "The body is not valid JSON for an invoice request: " + ex.Message);
        }
        catch (BadHttpRequestException)
        {
            return Error(413, "too_large", $"The body is larger than {MaxBodyBytes / 1024} KB.");
        }

        if (input is null)
        {
            return Error(400, "invalid_json", "The body is empty.");
        }

        var (invoice, created) = await requests.CreateAsync(Client(http), input, http.RequestAborted);
        var body = await requests.DescribeAsync(invoice, urls.Absolute, http.RequestAborted);
        return created ? Results.Json(body, Json, statusCode: 201) : Results.Json(body, Json);
    }

    /// <summary>GET /invoice-requests?reference=…: the invoice of a reference.</summary>
    private static async Task<IResult> ByReferenceAsync(HttpContext http, string? reference, InvoiceRequestService requests, PublicUrl urls)
    {
        if (MayRead(http) is { } forbidden)
        {
            return forbidden;
        }

        if (string.IsNullOrWhiteSpace(reference))
        {
            return Error(400, "reference_required", "Ask with ?reference=<the reference of the request>.");
        }

        var invoice = await requests.FindByReferenceAsync(Client(http), reference.Trim(), http.RequestAborted);
        return invoice is null
            ? Error(404, "not_found", "No invoice request with this reference.")
            : Results.Json(await requests.DescribeAsync(invoice, urls.Absolute, http.RequestAborted), Json);
    }

    private static async Task<IResult> GetAsync(long id, HttpContext http, InvoiceRequestService requests, PublicUrl urls)
    {
        if (MayRead(http) is { } forbidden)
        {
            return forbidden;
        }

        var invoice = await requests.FindAsync(Client(http), id, http.RequestAborted);
        return invoice is null
            ? Error(404, "not_found", "No invoice request with this id for this client.")
            : Results.Json(await requests.DescribeAsync(invoice, urls.Absolute, http.RequestAborted), Json);
    }

    /// <summary>The issued invoice of the client: its stored PDF (a ZUGFeRD PDF unless the settings say otherwise), the same file as in MatBill.</summary>
    private static async Task<IResult> PdfAsync(long id, HttpContext http, InvoiceRequestService requests, InvoiceService invoices)
    {
        if (await IssuedAsync(http, requests, invoices, id) is not { } found)
        {
            return await NotIssuedAsync(http, requests, id);
        }

        return Results.File(await invoices.PdfAsync(found, http.RequestAborted), "application/pdf", InvoiceService.PdfName(found));
    }

    /// <summary>The e-invoice of the client's issued invoice as XML: EN 16931 (CII), with ?xrechnung=1 the XRechnung.</summary>
    private static async Task<IResult> XmlAsync(long id, HttpContext http, InvoiceRequestService requests, InvoiceService invoices)
    {
        if (await IssuedAsync(http, requests, invoices, id) is not { } found)
        {
            return await NotIssuedAsync(http, requests, id);
        }

        var xrechnung = http.Request.Query.ContainsKey("xrechnung");
        try
        {
            return Results.File(invoices.Xml(found, xrechnung), "application/xml", Path.ChangeExtension(InvoiceService.PdfName(found), xrechnung ? ".xrechnung.xml" : ".zugferd.xml"));
        }
        catch (Exception ex) when (ex is InvoiceException or ArgumentException or InvalidOperationException)
        {
            return Error(422, "einvoice_failed", "The e-invoice could not be made: " + ex.Message);
        }
    }

    /// <summary>The issued invoice <paramref name="id"/> of the client, loaded the way the documents need it; null when there is none or it is a draft.</summary>
    private static async Task<Invoice?> IssuedAsync(HttpContext http, InvoiceRequestService requests, InvoiceService invoices, long id)
    {
        if (!Client(http).CanRead)
        {
            return null;
        }

        var own = await requests.FindAsync(Client(http), id, http.RequestAborted);
        if (own is null || own.Number is null || own.UpdateState == Data.UpdateState.Deleted)
        {
            return null;
        }

        return await invoices.FindAsync(id, http.RequestAborted);
    }

    private static async Task<IResult> NotIssuedAsync(HttpContext http, InvoiceRequestService requests, long id)
    {
        if (MayRead(http) is { } forbidden)
        {
            return forbidden;
        }

        return await requests.FindAsync(Client(http), id, http.RequestAborted) is { Number: null, UpdateState: not Data.UpdateState.Deleted }
            ? Error(409, "not_issued", "The invoice is still a draft: it has no number and no document yet.")
            : Error(404, "not_found", "No issued invoice with this id for this client.");
    }
}
