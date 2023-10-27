using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class PausedIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_user_permission_sets_user_uid_other_user_uid_is_paused",
                table: "user_permission_sets",
                columns: new[] { "user_uid", "other_user_uid", "is_paused" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_permission_sets_user_uid_other_user_uid_is_paused",
                table: "user_permission_sets");
        }
    }
}
