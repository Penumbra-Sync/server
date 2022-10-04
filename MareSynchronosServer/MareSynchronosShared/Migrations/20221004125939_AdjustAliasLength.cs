using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class AdjustAliasLength : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "alias",
                table: "users",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10,
                oldNullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "alias",
                table: "users",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(15)",
                oldMaxLength: 15,
                oldNullable: true);
        }
    }
}
