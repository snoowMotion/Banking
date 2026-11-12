using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTenable.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankCategoryMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankCategoryMappings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CategoryId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankCategoryMappings", x => x.Key);
                    table.ForeignKey(
                        name: "FK_BankCategoryMappings_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankCategoryMappings_CategoryId",
                table: "BankCategoryMappings",
                column: "CategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankCategoryMappings");
        }
    }
}
