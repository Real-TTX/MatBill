using System.Linq.Expressions;
using MatBill.Data.Entities;
using MatBill.Services;
using Microsoft.EntityFrameworkCore;

namespace MatBill.Data;

public class MbDbContext(DbContextOptions<MbDbContext> options, ICurrentUser currentUser, TimeProvider clock) : DbContext(options)
{
    public DbSet<User> User => Set<User>();

    public DbSet<UserTotp> UserTotp => Set<UserTotp>();

    public DbSet<UserRecoveryCode> UserRecoveryCode => Set<UserRecoveryCode>();

    public DbSet<UserSession> UserSession => Set<UserSession>();

    public DbSet<Customer> Customer => Set<Customer>();

    public DbSet<Product> Product => Set<Product>();

    public DbSet<Invoice> Invoice => Set<Invoice>();

    public DbSet<InvoiceLine> InvoiceLine => Set<InvoiceLine>();

    public DbSet<RecurringInvoice> RecurringInvoice => Set<RecurringInvoice>();

    public DbSet<RecurringInvoiceLine> RecurringInvoiceLine => Set<RecurringInvoiceLine>();

    public DbSet<RecurringRun> RecurringRun => Set<RecurringRun>();

    public DbSet<MailDelivery> MailDelivery => Set<MailDelivery>();

    public DbSet<Notification> Notification => Set<Notification>();

    public DbSet<Quote> Quote => Set<Quote>();

    public DbSet<QuoteLine> QuoteLine => Set<QuoteLine>();

    public DbSet<Expense> Expense => Set<Expense>();

    public DbSet<BankAccount> BankAccount => Set<BankAccount>();

    public DbSet<BankConnection> BankConnection => Set<BankConnection>();

    public DbSet<BankTransaction> BankTransaction => Set<BankTransaction>();

    public DbSet<BankImport> BankImport => Set<BankImport>();

    public DbSet<NumberSequence> NumberSequence => Set<NumberSequence>();

    public DbSet<StoredFile> StoredFile => Set<StoredFile>();

    public DbSet<ShareLink> ShareLink => Set<ShareLink>();

    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    public DbSet<BackupTarget> BackupTarget => Set<BackupTarget>();

    public DbSet<BackupPlan> BackupPlan => Set<BackupPlan>();

