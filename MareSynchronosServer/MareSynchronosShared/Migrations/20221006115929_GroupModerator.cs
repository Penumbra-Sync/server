using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    public partial class GroupModerator : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_moderator",
                table: "group_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_moderator",
                table: "group_pairs");
        }
    }
}
