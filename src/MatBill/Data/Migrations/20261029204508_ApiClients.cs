using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MatBill.Data.Migrations
{
    /// <inheritdoc />
    public partial class ApiClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ApiClientId",
                table: "Invoice",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiNote",
                table: "Invoice",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApiReference",
                table: "Invoice",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ApiClientId",
                table: "Customer",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalRef",
                table: "Customer",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ApiClient",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    CanCreate = table.Column<bool>(type: "boolean", nullable: false),
                    CanRead = table.Column<bool>(type: "boolean", nullable: false),
                    CanIssue = table.Column<bool>(type: "boolean", nullable: false),
                    NumberPattern = table.Column<string>(type: "text", nullable: true),
                    RevokedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUsedIp = table.Column<string>(type: "text", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreateUserId = table.Column<long>(type: "bigint", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateUserId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiClient", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ApiClientId_ApiReference",
                table: "Invoice",
                columns: new[] { "ApiClientId", "ApiReference" },
                unique: true,
                filter: "\"ApiReference\" IS NOT NULL AND \"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_Customer_ApiClientId_ExternalRef",
                table: "Customer",
                columns: new[] { "ApiClientId", "ExternalRef" },
                unique: true,
                filter: "\"ExternalRef\" IS NOT NULL AND \"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_ApiClient_KeyHash",
                table: "ApiClient",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiClient_KeyPrefix",
                table: "ApiClient",
                column: "KeyPrefix");

            migrationBuilder.AddForeignKey(
                name: "FK_Customer_ApiClient_ApiClientId",
                table: "Customer",
                column: "ApiClientId",
                principalTable: "ApiClient",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoice_ApiClient_ApiClientId",
                table: "Invoice",
                column: "ApiClientId",
                principalTable: "ApiClient",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customer_ApiClient_ApiClientId",
                table: "Customer");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoice_ApiClient_ApiClientId",
                table: "Invoice");

            migrationBuilder.DropTable(
                name: "ApiClient");

            migrationBuilder.DropIndex(
                name: "IX_Invoice_ApiClientId_ApiReference",
                table: "Invoice");

            migrationBuilder.DropIndex(
                name: "IX_Customer_ApiClientId_ExternalRef",
                table: "Customer");

            migrationBuilder.DropColumn(
                name: "ApiClientId",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ApiNote",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ApiReference",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ApiClientId",
                table: "Customer");

            migrationBuilder.DropColumn(
                name: "ExternalRef",
                table: "Customer");
        }
    }
}
