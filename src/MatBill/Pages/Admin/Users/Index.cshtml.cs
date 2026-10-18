using MatBill.Data;
using MatBill.Data.Entities;
using MatBill.Services;
using MatBill.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Pages.Admin.Users;

/// <summary>Everybody who can sign in: administrators and users.</summary>
public sealed class IndexModel(MbDbContext db, ICurrentUser current) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head(L["Admin.Users.Title"], L["Admin.Users.Sub"], (L["Admin.Settings"], null), (L["Admin.Users.Title"], null));

        var spec = new ListSpec<User>
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = L["Admin.Users.Title"],
            Noun = L["Admin.Users.Noun"],
            SearchPlaceholder = L["Admin.Users.Search"],
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.ILike(u.DisplayName, like) || EF.Functions.ILike(u.Email, like));
            },
            Filters =
            [
                new FilterSpec<User>("role", L["Admin.Users.Role"], ListBuilder.EnumOptions<UserRole>(Labels.Of), (q, v) => q.Where(u => u.Role == Enum.Parse<UserRole>(v))),
                new FilterSpec<User>("status", L["Common.Status"], [new("active", L["Admin.Users.Active"]), new("locked", L["Admin.Users.Locked"])], (q, v) => v == "locked" ? q.Where(u => !u.IsActive) : q.Where(u => u.IsActive)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<User>, bool, IOrderedQueryable<User>>)>
            {
                ["name"] = (L["Common.Name"], (q, d) => d ? q.OrderByDescending(u => u.DisplayName) : q.OrderBy(u => u.DisplayName)),
                ["email"] = (L["Common.Email"], (q, d) => d ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email)),
                ["role"] = (L["Admin.Users.Role"], (q, d) => d ? q.OrderByDescending(u => u.Role).ThenBy(u => u.DisplayName) : q.OrderBy(u => u.Role).ThenBy(u => u.DisplayName)),
                ["login"] = (L["Admin.Users.LastLogin"], (q, d) => d ? q.OrderByDescending(u => u.LastLoginDate) : q.OrderBy(u => u.LastLoginDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.User.AsNoTracking().Include(u => u.Totp), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var me = current.UserId;
        var thatsYou = L["Admin.Users.ThatsYou"].Value;
        var never = L["Admin.Users.Never"].Value;
        var on = L["Admin.Users.On"].Value;
        var off = L["Admin.Users.Off"].Value;
        var active = L["Admin.Users.Pill.Active"].Value;
        var locked = L["Admin.Users.Pill.Locked"].Value;
        Table = TableModel.Of("user-table", L["Admin.Users.Title"], result.Items,
        [
            (new TableColumn(L["Common.Name"]), u => Ui.NameWithSub(u.DisplayName, $"/Admin/Users/Edit?id={u.Id}", u.Id == me ? thatsYou : null)),
            (new TableColumn(L["Common.Email"], HideOnPhone: true), u => Ui.Text(u.Email)),
            (new TableColumn(L["Admin.Users.Role"]), u => Ui.Text(Labels.Of(u.Role))),
            (new TableColumn(L["Admin.Users.TwoFactor"], HideOnPhone: true), u => Ui.Text(u.Totp is { IsConfirmed: true } ? on : off)),
            (new TableColumn(L["Admin.Users.LastLogin"], HideOnPhone: true), u => Ui.Text(u.LastLoginDate is null ? never : Fmt.Moment(u.LastLoginDate))),
            (new TableColumn(L["Common.Status"]), u => u.IsActive ? Ui.Pill(active, "ok") : Ui.Pill(locked, "error")),
        ],
        u => [new RowAction("edit", L["Common.Edit"], $"/Admin/Users/Edit?id={u.Id}")]);
        Table = Table with { EmptyTitle = L["Admin.Empty.Title"], EmptyText = L["Admin.Empty.Text"], EmptyIcon = "users" };
    }
}
