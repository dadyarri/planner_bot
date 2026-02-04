using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeProperDateTimeStorageInSavedGames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DateTime",
                table: "SavedGame",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
            
            migrationBuilder.Sql("""
                                 UPDATE "SavedGame"
                                 SET "DateTime" = ("Date" + "Time") - INTERVAL '3 hour'
                                 """);
            
            migrationBuilder.DropColumn(
                name: "Date",
                table: "SavedGame");

            migrationBuilder.DropColumn(
                name: "Time",
                table: "SavedGame");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateTime",
                table: "SavedGame");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Date",
                table: "SavedGame",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Time",
                table: "SavedGame",
                type: "time without time zone",
                nullable: false,
                defaultValue: new TimeOnly(0, 0, 0));
        }
    }
}
