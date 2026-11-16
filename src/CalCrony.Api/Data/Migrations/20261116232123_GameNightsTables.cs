using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 4 (fork): tables. Adds GameNightTables and GameNightTableSeats (a seat holds a
    /// member or, from result reporting on, a guest; a check constraint makes it exactly one) and one nullable
    /// column on GameNightEventExtensions for the night's default table size. Additive, so rolling the IMAGE back
    /// needs no Down; Down drops the tables and the column.</summary>
    /// <inheritdoc />
    public partial class GameNightsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultTableSize",
                table: "GameNightEventExtensions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameNightTables",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    HostUserId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ThreadId = table.Column<long>(type: "bigint", nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightTables", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightTables_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameNightTables_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameNightTableSeats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeatOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightTableSeats", x => x.Id);
                    table.CheckConstraint("CK_GameNightTableSeats_OneOccupant", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GameNightTableSeats_GameNightTables_TableId",
                        column: x => x.TableId,
                        principalTable: "GameNightTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTables_EventId_Number",
                table: "GameNightTables",
                columns: new[] { "EventId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTables_GameId",
                table: "GameNightTables",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTableSeats_TableId_GuestId",
                table: "GameNightTableSeats",
                columns: new[] { "TableId", "GuestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTableSeats_TableId_UserId",
                table: "GameNightTableSeats",
                columns: new[] { "TableId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTableSeats_UserId",
                table: "GameNightTableSeats",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameNightTableSeats");

            migrationBuilder.DropTable(
                name: "GameNightTables");

            migrationBuilder.DropColumn(
                name: "DefaultTableSize",
                table: "GameNightEventExtensions");
        }
    }
}
