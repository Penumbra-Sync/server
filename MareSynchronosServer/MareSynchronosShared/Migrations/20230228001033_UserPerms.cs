using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class UserPerms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "disable_animations",
                table: "client_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "disable_sounds",
                table: "client_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "disable_animations",
                table: "client_pairs");

            migrationBuilder.DropColumn(
                name: "disable_sounds",
                table: "client_pairs");
        }
    }
}
