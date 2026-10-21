using MatBill.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace MatBill.Services;

/// <summary>
/// The preview of what an editor holds now, saved or not: the document is drawn once as page images and kept a few minutes under a random token, the PDF is drawn
/// when it is downloaded. Nothing is written to the database; the token is the only way to it (GET /preview/{token}/{page}, /preview/{token}.pdf).
/// </summary>
public sealed class PreviewStore(IMemoryCache cache)
{
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);

    public sealed record Entry(IReadOnlyList<byte[]> Pages, Func<byte[]> Pdf, string FileName);

    public (Guid Token, int Pages) Put(InvoiceDocument doc, CompanyConfig company, LogoImage? logo, string fileName)
    {
        var token = Guid.NewGuid();
        var pages = InvoicePdf.RenderImages(doc, company, logo, dpi: 110).ToList();
        cache.Set(Key(token), new Entry(pages, () => InvoicePdf.Render(doc, company, logo), fileName), Keep);
        return (token, pages.Count);
    }

    public Entry? Get(Guid token) => cache.TryGetValue(Key(token), out Entry? entry) ? entry : null;

    private static string Key(Guid token) => "unsaved-preview:" + token.ToString("N");
}
