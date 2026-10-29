using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Api;

/// <summary>The programs that may ask for invoices through the API, and how the API is used.</summary>
public sealed class IndexModel(MbDbContext db, PublicUrl urls) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    /// <summary>The address of the API, for the examples.</summary>
    public string Base => urls.Absolute("/api/v1");

    public async Task OnGetAsync()
    {
        Head(L["Api.Title"], L["Api.Sub"], (L["Admin.Settings"], null), (L["Api.Title"], null));

        var spec = new ListSpec<ApiClient>
        {
            Id = "apiclients",
            Action = "/Admin/Api",
            Label = L["Api.Title"],
            Noun = L["Api.Noun"],
            SearchPlaceholder = L["Api.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(c => EF.Functions.ILike(c.Name, like) || EF.Functions.ILike(c.KeyPrefix, like));
            },
            Filters =
            [
                new FilterSpec<ApiClient>("status", L["Common.Status"], [new("active", L["Api.Active"]), new("revoked", L["Api.RevokedPill"])],
                    (q, v) => v == "revoked" ? q.Where(c => c.RevokedDate != null) : q.Where(c => c.RevokedDate == null)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ApiClient>, bool, IOrderedQueryable<ApiClient>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(c => c.Name) : q.OrderBy(c => c.Name)),
                ["used"] = (L["Api.LastUsed"], (q, d) => d ? q.OrderByDescending(c => c.LastUsedDate) : q.OrderBy(c => c.LastUsedDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.ApiClient.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        string never = L["Api.Never"], active = L["Api.Active"], revoked = L["Api.RevokedPill"], normal = L["Api.Range.Normal"];
        string create = L["Api.Right.Create"], read = L["Api.Right.Read"], issue = L["Api.Right.Issue"];
        Table = TableModel.Of("api-table", L["Api.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), c => Ui.NameWithSub(c.Name, $"/Admin/Api/Edit?id={c.Id}", c.KeyPrefix + "…")),
            (new TableColumn(L["Api.Rights"], HideOnPhone: true), c => Ui.Text(string.Join(" · ", new[] { c.CanCreate ? create : null, c.CanRead ? read : null, c.CanIssue ? issue : null }.Where(r => r is not null)))),
            (new TableColumn(L["Api.Range"], HideOnPhone: true), c => c.NumberPattern is { Length: > 0 } p ? Ui.Mono(p) : Ui.Text(normal)),
            (new TableColumn(L["Api.LastUsed"], HideOnPhone: true), c => Ui.Text(c.LastUsedDate is null ? never : Fmt.Moment(c.LastUsedDate))),
            (new TableColumn(L["Common.Status"]), c => c.IsActive ? Ui.Pill(active, "ok") : Ui.Pill(revoked, "error")),
        ],
        c => [new RowAction("edit", L["Common.Edit"], $"/Admin/Api/Edit?id={c.Id}")]);
        Table = Table with { EmptyTitle = L["Api.Empty.Title"], EmptyText = L["Api.Empty.Text"], EmptyIcon = "plug" };
    }
}
