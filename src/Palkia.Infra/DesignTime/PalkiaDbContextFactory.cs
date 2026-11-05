using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Palkia.Data;

namespace Palkia.Infra.DesignTime;

/// <summary>
/// Lets `dotnet ef migrations add ... --startup-project src/Palkia.Infra`
/// run without the Web app (which would need a Design package reference of
/// its own and pulls the whole host). Migrations live in Palkia.Data; this
/// only supplies the DbContext + provider at design time, matching
/// Program.cs's runtime wiring exactly (SQLite).
/// </summary>
public sealed class PalkiaDbContextFactory : IDesignTimeDbContextFactory<PalkiaDbContext>
{
    public PalkiaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PalkiaDbContext>()
            .UseSqlite("Data Source=palkia-design.db")
            .Options;

        return new PalkiaDbContext(options);
    }
}
