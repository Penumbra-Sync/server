using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class InitialCreate : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CharacterData",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    JobId = table.Column<int>(type: "int", nullable: false),
                    CharacterCache = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterData", x => new { x.UserId, x.JobId });
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UID = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SecretKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CharacterIdentification = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Timestamp = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UID);
                });

            migrationBuilder.CreateTable(
                name: "ClientPairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserUID = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    OtherUserUID = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    IsPaused = table.Column<bool>(type: "bit", nullable: false),
                    Timestamp = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientPairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientPairs_Users_OtherUserUID",
                        column: x => x.OtherUserUID,
                        principalTable: "Users",
                        principalColumn: "UID");
                    table.ForeignKey(
                        name: "FK_ClientPairs_Users_UserUID",
                        column: x => x.UserUID,
                        principalTable: "Users",
                        principalColumn: "UID");
                });

            migrationBuilder.CreateTable(
                name: "FileCaches",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UploaderUID = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Uploaded = table.Column<bool>(type: "bit", nullable: false),
                    LastAccessTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Timestamp = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileCaches", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_FileCaches_Users_UploaderUID",
                        column: x => x.UploaderUID,
                        principalTable: "Users",
                        principalColumn: "UID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientPairs_OtherUserUID",
                table: "ClientPairs",
                column: "OtherUserUID");

            migrationBuilder.CreateIndex(
                name: "IX_ClientPairs_UserUID",
                table: "ClientPairs",
                column: "UserUID");

            migrationBuilder.CreateIndex(
                name: "IX_FileCaches_UploaderUID",
                table: "FileCaches",
                column: "UploaderUID");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterData");

            migrationBuilder.DropTable(
                name: "ClientPairs");

            migrationBuilder.DropTable(
                name: "FileCaches");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
