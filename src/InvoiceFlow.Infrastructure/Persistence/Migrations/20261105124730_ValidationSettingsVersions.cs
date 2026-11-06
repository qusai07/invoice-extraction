using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ValidationSettingsVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ValidationSettingsVersions",
                columns: table => new
                {
                    Version = table.Column<int>(type: "int", nullable: false),
                    SettingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationSettingsVersions", x => x.Version);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ValidationSettingsVersions");
        }
    }
}
