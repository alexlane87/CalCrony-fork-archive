using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 3 (fork): the game library. Four new tables and nothing else:
    /// GameNightGames (one row per game per server), GameNightGameFields (its result questions),
    /// GameNightGameTypes (roles an admin marked as game types) and GameNightGameTypeLinks (which types a
    /// game belongs to). Additive, so rolling the IMAGE back needs no Down; Down drops the four tables.</summary>
    /// <inheritdoc />
    public partial class GameNightsGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameNightGames",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ResultType = table.Column<int>(type: "integer", nullable: false),
                    MinSize = table.Column<int>(type: "integer", nullable: false),
                    MaxSize = table.Column<int>(type: "integer", nullable: false),
                    DefaultSize = table.Column<int>(type: "integer", nullable: false),
                    RatingEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    RatingK = table.Column<int>(type: "integer", nullable: false),
                    StartRating = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    SeedVersion = table.Column<int>(type: "integer", nullable: true),
                    LocallyEdited = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightGames", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameNightGameTypes",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightGameTypes", x => new { x.GuildId, x.RoleId });
                });

            migrationBuilder.CreateTable(
                name: "GameNightGameFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Unique = table.Column<bool>(type: "boolean", nullable: false),
                    Values = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightGameFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightGameFields_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightGameTypeLinks",
                columns: table => new
                {
                    GameId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeRoleId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightGameTypeLinks", x => new { x.GameId, x.TypeRoleId });
                    table.ForeignKey(
                        name: "FK_GameNightGameTypeLinks_GameNightGames_GameId",
                        column: x => x.GameId,
                        principalTable: "GameNightGames",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightGameFields_GameId_Key",
                table: "GameNightGameFields",
                columns: new[] { "GameId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightGames_GuildId_Key",
                table: "GameNightGames",
                columns: new[] { "GuildId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameNightGameFields");

            migrationBuilder.DropTable(
                name: "GameNightGameTypeLinks");

            migrationBuilder.DropTable(
                name: "GameNightGameTypes");

            migrationBuilder.DropTable(
                name: "GameNightGames");
        }
    }
}
