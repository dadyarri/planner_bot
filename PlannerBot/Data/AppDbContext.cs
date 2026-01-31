using Microsoft.EntityFrameworkCore;
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