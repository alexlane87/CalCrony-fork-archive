using CalCrony.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>EF configuration for the Game Nights tables. Called from CalCronyDbContext.OnModelCreating;
/// each step that adds tables adds its blocks here, so the core context file does not change again.</summary>
public static class GameNightsModel
{
    /// <summary>Configures the Game Nights entities.</summary>
    /// <param name="modelBuilder">The EF model builder.</param>
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GameNightsTierRole>(e =>
        {
            e.HasKey(t => new { t.GuildId, t.RoleId });
        });

        modelBuilder.Entity<GameNightEventExtension>(e =>
        {
            e.HasKey(x => x.EventId);
            e.HasOne<Event>().WithOne().HasForeignKey<GameNightEventExtension>(x => x.EventId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightInvite>(e =>
        {
            e.HasKey(i => new { i.Scope, i.ScopeId, i.UserId });
            e.HasIndex(i => i.UserId);
        });
    }
}
