using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.GameNights;

/// <summary>EF configuration for the Game Nights tables. Called from CalCronyDbContext.OnModelCreating;
/// each step that adds tables adds its blocks here, so the core context file does not change again.</summary>
public static class GameNightsModel
{
    /// <summary>Longest game-type label.</summary>
    public const int GameTypeLabelLength = 60;

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
            // A venue in use is retired, not deleted; the endpoint refuses a delete (409) first.
            e.HasOne<GameNightVenue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.VenueId);
        });

        modelBuilder.Entity<GameNightVenue>(e =>
        {
            e.Property(v => v.Name).HasMaxLength(VenueLimits.NameLength);
            e.Property(v => v.Address).HasMaxLength(VenueLimits.AddressLength);
            e.HasIndex(v => v.GuildId);
            e.HasMany(v => v.Areas).WithOne().HasForeignKey(a => a.VenueId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightVenueArea>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(VenueLimits.AreaNameLength);
            e.HasIndex(a => new { a.VenueId, a.Name }).IsUnique();
        });

        modelBuilder.Entity<GameNightEventArea>(e =>
        {
            e.HasKey(x => new { x.EventId, x.AreaId });
            e.HasOne<GameNightEventExtension>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GameNightVenueArea>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightGame>(e =>
        {
            e.Property(g => g.Key).HasMaxLength(GameRules.KeyLength);
            e.Property(g => g.Name).HasMaxLength(GameRules.NameLength);
            e.HasIndex(g => new { g.GuildId, g.Key }).IsUnique();
            e.HasMany(g => g.Fields).WithOne().HasForeignKey(f => f.GameId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(g => g.TypeLinks).WithOne().HasForeignKey(l => l.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightGameField>(e =>
        {
            e.Property(f => f.Key).HasMaxLength(GameRules.KeyLength);
            e.Property(f => f.Label).HasMaxLength(GameRules.LabelLength);
            e.HasIndex(f => new { f.GameId, f.Key }).IsUnique();
        });

        modelBuilder.Entity<GameNightGameType>(e =>
        {
            e.HasKey(t => new { t.GuildId, t.RoleId });
            e.Property(t => t.Label).HasMaxLength(GameTypeLabelLength);
        });

        modelBuilder.Entity<GameNightGameTypeLink>(e =>
        {
            e.HasKey(l => new { l.GameId, l.TypeRoleId });
        });

        modelBuilder.Entity<GameNightInvite>(e =>
        {
            e.HasKey(i => new { i.Scope, i.ScopeId, i.UserId });
            e.HasIndex(i => i.UserId);
        });
    }
}
