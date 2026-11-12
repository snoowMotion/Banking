using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTenable.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedChargeAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AccountId",
                table: "Categories",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Categories_AccountId",
                table: "Categories",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_Accounts_AccountId",
                table: "Categories",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Categories_Accounts_AccountId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_AccountId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Categories");
        }
    }
}
