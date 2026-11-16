using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 5 (fork): two nullable/defaulted columns on GameNightGames, RoleState and
    /// RoleNote, recording whether a game's follow role is being made or the bot failed to make or grant it.
    /// Additive, so rolling the IMAGE back needs no Down; Down drops the two columns.</summary>
    /// <inheritdoc />
    public partial class GameNightsGameRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoleNote",
                table: "GameNightGames",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoleState",
                table: "GameNightGames",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoleNote",
                table: "GameNightGames");

            migrationBuilder.DropColumn(
                name: "RoleState",
                table: "GameNightGames");
        }
    }
}
