using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class ChangeGidLength : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "gid",
                table: "groups",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(14)",
                oldMaxLength: 14);

            migrationBuilder.AlterColumn<string>(
                name: "group_gid",
                table: "group_pairs",
                type: "character varying(20)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(14)");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "gid",
                table: "groups",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "group_gid",
                table: "group_pairs",
                type: "character varying(14)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)");
        }
    }
}
