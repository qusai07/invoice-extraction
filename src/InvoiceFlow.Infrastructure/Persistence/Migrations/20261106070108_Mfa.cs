using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Mfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MfaEnabledAt",
                table: "Users",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MfaLastTimeStep",
                table: "Users",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaPendingSecret",
                table: "Users",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MfaSecret",
                table: "Users",
                type: "varchar(256)",
                unicode: false,
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecoveryCodes_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryCodes_UserId_CodeHash",
                table: "RecoveryCodes",
                columns: new[] { "UserId", "CodeHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecoveryCodes");

            migrationBuilder.DropColumn(
                name: "MfaEnabledAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MfaLastTimeStep",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MfaPendingSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MfaSecret",
                table: "Users");
        }
    }
}
