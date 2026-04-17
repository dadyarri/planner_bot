using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignOrderFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OrderIndex",
                table: "Campaigns",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CampaignOrderDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    OrderedCampaignIds = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignOrderDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CampaignOrderStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    CurrentCampaignId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignOrderStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CampaignOrderStates_Campaigns_CurrentCampaignId",
                        column: x => x.CurrentCampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignOrderDrafts_UserId_ChatId",
                table: "CampaignOrderDrafts",
                columns: new[] { "UserId", "ChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignOrderStates_ChatId",
                table: "CampaignOrderStates",
                column: "ChatId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CampaignOrderStates_CurrentCampaignId",
                table: "CampaignOrderStates",
                column: "CurrentCampaignId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignOrderDrafts");

            migrationBuilder.DropTable(
                name: "CampaignOrderStates");

            migrationBuilder.DropColumn(
                name: "OrderIndex",
                table: "Campaigns");
        }
    }
}
