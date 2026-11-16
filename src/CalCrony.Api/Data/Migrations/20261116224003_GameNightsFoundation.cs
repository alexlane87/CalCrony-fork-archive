using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 1 (fork): three new tables and nothing else. GameNightsTierRoles maps a
    /// guild's Discord roles to tiers; GameNightEventExtensions is a one-to-one extension row per event
    /// (visibility), cascading with the event; GameNightInvites is the invite list the visibility filter
    /// reads. Additive, so rolling the IMAGE back needs no Down; Down drops the three tables.</summary>
    /// <inheritdoc />
    public partial class GameNightsFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GameNightEventExtensions",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightEventExtensions", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_GameNightEventExtensions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightInvites",
                columns: table => new
                {
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightInvites", x => new { x.Scope, x.ScopeId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "GameNightsTierRoles",
                columns: table => new
                {
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    RoleId = table.Column<long>(type: "bigint", nullable: false),
                    Tier = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightsTierRoles", x => new { x.GuildId, x.RoleId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightInvites_UserId",
                table: "GameNightInvites",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameNightEventExtensions");

            migrationBuilder.DropTable(
                name: "GameNightInvites");

            migrationBuilder.DropTable(
                name: "GameNightsTierRoles");
        }
    }
}
