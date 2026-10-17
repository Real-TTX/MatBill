using MatBill.Data;
using MatBill.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatBill.Services;

/// <summary>
/// Notes for the people who work with invoices: a series that runs soon, an invoice that a series made, a problem. Every active person gets each note once
/// (the key keeps a note from being written twice); the bell in the header counts the unread ones.
/// </summary>
public sealed class NotificationService(MbDbContext db, TimeProvider clock, IMemoryCache cache)
{
    public async Task NotifyAllAsync(NotificationTone tone, string title, string? text, string? url, string? key = null, CancellationToken ct = default)
    {
        var users = await db.User.AsNoTracking().Where(u => u.IsActive).Select(u => u.Id).ToListAsync(ct);
        var already = key is null ? [] : await db.Notification.AsNoTracking().Where(n => n.Key == key).Select(n => n.UserId).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var userId in users.Except(already))
        {
            db.Notification.Add(new Notification { UserId = userId, Tone = tone, Title = Cut(title, 200), Text = text is null ? null : Cut(text, 1000), Url = url, Key = key, OccurredDate = now });
            cache.Remove(CountKey(userId));
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<int> UnreadCountAsync(long userId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync(CountKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return await db.Notification.CountAsync(n => n.UserId == userId && n.ReadDate == null, ct);
        });

    public async Task MarkReadAsync(long userId, long? id = null, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Notification.Where(n => n.UserId == userId && n.ReadDate == null && (id == null || n.Id == id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadDate, now), ct);
        cache.Remove(CountKey(userId));
    }

    private static string CountKey(long userId) => "unread-" + userId;

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
