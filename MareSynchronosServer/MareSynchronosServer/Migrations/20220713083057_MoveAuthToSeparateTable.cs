using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class MoveAuthToSeparateTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Auth",
                columns: table => new
                {
                    HashedKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserUID = table.Column<string>(type: "nvarchar(10)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auth", x => x.HashedKey);
                    table.ForeignKey(
                        name: "FK_Auth_Users_UserUID",
                        column: x => x.UserUID,
                        principalTable: "Users",
                        principalColumn: "UID");
                });

            migrationBuilder.Sql("INSERT INTO Auth SELECT SecretKey, UID FROM Users");

            migrationBuilder.DropColumn(
                name: "SecretKey",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Auth_UserUID",
                table: "Auth",
                column: "UserUID");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Auth");

            migrationBuilder.AddColumn<string>(
                name: "SecretKey",
                table: "Users",
                type: "nvarchar(max)",
                nullable: true);

        }
    }
}
