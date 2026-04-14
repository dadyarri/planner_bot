using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignIdToSavedGameAndVoteSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Delete existing data — campaigns are being set up from scratch (Phase 4 strategy).
            // VoteSessionVotes must be deleted first due to FK constraint on VoteSessions.
            migrationBuilder.Sql("DELETE FROM \"VoteSessionVotes\"");
            migrationBuilder.Sql("DELETE FROM \"VoteSessions\"");
            migrationBuilder.Sql("DELETE FROM \"SavedGame\"");

            migrationBuilder.AddColumn<int>(
                name: "CampaignId",
                table: "VoteSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CampaignId",
                table: "SavedGame",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_VoteSessions_CampaignId",
                table: "VoteSessions",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedGame_CampaignId",
                table: "SavedGame",
                column: "CampaignId");

            migrationBuilder.AddForeignKey(
                name: "FK_SavedGame_Campaigns_CampaignId",
                table: "SavedGame",
                column: "CampaignId",
                principalTable: "Campaigns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_VoteSessions_Campaigns_CampaignId",
                table: "VoteSessions",
                column: "CampaignId",
                principalTable: "Campaigns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SavedGame_Campaigns_CampaignId",
                table: "SavedGame");

            migrationBuilder.DropForeignKey(
                name: "FK_VoteSessions_Campaigns_CampaignId",
                table: "VoteSessions");

            migrationBuilder.DropIndex(
                name: "IX_VoteSessions_CampaignId",
                table: "VoteSessions");

            migrationBuilder.DropIndex(
                name: "IX_SavedGame_CampaignId",
                table: "SavedGame");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                table: "VoteSessions");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                table: "SavedGame");
        }
    }
}
