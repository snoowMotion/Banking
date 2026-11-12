using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BudgetTenable.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBankSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankAuthStates",
                columns: table => new
                {
                    State = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AspspName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AspspCountry = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RequiredPsuHeaders = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacesConnectionId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAuthStates", x => x.State);
                });

            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    AspspName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    AspspCountry = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    RequiredPsuHeaders = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BankAccountLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderUid = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    IdentificationHash = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    IbanMasked = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    SyncedThrough = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccountLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankAccountLinks_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BankAccountLinks_BankConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccountLinks_AccountId",
                table: "BankAccountLinks",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankAccountLinks_ConnectionId",
                table: "BankAccountLinks",
                column: "ConnectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankAccountLinks");

            migrationBuilder.DropTable(
                name: "BankAuthStates");

            migrationBuilder.DropTable(
                name: "BankConnections");
        }
    }
}
