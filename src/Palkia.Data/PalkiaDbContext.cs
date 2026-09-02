using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Palkia.Data;

public sealed class PalkiaDbContext : IdentityDbContext<ApplicationUser>
{
    public PalkiaDbContext(DbContextOptions<PalkiaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(64);
        });

        // Two roles for phase 1: Admin can control services and manage
        // users; User can only view status. Seeded in Program.cs on first run.
        builder.Entity<IdentityRole>().HasData(
            new IdentityRole { Id = "role-admin", Name = "Admin", NormalizedName = "ADMIN" },
            new IdentityRole { Id = "role-user", Name = "User", NormalizedName = "USER" }
        );
    }
}
