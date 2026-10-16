using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ExpenseTracker.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<BankSession> BankSessions => Set<BankSession>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<Transaction> Transactions => Set<Transaction>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // SQLite can't order/compare DateTimeOffset or decimal natively; store them in sortable forms.
        builder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        builder.Properties<decimal>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<BankSession>().HasIndex(s => s.SessionId).IsUnique();
        model.Entity<BankAccount>().HasIndex(a => a.Uid).IsUnique();

        var tx = model.Entity<Transaction>();
        tx.HasIndex(t => new { t.AccountUid, t.ExternalId }).IsUnique();
        tx.HasIndex(t => t.BookingDate);
        tx.HasIndex(t => t.Category);
    }
}
