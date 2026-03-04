using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSavingVotingSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoteSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    ThreadId = table.Column<int>(type: "integer", nullable: false),
                    GameDateTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VoteCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteSessions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoteSessions");
        }
    }
}
