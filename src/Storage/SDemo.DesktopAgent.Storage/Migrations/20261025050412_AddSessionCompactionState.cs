using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.DesktopAgent.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionCompactionState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CompactionFinishedAt",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompactionReason",
                table: "sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompactionStartedAt",
                table: "sessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompactionFinishedAt",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "CompactionReason",
                table: "sessions");

            migrationBuilder.DropColumn(
                name: "CompactionStartedAt",
                table: "sessions");
        }
    }
}
