using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 7 (fork): result reporting. Adds GameNightResults, GameNightResultPlayers (a member
    /// by Discord id, or a guest; a check constraint makes it exactly one), GameNightResultValues,
    /// GameNightResultConfirmations, GameNightGuests (the typed First L name is the only personal detail) and
    /// GameNightMemberNames (the derived First L only, never a nickname), and puts the foreign key on table seats'
    /// GuestId. No photo or link column exists. Additive, so rolling the IMAGE back needs no Down; Down drops them.</summary>
    /// <inheritdoc />
    public partial class GameNightsResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameNightGuests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Erased = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightGuests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameNightMemberNames",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Left = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightMemberNames", x => new { x.GuildId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "GameNightResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReporterUserId = table.Column<long>(type: "bigint", nullable: false),
                    SubmittedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightResults_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameNightResults_GameNightTables_TableId",
                        column: x => x.TableId,
                        principalTable: "GameNightTables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GameNightResultConfirmations",
                columns: table => new
                {
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<Instant>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightResultConfirmations", x => new { x.ResultId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GameNightResultConfirmations_GameNightResults_ResultId",
                        column: x => x.ResultId,
                        principalTable: "GameNightResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightResultPlayers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: true),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: true),
                    Placement = table.Column<int>(type: "integer", nullable: true),
                    Score = table.Column<long>(type: "bigint", nullable: true),
                    ValuesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightResultPlayers", x => x.Id);
                    table.CheckConstraint("CK_GameNightResultPlayers_OnePlayer", "(\"UserId\" IS NULL) <> (\"GuestId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GameNightResultPlayers_GameNightGuests_GuestId",
                        column: x => x.GuestId,
                        principalTable: "GameNightGuests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GameNightResultPlayers_GameNightResults_ResultId",
                        column: x => x.ResultId,
                        principalTable: "GameNightResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightResultValues",
                columns: table => new
                {
                    ResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightResultValues", x => new { x.ResultId, x.Key });
                    table.ForeignKey(
                        name: "FK_GameNightResultValues_GameNightResults_ResultId",
                        column: x => x.ResultId,
                        principalTable: "GameNightResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightTableSeats_GuestId",
                table: "GameNightTableSeats",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightGuests_GuildId",
                table: "GameNightGuests",
                column: "GuildId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResultPlayers_GuestId",
                table: "GameNightResultPlayers",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResultPlayers_ResultId_GuestId",
                table: "GameNightResultPlayers",
                columns: new[] { "ResultId", "GuestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResultPlayers_ResultId_UserId",
                table: "GameNightResultPlayers",
                columns: new[] { "ResultId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResultPlayers_UserId",
                table: "GameNightResultPlayers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResults_GameId",
                table: "GameNightResults",
                column: "GameId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightResults_TableId",
                table: "GameNightResults",
                column: "TableId");

            migrationBuilder.AddForeignKey(
                name: "FK_GameNightTableSeats_GameNightGuests_GuestId",
                table: "GameNightTableSeats",
                column: "GuestId",
                principalTable: "GameNightGuests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GameNightTableSeats_GameNightGuests_GuestId",
                table: "GameNightTableSeats");

            migrationBuilder.DropTable(
                name: "GameNightMemberNames");

            migrationBuilder.DropTable(
                name: "GameNightResultConfirmations");

            migrationBuilder.DropTable(
                name: "GameNightResultPlayers");

            migrationBuilder.DropTable(
                name: "GameNightResultValues");

            migrationBuilder.DropTable(
                name: "GameNightGuests");

            migrationBuilder.DropTable(
                name: "GameNightResults");

            migrationBuilder.DropIndex(
                name: "IX_GameNightTableSeats_GuestId",
                table: "GameNightTableSeats");
        }
    }
}
