using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TickerQ.EntityFrameworkCore.Configurations;
using TickerQ.Utilities.Entities;

namespace PlannerBot.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options): DbContext(options)
{
    public DbSet<Response> Responses { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<SavedGame> SavedGame { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        modelBuilder.ApplyConfiguration(new TimeTickerConfigurations<TimeTickerEntity>());
        modelBuilder.ApplyConfiguration(new CronTickerConfigurations<CronTickerEntity>());
        modelBuilder.ApplyConfiguration(new CronTickerOccurrenceConfigurations<CronTickerEntity>());
    }
}

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ??
                          throw new Exception("DATABASE_URL environment variable not set");
        optionsBuilder.UseNpgsql(databaseUrl);

        return new AppDbContext(optionsBuilder.Options);
    }
}