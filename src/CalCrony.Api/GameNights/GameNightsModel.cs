using CalCrony.Api.Data;
using CalCrony.Contracts.GameNights;
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

        modelBuilder.Entity<GameNightInvite>(e =>
        {
            e.HasKey(i => new { i.Scope, i.ScopeId, i.UserId });
            e.HasIndex(i => i.UserId);
        });
    }
}
