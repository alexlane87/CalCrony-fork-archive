using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CalCrony.Api.Data.Migrations
{
    /// <summary>Game Nights step 8 (fork): the Discord report flow. Two columns: GameNightGames.PhotoRequired (a per-game
    /// setting: must the report say the board photo has been posted; no photo or link is ever stored) and
    /// GameNightResults.SummaryMessageId (the Discord message the bot posted for the result, so it can be updated when it
    /// settles). Additive and defaulted, so rolling the IMAGE back needs no Down; Down drops the two columns.</summary>
    /// <inheritdoc />
    public partial class GameNightsReportFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SummaryMessageId",
                table: "GameNightResults",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PhotoRequired",
                table: "GameNightGames",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SummaryMessageId",
                table: "GameNightResults");

            migrationBuilder.DropColumn(
                name: "PhotoRequired",
                table: "GameNightGames");
        }
    }
}