    public DbSet<BackupRun> BackupRun => Set<BackupRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Tables are PascalCase and named like the entity; enums are stored as text; money has two decimals.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            entityType.SetTableName(entityType.ClrType.Name);

            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                // UpdateState is a number on purpose (0 = deleted, 1 = created, 2 = updated); DayOfWeek is one too.
                if (type.IsEnum && type != typeof(DayOfWeek) && type != typeof(UpdateState))
                {
                    property.SetProviderClrType(typeof(string));
                }
                else if (type == typeof(decimal) && property.GetColumnType() is null)
                {
                    property.SetColumnType("numeric(18,2)");
                }
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).HasQueryFilter(NotDeleted(entityType.ClrType));
            }
        }

        b.Entity<User>().Ignore(x => x.Initials);
        b.Entity<Customer>().Ignore(x => x.AddressLine);
        b.Entity<Invoice>().Ignore(x => x.IsDraft);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasOne(x => x.Totp).WithOne(x => x.User).HasForeignKey<UserTotp>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserTotp>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserRecoveryCode>().HasOne(x => x.UserTotp).WithMany(x => x.RecoveryCodes).HasForeignKey(x => x.UserTotpId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Customer>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.Name);
        });
        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.Name);
            e.Property(x => x.TaxRate).HasColumnType("numeric(5,2)");
        });

        // A quantity can be 0.25 hours or 1.5 days; a unit price can have more than two decimals (0.0125 € per mail), the position is rounded to cents.
        foreach (var line in new[] { typeof(InvoiceLine), typeof(RecurringInvoiceLine) })
        {
            b.Entity(line).Property(nameof(LineBase.Quantity)).HasColumnType("numeric(18,3)");
            b.Entity(line).Property(nameof(LineBase.UnitPrice)).HasColumnType("numeric(18,4)");
            b.Entity(line).Property(nameof(LineBase.TaxRate)).HasColumnType("numeric(5,2)");
            b.Entity(line).Ignore(nameof(LineBase.Net)).Ignore(nameof(LineBase.Amounts));
        }

        b.Entity<Invoice>(e =>
        {
            // The number is unique among the invoices that have one (drafts have none).
            e.HasIndex(x => x.Number).IsUnique().HasFilter("\"Number\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.DueDate });
            e.HasIndex(x => x.InvoiceDate);
            e.HasIndex(x => x.SendState);
            // A series never invoices the same date twice, even if two runs met.
            e.HasIndex(x => new { x.RecurringInvoiceId, x.RecurringDate }).IsUnique().HasFilter("\"RecurringInvoiceId\" IS NOT NULL AND \"RecurringDate\" IS NOT NULL");
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PdfFile).WithMany().HasForeignKey(x => x.PdfFileId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.RecurringInvoice).WithMany().HasForeignKey(x => x.RecurringInvoiceId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<InvoiceLine>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.Position });
            e.HasOne(x => x.Invoice).WithMany(x => x.Lines).HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringInvoice>(e =>
        {
            e.HasIndex(x => new { x.Status, x.NextRunDate });
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<RecurringInvoiceLine>(e =>
        {
            e.HasIndex(x => new { x.RecurringInvoiceId, x.Position });
            e.HasOne(x => x.RecurringInvoice).WithMany(x => x.Lines).HasForeignKey(x => x.RecurringInvoiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RecurringRun>(e =>
        {
            e.HasIndex(x => new { x.RecurringInvoiceId, x.RunDate });
            e.HasOne(x => x.RecurringInvoice).WithMany().HasForeignKey(x => x.RecurringInvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<MailDelivery>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.AttemptDate });
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ReadDate, x.OccurredDate });
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique().HasFilter("\"Key\" IS NOT NULL");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Quote>().Ignore(x => x.IsDraft);
        b.Entity(typeof(QuoteLine)).Property(nameof(LineBase.Quantity)).HasColumnType("numeric(18,3)");
        b.Entity(typeof(QuoteLine)).Property(nameof(LineBase.UnitPrice)).HasColumnType("numeric(18,4)");
        b.Entity(typeof(QuoteLine)).Property(nameof(LineBase.TaxRate)).HasColumnType("numeric(5,2)");
        b.Entity(typeof(QuoteLine)).Ignore(nameof(LineBase.Net)).Ignore(nameof(LineBase.Amounts));
        b.Entity<Quote>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique().HasFilter("\"Number\" IS NOT NULL");
            e.HasIndex(x => new { x.Status, x.ValidUntil });
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.PdfFile).WithMany().HasForeignKey(x => x.PdfFileId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<QuoteLine>(e =>
        {
            e.HasIndex(x => new { x.QuoteId, x.Position });
            e.HasOne(x => x.Quote).WithMany(x => x.Lines).HasForeignKey(x => x.QuoteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Expense>(e =>
        {
            e.HasIndex(x => x.Number).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasIndex(x => x.ExpenseDate);
            e.HasIndex(x => x.Status);
            e.Property(x => x.TaxRate).HasColumnType("numeric(5,2)");
            e.HasOne(x => x.ReceiptFile).WithMany().HasForeignKey(x => x.ReceiptFileId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<BankAccount>().HasOne(x => x.BankConnection).WithMany().HasForeignKey(x => x.BankConnectionId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<BankConnection>().HasIndex(x => x.Token).IsUnique();
        b.Entity<BankTransaction>(e =>
        {
            e.HasIndex(x => new { x.BankAccountId, x.ImportKey }).IsUnique();
            e.HasIndex(x => new { x.State, x.BookingDate });
            e.HasIndex(x => x.InvoiceId);
            e.HasIndex(x => x.ExpenseId);
            e.HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.Expense).WithMany().HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<BankImport>().HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<NumberSequence>().HasIndex(x => new { x.Name, x.Period }).IsUnique();

        b.Entity<StoredFile>().HasIndex(x => x.Token).IsUnique();
        b.Entity<ShareLink>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLog>().HasIndex(x => x.OccurredDate);
        b.Entity<BackupPlan>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupRun>().HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);
    }

    private static LambdaExpression NotDeleted(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var state = Expression.Property(parameter, nameof(ISoftDeletable.UpdateState));
        var body = Expression.NotEqual(state, Expression.Constant(UpdateState.Deleted));
        return Expression.Lambda(body, parameter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns and turns a delete of a soft-deletable record into a state change.</summary>
    private void Stamp()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>().ToList())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = UpdateState.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreateDate == default)
                    {
                        entry.Entity.CreateDate = now;
                    }

                    entry.Entity.CreateUserId ??= userId;
                    entry.Entity.UpdateDate = entry.Entity.CreateDate;
                    entry.Entity.UpdateUserId = entry.Entity.CreateUserId;
                    if (entry.Entity is ISoftDeletable added && added.UpdateState == UpdateState.Deleted)
                    {
                        added.UpdateState = UpdateState.Created;
                    }

                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Property(nameof(AuditedEntity.CreateDate)).IsModified = false;
                    entry.Property(nameof(AuditedEntity.CreateUserId)).IsModified = false;
                    if (entry.Entity is ISoftDeletable modified && modified.UpdateState == UpdateState.Created)
                    {
                        modified.UpdateState = UpdateState.Updated;
                    }

                    break;
            }
        }
    }
}
