using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TickerQ.EntityFrameworkCore.Configurations;
using TickerQ.Utilities.Entities;

namespace PlannerBot.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Response> Responses { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<SavedGame> SavedGame { get; set; }
    public DbSet<VoteSession> VoteSessions { get; set; }
    public DbSet<VoteSessionVote> VoteSessionVotes { get; set; }
    public DbSet<ForumThread> ForumThreads { get; set; }
    public DbSet<Campaign> Campaigns { get; set; }
    public DbSet<CampaignMember> CampaignMembers { get; set; }
    public DbSet<ServiceThread> ServiceThreads { get; set; }
    public DbSet<AvailableSlot> AvailableSlots { get; set; }
    public DbSet<CampaignOrderState> CampaignOrderStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<VoteSessionVote>()
            .HasIndex(v => new { v.VoteSessionId, v.UserId })
            .IsUnique();

        modelBuilder.Entity<ForumThread>()
            .HasIndex(ft => new { ft.ChatId, ft.ThreadId })
            .IsUnique();

        modelBuilder.Entity<CampaignMember>()
            .HasKey(cm => new { cm.CampaignId, cm.UserId });

        modelBuilder.Entity<CampaignMember>()
            .HasIndex(cm => new { cm.CampaignId, cm.UserId })
            .IsUnique();

        modelBuilder.Entity<ServiceThread>()
            .HasIndex(st => st.ForumThreadId)
            .IsUnique();

        modelBuilder.Entity<CampaignOrderState>()
            .HasIndex(cos => cos.ChatId)
            .IsUnique();

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