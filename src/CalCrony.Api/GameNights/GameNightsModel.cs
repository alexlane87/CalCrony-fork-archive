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

    /// <summary>Longest season name.</summary>
    public const int SeasonNameLength = 60;

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
            // A night that is not public can never carry a league entry.
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightEventExtensions_LeagueNeedsPublic", "\"Visibility\" = 0 OR NOT \"InLeague\""));
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
            // A table that is not public can never carry a league entry: the database refuses it, whatever the code does.
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightTables_LeagueNeedsPublic", "\"Visibility\" = 0 OR NOT \"InLeague\""));
        });

        modelBuilder.Entity<GameNightLeague>(e =>
        {
            e.Property(l => l.Id).ValueGeneratedNever();
            e.HasOne<GameNightGame>().WithMany().HasForeignKey(l => l.GameId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => l.GameId).IsUnique();
            e.HasIndex(l => l.GuildId);
        });

        modelBuilder.Entity<GameNightSeason>(e =>
        {
            e.Property(s => s.Id).ValueGeneratedNever();
            e.Property(s => s.Name).HasMaxLength(SeasonNameLength);
            e.HasOne<GameNightLeague>().WithMany().HasForeignKey(s => s.LeagueId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => new { s.LeagueId, s.StartsAt });
        });

        modelBuilder.Entity<GameNightPlayerRating>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightPlayerRatings_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)"));
            e.HasOne<GameNightLeague>().WithMany().HasForeignKey(r => r.LeagueId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GameNightSeason>().WithMany().HasForeignKey(r => r.SeasonId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GameNightGuest>().WithMany().HasForeignKey(r => r.GuestId).OnDelete(DeleteBehavior.Restrict);
            // One rating per player per league per season; the all-time rows (no season) have their own unique indexes because
            // the database treats NULL seasons as different from each other.
            e.HasIndex(r => new { r.LeagueId, r.SeasonId, r.UserId }).IsUnique();
            e.HasIndex(r => new { r.LeagueId, r.SeasonId, r.GuestId }).IsUnique();
            e.HasIndex(r => new { r.LeagueId, r.UserId }).IsUnique().HasFilter("\"SeasonId\" IS NULL AND \"UserId\" IS NOT NULL");
            e.HasIndex(r => new { r.LeagueId, r.GuestId }).IsUnique().HasFilter("\"SeasonId\" IS NULL AND \"GuestId\" IS NOT NULL");
            e.HasIndex(r => r.UserId);
        });

        modelBuilder.Entity<GameNightRatingEvent>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightRatingEvents_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)"));
            e.HasOne<GameNightLeague>().WithMany().HasForeignKey(r => r.LeagueId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<GameNightResult>().WithMany().HasForeignKey(r => r.ResultId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GameNightGuest>().WithMany().HasForeignKey(r => r.GuestId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.LeagueId, r.SeasonId, r.UserId, r.PlayedAt });
            e.HasIndex(r => r.ResultId);
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
            e.HasOne<GameNightGuest>().WithMany().HasForeignKey(s => s.GuestId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<GameNightGuest>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.DisplayName).HasMaxLength(DisplayNames.MaxLength);
            e.HasIndex(x => x.GuildId);
        });

        modelBuilder.Entity<GameNightMemberName>(e =>
        {
            e.HasKey(x => new { x.GuildId, x.UserId });
            e.Property(x => x.DisplayName).HasMaxLength(DisplayNames.MaxLength);
        });

        modelBuilder.Entity<GameNightResult>(e =>
        {
            e.Property(r => r.Id).ValueGeneratedNever();
            // A table with a result keeps it (cancelled tables are not deleted); a game with results is retired, never deleted.
            e.HasOne<GameNightTable>().WithMany().HasForeignKey(r => r.TableId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<GameNightGame>().WithMany().HasForeignKey(r => r.GameId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => r.TableId);
            e.HasMany(r => r.Players).WithOne().HasForeignKey(p => p.ResultId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Values).WithOne().HasForeignKey(v => v.ResultId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Confirmations).WithOne().HasForeignKey(c => c.ResultId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GameNightResultPlayer>(e =>
        {
            e.Property(p => p.Id).ValueGeneratedNever();
            e.Property(p => p.ValuesJson).HasMaxLength(2000);
            e.ToTable(t => t.HasCheckConstraint("CK_GameNightResultPlayers_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)"));
            e.HasOne<GameNightGuest>().WithMany().HasForeignKey(p => p.GuestId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(p => new { p.ResultId, p.UserId }).IsUnique();
            e.HasIndex(p => new { p.ResultId, p.GuestId }).IsUnique();
            e.HasIndex(p => p.UserId);
        });

        modelBuilder.Entity<GameNightResultValue>(e =>
        {
            e.HasKey(v => new { v.ResultId, v.Key });
            e.Property(v => v.Key).HasMaxLength(GameRules.KeyLength);
            e.Property(v => v.Value).HasMaxLength(GameRules.TextAnswerLength);
        });

        modelBuilder.Entity<GameNightResultConfirmation>(e =>
        {
            e.HasKey(c => new { c.ResultId, c.UserId });
        });

        modelBuilder.Entity<GameNightInvite>(e =>
        {
            e.HasKey(i => new { i.Scope, i.ScopeId, i.UserId });
            e.HasIndex(i => i.UserId);
        });
    }
}
