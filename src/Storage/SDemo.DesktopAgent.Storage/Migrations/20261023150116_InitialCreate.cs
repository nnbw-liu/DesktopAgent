using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SDemo.DesktopAgent.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Meta = table.Column<string>(type: "TEXT", nullable: true),
                    WorkspaceId = table.Column<string>(type: "TEXT", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OriginMountId = table.Column<string>(type: "TEXT", nullable: true),
                    ActiveMountId = table.Column<string>(type: "TEXT", nullable: true),
                    LastPluginId = table.Column<string>(type: "TEXT", nullable: true),
                    PluginMemoryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryThroughOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    SummaryUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SummaryTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    LastVerificationJson = table.Column<string>(type: "TEXT", nullable: true),
                    TodosJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    Reasoning = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolCallsJson = table.Column<string>(type: "TEXT", nullable: true),
                    PluginJson = table.Column<string>(type: "TEXT", nullable: true),
                    DerivedJson = table.Column<string>(type: "TEXT", nullable: true),
                    ToolTraceJson = table.Column<string>(type: "TEXT", nullable: true),
                    MemoryCitationsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_messages_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_SessionId",
                table: "messages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_UpdatedAt",
                table: "sessions",
                column: "UpdatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "sessions");
        }
    }
}
