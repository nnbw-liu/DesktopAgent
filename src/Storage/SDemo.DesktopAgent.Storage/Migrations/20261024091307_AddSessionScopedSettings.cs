using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.DesktopAgent.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionScopedSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScopedSettingsJson",
                table: "sessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScopedSettingsJson",
                table: "sessions");
        }
    }
}
