using CalCrony.Api.GameNights;
using Microsoft.EntityFrameworkCore;

namespace CalCrony.Api.Data;

// The Game Nights half of the context (partial, so the core file keeps one added word and one
// added call). Model configuration lives in GameNightsModel.
public partial class CalCronyDbContext
{
    public DbSet<GameNightsTierRole> GameNightsTierRoles => Set<GameNightsTierRole>();
    public DbSet<GameNightEventExtension> GameNightEventExtensions => Set<GameNightEventExtension>();
    public DbSet<GameNightInvite> GameNightInvites => Set<GameNightInvite>();
    public DbSet<GameNightVenue> GameNightVenues => Set<GameNightVenue>();
    public DbSet<GameNightVenueArea> GameNightVenueAreas => Set<GameNightVenueArea>();
    public DbSet<GameNightEventArea> GameNightEventAreas => Set<GameNightEventArea>();
    public DbSet<GameNightGame> GameNightGames => Set<GameNightGame>();
    public DbSet<GameNightGameField> GameNightGameFields => Set<GameNightGameField>();
    public DbSet<GameNightGameType> GameNightGameTypes => Set<GameNightGameType>();
    public DbSet<GameNightGameTypeLink> GameNightGameTypeLinks => Set<GameNightGameTypeLink>();
    public DbSet<GameNightTable> GameNightTables => Set<GameNightTable>();
    public DbSet<GameNightSignal> GameNightSignals => Set<GameNightSignal>();
    public DbSet<GameNightResult> GameNightResults => Set<GameNightResult>();
    public DbSet<GameNightResultPlayer> GameNightResultPlayers => Set<GameNightResultPlayer>();
    public DbSet<GameNightResultValue> GameNightResultValues => Set<GameNightResultValue>();
    public DbSet<GameNightResultConfirmation> GameNightResultConfirmations => Set<GameNightResultConfirmation>();
    public DbSet<GameNightGuest> GameNightGuests => Set<GameNightGuest>();
    public DbSet<GameNightMemberName> GameNightMemberNames => Set<GameNightMemberName>();
    public DbSet<GameNightTableSeat> GameNightTableSeats => Set<GameNightTableSeat>();
}
