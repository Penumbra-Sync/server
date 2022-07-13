using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class ClientPairKey : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientPairs_Users_OtherUserUID",
                table: "ClientPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientPairs_Users_UserUID",
                table: "ClientPairs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPairs",
                table: "ClientPairs");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "ClientPairs");

            migrationBuilder.AlterColumn<string>(
                name: "UserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "OtherUserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPairs",
                table: "ClientPairs",
                columns: new[] { "UserUID", "OtherUserUID" });

            migrationBuilder.AddForeignKey(
                name: "FK_ClientPairs_Users_OtherUserUID",
                table: "ClientPairs",
                column: "OtherUserUID",
                principalTable: "Users",
                principalColumn: "UID",
                onDelete: ReferentialAction.NoAction);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientPairs_Users_UserUID",
                table: "ClientPairs",
                column: "UserUID",
                principalTable: "Users",
                principalColumn: "UID",
                onDelete: ReferentialAction.NoAction);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientPairs_Users_OtherUserUID",
                table: "ClientPairs");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientPairs_Users_UserUID",
                table: "ClientPairs");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ClientPairs",
                table: "ClientPairs");

            migrationBuilder.AlterColumn<string>(
                name: "OtherUserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AlterColumn<string>(
                name: "UserUID",
                table: "ClientPairs",
                type: "nvarchar(10)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "ClientPairs",
                type: "int",
                nullable: false,
                defaultValue: 0)
                .Annotation("SqlServer:Identity", "1, 1");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ClientPairs",
                table: "ClientPairs",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientPairs_Users_OtherUserUID",
                table: "ClientPairs",
                column: "OtherUserUID",
                principalTable: "Users",
                principalColumn: "UID");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientPairs_Users_UserUID",
                table: "ClientPairs",
                column: "UserUID",
                principalTable: "Users",
                principalColumn: "UID");
        }
    }
}
