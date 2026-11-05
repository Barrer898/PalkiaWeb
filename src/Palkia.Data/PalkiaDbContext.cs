using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Palkia.Data;

public sealed class PalkiaDbContext : IdentityDbContext<ApplicationUser>
{
    public PalkiaDbContext(DbContextOptions<PalkiaDbContext> options) : base(options)
    {
    }

    /// <summary>Admin-managed LLM gateway keys (hashed at rest - see
    /// Palkia.Infra.Ai.DbAiKeyStore). Managed from the AI page's admin
    /// section; the plaintext never touches this table.</summary>
    public DbSet<AiApiKeyRow> AiApiKeys => Set<AiApiKeyRow>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(64);
        });

        builder.Entity<AiApiKeyRow>(b =>
        {
            b.ToTable("AiApiKeys");
            b.HasKey(k => k.Id);
            b.Property(k => k.Label).HasMaxLength(64);
            b.Property(k => k.KeyHash).HasMaxLength(256);
            b.Property(k => k.KeyPrefix).HasMaxLength(16);
            b.Property(k => k.AllowedProfilesCsv).HasMaxLength(256);
        });

        // Two roles for phase 1: Admin can control services and manage
        // users; User can only view status. Seeded in Program.cs on first run.
        builder.Entity<IdentityRole>().HasData(
            new IdentityRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" },
            new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" }
        );
    }
}
