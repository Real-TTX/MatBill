using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Shares;

/// <summary>Every link that shows a project or a goal to somebody without an account: who has it, how long it holds, how often it was opened.</summary>
public sealed class IndexModel(MbDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head(L["Admin.Shares.Title"], L["Admin.Shares.Sub"], (L["Admin.Settings"], null), (L["Admin.Shares.Title"], null));
        var now = clock.GetUtcNow().UtcDateTime;

        var spec = new ListSpec<ShareLink>
        {
            Id = "shares",
            Action = "/Admin/Shares",
            Label = L["Admin.Shares.Title"],
            Noun = L["Admin.Shares.Noun"],
            SearchPlaceholder = L["Admin.Shares.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(l => EF.Functions.ILike(l.Title, like) || EF.Functions.ILike(l.RecipientName ?? "", like) || (l.Invoice != null && l.Invoice.Number != null && EF.Functions.ILike(l.Invoice.Number, like)));
            },
            Filters =
            [
                new FilterSpec<ShareLink>("status", L["Common.Status"], [new("active", L["Admin.Shares.Filter.Active"]), new("expired", L["Admin.Shares.Filter.Expired"])], (q, v) => v == "active" ? q.Where(l => l.RevokedDate == null && l.ExpiresDate > now) : q.Where(l => l.RevokedDate != null || l.ExpiresDate <= now)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ShareLink>, bool, IOrderedQueryable<ShareLink>>)>
            {
                ["created"] = (L["Admin.Shares.Created"], (q, d) => d ? q.OrderBy(l => l.CreateDate) : q.OrderByDescending(l => l.CreateDate)),
                ["expires"] = (L["Admin.Shares.ValidUntil"], (q, d) => d ? q.OrderByDescending(l => l.ExpiresDate) : q.OrderBy(l => l.ExpiresDate)),
                ["views"] = (L["Admin.Shares.Views"], (q, d) => d ? q.OrderBy(l => l.ViewCount) : q.OrderByDescending(l => l.ViewCount)),
            },
            DefaultSort = "created",
        };

        var result = await ListBuilder.RunAsync(db.ShareLink.AsNoTracking().Include(l => l.Invoice), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var revoked = L["Admin.Shares.Pill.Revoked"].Value;
        var expired = L["Admin.Shares.Pill.Expired"].Value;
        var valid = L["Admin.Shares.Pill.Valid"].Value;
        Table = TableModel.Of("share-table", L["Admin.Shares.Title"], result.Items,
        [
            (new TableColumn(L["Admin.Shares.Link"]), l => Ui.NameWithSub(l.Title, $"/Admin/Shares/Edit?id={l.Id}", l.RecipientName is { Length: > 0 } ? L["Admin.Shares.For", l.RecipientName].Value : null)),
            (new TableColumn(L["Admin.Shares.Invoice"], HideOnPhone: true), l => Ui.Text(l.Invoice is null ? "–" : $"{l.Invoice.Number} · {l.Invoice.RecipientName}")),
            (new TableColumn(L["Admin.Shares.ValidUntil"], KeepLabel: true), l => Ui.Text(Fmt.Date(l.ExpiresDate))),
            (new TableColumn(L["Admin.Shares.Views"], Numeric: true, HideOnPhone: true), l => Ui.Text(l.ViewCount.ToString())),
            (new TableColumn(L["Common.Status"]), l => l.RevokedDate is not null ? Ui.Pill(revoked, "plain") : l.ExpiresDate <= now ? Ui.Pill(expired, "plain") : Ui.Pill(valid, "ok")),
        ],
        l => [new RowAction("edit", L["Admin.Shares.Open"], $"/Admin/Shares/Edit?id={l.Id}")])
        with
        {
            EmptyTitle = Toolbar.Total == 0 ? L["Admin.Shares.Empty.Title"] : L["Admin.Empty.Title"],
            EmptyText = Toolbar.Total == 0 ? L["Admin.Shares.Empty.Text"] : L["Admin.Empty.Text"],
            EmptyIcon = "share",
        };
    }
}
