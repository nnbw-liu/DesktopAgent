using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.DesktopAgent.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageProviderModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApprovalCount",
                table: "messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderId",
                table: "messages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalCount",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "ProviderId",
                table: "messages");
        }
    }
}
