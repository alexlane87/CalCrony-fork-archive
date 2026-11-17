using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 9 (fork): leagues. Adds GameNightLeagues (one per game), GameNightSeasons,
    /// GameNightPlayerRatings and GameNightRatingEvents (the league entries), and an InLeague flag on GameNightTables and
    /// GameNightEventExtensions, each with a check constraint (Visibility = 0 OR NOT InLeague) so a table or night that is not
    /// public can never feed a league, whatever any code does. Ratings are rebuilt from confirmed results, so none of this is
    /// source data. Additive and defaulted, so rolling the IMAGE back needs no Down; Down drops the tables and columns.</summary>
    /// <inheritdoc />
    public partial class GameNightsLeagues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "InLeague",
                table: "GameNightTables",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "InLeague",
                table: "GameNightEventExtensions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "GameNightLeagues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightLeagues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightLeagues_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameNightRatingEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeagueId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Placement = table.Column<int>(type: "integer", nullable: true),
                    Before = table.Column<double>(type: "double precision", nullable: false),
                    After = table.Column<double>(type: "double precision", nullable: false),
                    PlayedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightRatingEvents", x => x.Id);
                    table.CheckConstraint("CK_GameNightRatingEvents_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GameNightRatingEvents_GameNightGuests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "GameNightGuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameNightRatingEvents_GameNightLeagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "GameNightLeagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameNightRatingEvents_GameNightResults_ResultId",
                        column: x => x.ResultId,
                        principalTable: "GameNightResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameNightSeasons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeagueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    StartsAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightSeasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightSeasons_GameNightLeagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "GameNightLeagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightPlayerRatings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LeagueId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeasonId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rating = table.Column<double>(type: "double precision", nullable: false),
                    GamesPlayed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightPlayerRatings", x => x.Id);
                    table.CheckConstraint("CK_GameNightPlayerRatings_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GameNightPlayerRatings_GameNightGuests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "GameNightGuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameNightPlayerRatings_GameNightLeagues_LeagueId",
                        column: x => x.LeagueId,
                        principalTable: "GameNightLeagues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameNightPlayerRatings_GameNightSeasons_SeasonId",
                        column: x => x.SeasonId,
                        principalTable: "GameNightSeasons",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameNightTables_LeagueNeedsPublic",
                table: "GameNightTables",
                sql: "\"Visibility\" = 0 OR NOT \"InLeague\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameNightEventExtensions_LeagueNeedsPublic",
                table: "GameNightEventExtensions",
                sql: "\"Visibility\" = 0 OR NOT \"InLeague\"");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightLeagues_GameId",
                table: "GameNightLeagues",
                column: "GameId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightLeagues_GuildId",
                table: "GameNightLeagues",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_GuestId",
                table: "GameNightPlayerRatings",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_LeagueId_GuestId",
                table: "GameNightPlayerRatings",
                columns: new[] { "LeagueId", "GuestId" },
                unique: true,
                filter: "\"SeasonId\" IS NULL AND \"GuestId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_LeagueId_SeasonId_GuestId",
                table: "GameNightPlayerRatings",
                columns: new[] { "LeagueId", "SeasonId", "GuestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_LeagueId_SeasonId_UserId",
                table: "GameNightPlayerRatings",
                columns: new[] { "LeagueId", "SeasonId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_LeagueId_UserId",
                table: "GameNightPlayerRatings",
                columns: new[] { "LeagueId", "UserId" },
                unique: true,
                filter: "\"SeasonId\" IS NULL AND \"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_SeasonId",
                table: "GameNightPlayerRatings",
                column: "SeasonId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightPlayerRatings_UserId",
                table: "GameNightPlayerRatings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightRatingEvents_GuestId",
                table: "GameNightRatingEvents",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightRatingEvents_LeagueId_SeasonId_UserId_PlayedAt",
                table: "GameNightRatingEvents",
                columns: new[] { "LeagueId", "SeasonId", "UserId", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightRatingEvents_ResultId",
                table: "GameNightRatingEvents",
                column: "ResultId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightSeasons_LeagueId_StartsAt",
                table: "GameNightSeasons",
                columns: new[] { "LeagueId", "StartsAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameNightPlayerRatings");

            migrationBuilder.DropTable(
                name: "GameNightRatingEvents");

            migrationBuilder.DropTable(
                name: "GameNightSeasons");

            migrationBuilder.DropTable(
                name: "GameNightLeagues");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameNightTables_LeagueNeedsPublic",
                table: "GameNightTables");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameNightEventExtensions_LeagueNeedsPublic",
                table: "GameNightEventExtensions");

            migrationBuilder.DropColumn(
                name: "InLeague",
                table: "GameNightTables");

            migrationBuilder.DropColumn(
                name: "InLeague",
                table: "GameNightEventExtensions");
        }
    }
}
