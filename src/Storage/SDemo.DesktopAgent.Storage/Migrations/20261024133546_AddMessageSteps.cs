using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.DesktopAgent.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StepsJson",
                table: "messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StepsJson",
                table: "messages");
        }
    }
}
