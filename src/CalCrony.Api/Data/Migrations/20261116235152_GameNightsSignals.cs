using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 6 (fork): GameNightSignals, one row per member per game per night holding their
    /// answer (can teach, can play, new to it, not tonight). Cascades with the event; the game side is restricted
    /// because games are retired, never deleted. Additive, so rolling the IMAGE back needs no Down; Down drops the table.</summary>
    /// <inheritdoc />
    public partial class GameNightsSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameNightSignals",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightSignals", x => new { x.EventId, x.UserId, x.GameId });
                    table.ForeignKey(
                        name: "FK_GameNightSignals_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameNightSignals_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightSignals_EventId_GameId",
                table: "GameNightSignals",
                columns: new[] { "EventId", "GameId" });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightSignals_GameId",
                table: "GameNightSignals",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightSignals_UserId",
                table: "GameNightSignals",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameNightSignals");
        }
    }
}
