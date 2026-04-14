using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreUserIdInsteadOfUsername : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatorUsername",
                table: "VoteSessions");

            migrationBuilder.AddColumn<long>(
                name: "CreatorId",
                table: "VoteSessions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_VoteSessions_CreatorId",
                table: "VoteSessions",
                column: "CreatorId");

            migrationBuilder.AddForeignKey(
                name: "FK_VoteSessions_Users_CreatorId",
                table: "VoteSessions",
                column: "CreatorId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_VoteSessions_Users_CreatorId",
                table: "VoteSessions");

            migrationBuilder.DropIndex(
                name: "IX_VoteSessions_CreatorId",
                table: "VoteSessions");

            migrationBuilder.DropColumn(
                name: "CreatorId",
                table: "VoteSessions");

            migrationBuilder.AddColumn<string>(
                name: "CreatorUsername",
                table: "VoteSessions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }
    }
}
