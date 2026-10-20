using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Services;

/// <summary>A page of results of a lookup and how many there are in all.</summary>
public sealed record LookupPage<T>(IReadOnlyList<T> Items, int Total);

/// <summary>
/// The search behind the pickers of the editor (customers, products): the database finds the few that match, so a page never carries all customers or products.
/// Every word of the query has to be in one of the fields; what starts with the query comes first.
/// </summary>
public static class Lookup
{
    public const int MaxTake = 50;

    public static async Task<LookupPage<CustomerJson>> CustomersAsync(MbDbContext db, string? query, string? city, CustomerKind? kind, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Customer.AsNoTracking();
        foreach (var word in Words(query))
        {
            var like = Like.Contains(word);
            q = q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.Number, like) || EF.Functions.ILike(c.ContactName ?? "", like)
                || EF.Functions.ILike(c.City ?? "", like) || EF.Functions.ILike(c.PostalCode ?? "", like) || EF.Functions.ILike(c.Email ?? "", like));
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            var like = Like.Contains(city);
            q = q.Where(c => EF.Functions.ILike(c.City ?? "", like) || EF.Functions.ILike(c.PostalCode ?? "", like));
        }

        if (kind is { } k)
        {
            q = q.Where(c => c.Kind == k);
        }

        var start = Starts(query);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(c => start != null && EF.Functions.ILike(c.Name, start) ? 0 : 1).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(ct);
        return new LookupPage<CustomerJson>([.. items.Select(CustomerJson.Of)], total);
    }

    public static async Task<LookupPage<ProductJson>> ProductsAsync(MbDbContext db, string? query, ProductKind? kind, int skip, int take, CancellationToken ct = default)
    {
        var q = db.Product.AsNoTracking().Where(p => p.IsActive);
        foreach (var word in Words(query))
        {
            var like = Like.Contains(word);
            q = q.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Description ?? "", like) || EF.Functions.ILike(p.Sku ?? "", like));
        }

        if (kind is { } k)
        {
            q = q.Where(p => p.Kind == k);
        }

        var start = Starts(query);
        var total = await q.CountAsync(ct);
        var items = await q.OrderBy(p => start != null && EF.Functions.ILike(p.Name, start) ? 0 : 1).ThenBy(p => p.Name).ThenBy(p => p.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxTake)).ToListAsync(ct);
        return new LookupPage<ProductJson>([.. items.Select(ProductJson.Of)], total);
    }

    /// <summary>The name of a customer by its id as a list filter shows it (null: no such customer), for a customer filter that searches (<see cref="UiKit.FilterSpec{T}"/>).</summary>
    public static Func<string, Task<string?>> CustomerName(MbDbContext db)
        => async value => long.TryParse(value, out var id) ? await db.Customer.AsNoTracking().Where(c => c.Id == id).Select(c => c.Name).FirstOrDefaultAsync() : null;

    private static string[] Words(string? query)
        => (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(6).ToArray();

    private static string? Starts(string? query)
        => string.IsNullOrWhiteSpace(query) ? null : query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
