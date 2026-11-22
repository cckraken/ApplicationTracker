using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApplicationTracker.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    OriginalFileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    StoredFileName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApplicationDocuments",
                columns: table => new
                {
                    ApplicationsId = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentsId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationDocuments", x => new { x.ApplicationsId, x.DocumentsId });
                    table.ForeignKey(
                        name: "FK_ApplicationDocuments_Applications_ApplicationsId",
                        column: x => x.ApplicationsId,
                        principalTable: "Applications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApplicationDocuments_Documents_DocumentsId",
                        column: x => x.DocumentsId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationDocuments_DocumentsId",
                table: "ApplicationDocuments",
                column: "DocumentsId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_StoredFileName",
                table: "Documents",
                column: "StoredFileName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApplicationDocuments");

            migrationBuilder.DropTable(
                name: "Documents");
        }
    }
}
