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

    /// <summary>Longest note about a game's role.</summary>
    public const int RoleNoteLength = 300;

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
            // Ids are assigned in code. Telling EF so matters for the fields collection: a new field added to
            // an already-tracked game with a Guid already set would otherwise be taken for an existing row.
            e.Property(g => g.Id).ValueGeneratedNever();
            e.Property(g => g.RoleNote).HasMaxLength(RoleNoteLength);
            e.Property(g => g.Key).HasMaxLength(GameRules.KeyLength);
            e.Property(g => g.Name).HasMaxLength(GameRules.NameLength);
            e.HasIndex(g => new { g.GuildId, g.Key }).IsUnique();
            e.HasMany(g => g.Fields).WithOne().HasForeignKey(f => f.GameId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(g => g.TypeLinks).WithOne().HasForeignKey(l => l.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightGameField>(e =>
        {
            e.Property(f => f.Id).ValueGeneratedNever();
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

        modelBuilder.Entity<GameNightTable>(e =>
        {
            e.Property(t => t.Id).ValueGeneratedNever();
            e.HasOne<Event>().WithMany().HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Cascade);
            // A game with tables is retired, never deleted.
            e.HasOne<GameNightGame>().WithMany().HasForeignKey(t => t.GameId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(t => new { t.EventId, t.Number }).IsUnique();
            e.HasIndex(t => t.GameId);
            e.HasMany(t => t.Seats).WithOne().HasForeignKey(s => s.TableId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightSignal>(e =>
        {
            e.HasKey(s => new { s.EventId, s.UserId, s.GameId });
            e.HasOne<Event>().WithMany().HasForeignKey(s => s.EventId).OnDelete(DeleteBehavior.Cascade);
            // A game with answers is retired, never deleted.
            e.HasOne<GameNightGame>().WithMany().HasForeignKey(s => s.GameId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => new { s.EventId, s.GameId });
            e.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<GameNightTableSeat>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            // Exactly one of a member or a guest. Guests (and their table) arrive with result reporting;
            // the column exists now so that step does not have to rewrite this table.
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightTableSeats_OneOccupant", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)"));
            e.HasIndex(s => new { s.TableId, s.UserId }).IsUnique();
            e.HasIndex(s => new { s.TableId, s.GuestId }).IsUnique();
            e.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<GameNightInvite>(e =>
        {
            e.HasKey(i => new { i.Scope, i.ScopeId, i.UserId });
            e.HasIndex(i => i.UserId);
        });
    }
}
