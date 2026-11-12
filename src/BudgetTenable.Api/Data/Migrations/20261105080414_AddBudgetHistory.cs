using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTenable.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveFrom",
                table: "Categories",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CategoryTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CategoryId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    From = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Percentage = table.Column<decimal>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CategoryTerms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CategoryTerms_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CategoryTerms_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncomeTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    From = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Net = table.Column<decimal>(type: "TEXT", nullable: false),
                    Partner = table.Column<decimal>(type: "TEXT", nullable: false),
                    Caf = table.Column<decimal>(type: "TEXT", nullable: false),
                    AnnualBonus = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncomeTerms", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CategoryTerms_AccountId",
                table: "CategoryTerms",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CategoryTerms_CategoryId_From",
                table: "CategoryTerms",
                columns: new[] { "CategoryId", "From" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncomeTerms_From",
                table: "IncomeTerms",
                column: "From",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CategoryTerms");

            migrationBuilder.DropTable(
                name: "IncomeTerms");

            migrationBuilder.DropColumn(
                name: "ActiveFrom",
                table: "Categories");
        }
    }
}
