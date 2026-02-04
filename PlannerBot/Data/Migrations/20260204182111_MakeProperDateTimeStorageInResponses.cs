using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlannerBot.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeProperDateTimeStorageInResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            
            migrationBuilder.AddColumn<DateTime>(
                name: "DateTime",
                table: "Responses",
                type: "timestamp with time zone",
                nullable: true);
            
            migrationBuilder.Sql("""
                                 UPDATE "Responses"
                                 SET "DateTime" = ("Date" + "Time") - INTERVAL '3 hour'
                                 """);
            
            migrationBuilder.DropColumn(
                name: "Date",
                table: "Responses");

            migrationBuilder.DropColumn(
                name: "Time",
                table: "Responses");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DateTime",
                table: "Responses");

            migrationBuilder.AddColumn<DateOnly>(
                name: "Date",
                table: "Responses",
                type: "date",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<TimeOnly>(
                name: "Time",
                table: "Responses",
                type: "time without time zone",
                nullable: true);
        }
    }
}
