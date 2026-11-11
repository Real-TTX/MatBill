using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatBill.Data.Migrations
{
    /// <inheritdoc />
    public partial class LegacyImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportNote",
                table: "Invoice",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportRef",
                table: "Invoice",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportSource",
                table: "Invoice",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportRef",
                table: "Expense",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportSource",
                table: "Expense",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoice_ImportSource_ImportRef",
                table: "Invoice",
                columns: new[] { "ImportSource", "ImportRef" },
                unique: true,
                filter: "\"ImportRef\" IS NOT NULL AND \"UpdateState\" <> 0");

            migrationBuilder.CreateIndex(
                name: "IX_Expense_ImportSource_ImportRef",
                table: "Expense",
                columns: new[] { "ImportSource", "ImportRef" },
                unique: true,
                filter: "\"ImportRef\" IS NOT NULL AND \"UpdateState\" <> 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoice_ImportSource_ImportRef",
                table: "Invoice");

            migrationBuilder.DropIndex(
                name: "IX_Expense_ImportSource_ImportRef",
                table: "Expense");

            migrationBuilder.DropColumn(
                name: "ImportNote",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ImportRef",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ImportSource",
                table: "Invoice");

            migrationBuilder.DropColumn(
                name: "ImportRef",
                table: "Expense");

            migrationBuilder.DropColumn(
                name: "ImportSource",
                table: "Expense");
        }
    }
}
