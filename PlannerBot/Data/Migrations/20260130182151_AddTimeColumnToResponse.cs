using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTimeColumnToResponse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeOnly>(
                name: "Time",
                table: "Responses",
                type: "time without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Time",
                table: "Responses");
        }
    }
}
