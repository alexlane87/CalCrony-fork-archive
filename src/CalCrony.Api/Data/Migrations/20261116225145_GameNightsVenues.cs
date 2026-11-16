using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameNightsVenues : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LimitOverride",
                table: "GameNightEventExtensions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VenueId",
                table: "GameNightEventExtensions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GameNightVenues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DefaultLimit = table.Column<int>(type: "integer", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightVenues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GameNightVenueAreas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    ExtraCapacity = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightVenueAreas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameNightVenueAreas_GameNightVenues_VenueId",
                        column: x => x.VenueId,
                        principalTable: "GameNightVenues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameNightEventAreas",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AreaId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameNightEventAreas", x => new { x.EventId, x.AreaId });
                    table.ForeignKey(
                        name: "FK_GameNightEventAreas_GameNightEventExtensions_EventId",
                        column: x => x.EventId,
                        principalTable: "GameNightEventExtensions",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GameNightEventAreas_GameNightVenueAreas_AreaId",
                        column: x => x.AreaId,
                        principalTable: "GameNightVenueAreas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameNightEventExtensions_VenueId",
                table: "GameNightEventExtensions",
                column: "VenueId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightEventAreas_AreaId",
                table: "GameNightEventAreas",
                column: "AreaId");

            migrationBuilder.CreateIndex(
                name: "IX_GameNightVenueAreas_VenueId_Name",
                table: "GameNightVenueAreas",
                columns: new[] { "VenueId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameNightVenues_GuildId",
                table: "GameNightVenues",
                column: "GuildId");

            migrationBuilder.AddForeignKey(
                name: "FK_GameNightEventExtensions_GameNightVenues_VenueId",
                table: "GameNightEventExtensions",
                column: "VenueId",
                principalTable: "GameNightVenues",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GameNightEventExtensions_GameNightVenues_VenueId",
                table: "GameNightEventExtensions");

            migrationBuilder.DropTable(
                name: "GameNightEventAreas");

            migrationBuilder.DropTable(
                name: "GameNightVenueAreas");

            migrationBuilder.DropTable(
                name: "GameNightVenues");

            migrationBuilder.DropIndex(
                name: "IX_GameNightEventExtensions_VenueId",
                table: "GameNightEventExtensions");

            migrationBuilder.DropColumn(
                name: "LimitOverride",
                table: "GameNightEventExtensions");

            migrationBuilder.DropColumn(
                name: "VenueId",
                table: "GameNightEventExtensions");
        }
    }
}
