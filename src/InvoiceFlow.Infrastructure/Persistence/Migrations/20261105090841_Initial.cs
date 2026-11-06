using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Corrections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Field = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Corrections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SubmittedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SupplierId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SupplierName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvoiceNumberKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    InvoiceDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PoNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ResolvedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ErpReference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ErrorCodes = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    InvoiceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtractionJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PipelineJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Step = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NotBefore = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_DocumentId_Id",
                table: "AuditEvents",
                columns: new[] { "DocumentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Corrections_DocumentId",
                table: "Corrections",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Sha256",
                table: "Documents",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Status_StatusChangedAt",
                table: "Documents",
                columns: new[] { "Status", "StatusChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SubmittedBy",
                table: "Documents",
                column: "SubmittedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_SupplierId_InvoiceNumberKey",
                table: "Documents",
                columns: new[] { "SupplierId", "InvoiceNumberKey" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_DocumentId",
                table: "PipelineJobs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineJobs_Status_NotBefore",
                table: "PipelineJobs",
                columns: new[] { "Status", "NotBefore" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "Corrections");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "PipelineJobs");
        }
    }
}
