using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVotingAgainstAndOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "VoteSessionVotes",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AgainstCount",
                table: "VoteSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "VoteSessions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "VoteSessionVotes");

            migrationBuilder.DropColumn(
                name: "AgainstCount",
                table: "VoteSessions");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "VoteSessions");
        }
    }
}
