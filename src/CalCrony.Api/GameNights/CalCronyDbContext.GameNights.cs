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
}
