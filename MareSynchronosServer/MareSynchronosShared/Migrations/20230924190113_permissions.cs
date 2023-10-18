using MareSynchronosShared.Models;
using Microsoft.EntityFrameworkCore.Migrations;
using static System.Runtime.InteropServices.JavaScript.JSType;

#nullable disable

namespace MareSynchronosServer.Migrations
{
    /// <inheritdoc />
    public partial class permissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "group_pair_preferred_permissions",
                columns: table => new
                {
                    group_gid = table.Column<string>(type: "character varying(20)", nullable: false),
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    disable_animations = table.Column<bool>(type: "boolean", nullable: false),
                    disable_sounds = table.Column<bool>(type: "boolean", nullable: false),
                    disable_vfx = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_pair_preferred_permissions", x => new { x.user_uid, x.group_gid });
                    table.ForeignKey(
                        name: "fk_group_pair_preferred_permissions_groups_group_temp_id1",
                        column: x => x.group_gid,
                        principalTable: "groups",
                        principalColumn: "gid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_pair_preferred_permissions_users_user_temp_id7",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_default_preferred_permissions",
                columns: table => new
                {
                    user_uid = table.Column<string>(type: "text", nullable: false),
                    user_uid1 = table.Column<string>(type: "character varying(10)", nullable: true),
                    disable_individual_animations = table.Column<bool>(type: "boolean", nullable: false),
                    disable_individual_sounds = table.Column<bool>(type: "boolean", nullable: false),
                    disable_individual_vfx = table.Column<bool>(type: "boolean", nullable: false),
                    disable_group_animations = table.Column<bool>(type: "boolean", nullable: false),
                    disable_group_sounds = table.Column<bool>(type: "boolean", nullable: false),
                    disable_group_vfx = table.Column<bool>(type: "boolean", nullable: false),
                    individual_is_sticky = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_default_preferred_permissions", x => x.user_uid);
                    table.ForeignKey(
                        name: "fk_user_default_preferred_permissions_users_user_temp_id13",
                        column: x => x.user_uid1,
                        principalTable: "users",
                        principalColumn: "uid");
                });

            migrationBuilder.CreateTable(
                name: "user_permission_sets",
                columns: table => new
                {
                    user_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    other_user_uid = table.Column<string>(type: "character varying(10)", nullable: false),
                    sticky = table.Column<bool>(type: "boolean", nullable: false),
                    is_paused = table.Column<bool>(type: "boolean", nullable: false),
                    disable_animations = table.Column<bool>(type: "boolean", nullable: false),
                    disable_vfx = table.Column<bool>(type: "boolean", nullable: false),
                    disable_sounds = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_permission_sets", x => new { x.user_uid, x.other_user_uid });
                    table.ForeignKey(
                        name: "fk_user_permission_sets_users_other_user_uid",
                        column: x => x.other_user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_permission_sets_users_user_uid",
                        column: x => x.user_uid,
                        principalTable: "users",
                        principalColumn: "uid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(@"insert into user_permission_sets
                select user1.user_uid as user_uid, user1.other_user_uid as other_user_uid,
	                true,
	                user1.is_paused as is_paused,
	                user1.disable_animations as disable_animations,
	                user1.disable_vfx as disable_vfx,
	                user1.disable_sounds as disable_sounds
	                from client_pairs as user1;");

            migrationBuilder.Sql(@"insert into user_permission_sets 
                select gp.group_user_uid, gp2.group_user_uid,
		                false,
		                bool_and(gp.is_paused),
		                bool_and(g.disable_animations or gp.disable_animations),
		                bool_and(g.disable_vfx or gp.disable_vfx),
		                bool_and(g.disable_sounds or gp.disable_sounds)
		                from group_pairs gp 
		                left join group_pairs gp2 on gp2.group_gid = gp.group_gid 
		                left join groups g on g.gid = gp2.group_gid 
		                where gp2.group_user_uid <> gp.group_user_uid
                group by gp.group_user_uid, gp2.group_user_uid
                on conflict do nothing;");

            migrationBuilder.Sql(@"insert into group_pair_preferred_permissions
                        select group_gid
                        , group_user_uid
                        , gp.is_paused
                        , gp.disable_animations or g.disable_animations as disable_animations 
                        , gp.disable_sounds or g.disable_sounds as disable_sounds 
                        , gp.disable_vfx or g.disable_vfx as disable_vfx 
                        from group_pairs as gp
                left join groups g on g.gid = gp.group_gid");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_temp_id1",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id7",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id8",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "disable_animations",
                table: "group_pairs");

            migrationBuilder.DropColumn(
                name: "disable_sounds",
                table: "group_pairs");

            migrationBuilder.DropColumn(
                name: "disable_vfx",
                table: "group_pairs");

            migrationBuilder.DropColumn(
                name: "is_paused",
                table: "group_pairs");

            migrationBuilder.DropColumn(
                name: "allow_receiving_messages",
                table: "client_pairs");

            migrationBuilder.DropColumn(
                name: "disable_animations",
                table: "client_pairs");

            migrationBuilder.DropColumn(
                name: "disable_sounds",
                table: "client_pairs");

            migrationBuilder.DropColumn(
                name: "disable_vfx",
                table: "client_pairs");

            migrationBuilder.DropColumn(
                name: "is_paused",
                table: "client_pairs");

            migrationBuilder.RenameColumn(
                name: "disable_vfx",
                table: "groups",
                newName: "prefer_disable_vfx");

            migrationBuilder.RenameColumn(
                name: "disable_sounds",
                table: "groups",
                newName: "prefer_disable_sounds");

            migrationBuilder.RenameColumn(
                name: "disable_animations",
                table: "groups",
                newName: "prefer_disable_animations");

            migrationBuilder.CreateIndex(
                name: "ix_group_pair_preferred_permissions_group_gid",
                table: "group_pair_preferred_permissions",
                column: "group_gid");

            migrationBuilder.CreateIndex(
                name: "ix_group_pair_preferred_permissions_user_uid",
                table: "group_pair_preferred_permissions",
                column: "user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_default_preferred_permissions_user_uid1",
                table: "user_default_preferred_permissions",
                column: "user_uid1");

            migrationBuilder.CreateIndex(
                name: "ix_user_permission_sets_other_user_uid",
                table: "user_permission_sets",
                column: "other_user_uid");

            migrationBuilder.CreateIndex(
                name: "ix_user_permission_sets_user_uid",
                table: "user_permission_sets",
                column: "user_uid");

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_temp_id2",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id8",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id9",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");

            migrationBuilder.Sql(@"create function get_all_pairs_for_user(req_uid text)
returns table(
	user_uid varchar(10)
	,other_user_uid varchar(10)
	,alias varchar(15)
	,gid varchar(20)
	,synced bool
	,ownperm_is_paused bool
	,ownperm_sticky bool
	,ownperm_disable_animations bool
	,ownperm_disable_sounds bool
	,ownperm_disable_vfx bool
	,otherperm_is_paused bool
	,otherperm_disable_animations bool
	,otherperm_disable_sounds bool
	,otherperm_disable_vfx bool)
as
$$
begin
return query(
WITH query1 AS (
    SELECT user1.user_uid AS user_uid
        ,user1.other_user_uid AS other_user_uid
        ,NULL AS gid
        ,NOT (user2 IS NULL) AS synced
    FROM client_pairs AS user1
    LEFT JOIN client_pairs user2 ON user1.user_uid = user2.other_user_uid
        AND user2.user_uid = user1.other_user_uid
    WHERE user1.user_uid = req_uid
),
query2 AS (
    SELECT gp.group_user_uid
        ,gp2.group_user_uid
        ,gp.group_gid
        ,true
    FROM group_pairs gp
    LEFT JOIN group_pairs gp2 ON gp2.group_gid = gp.group_gid
    WHERE gp.group_user_uid = req_uid
        AND gp2.group_user_uid <> req_uid
        AND gp2.group_gid = gp.group_gid
)

SELECT pairs.user_uid
    ,pairs.other_user_uid
    ,u.alias
    ,cast(pairs.gid as varchar(20))
    ,pairs.synced
    ,ownperm.is_paused
    ,ownperm.sticky
    ,ownperm.disable_animations 
    ,ownperm.disable_sounds 
    ,ownperm.disable_vfx 
    ,otherperm.is_paused
    ,otherperm.disable_animations 
    ,otherperm.disable_sounds
    ,otherperm.disable_vfx 
FROM (SELECT * FROM query1
    union all
    SELECT * FROM query2) AS pairs
LEFT JOIN users AS u ON pairs.other_user_uid = u.uid
LEFT JOIN user_permission_sets AS ownperm ON pairs.user_uid = ownperm.user_uid
    AND pairs.other_user_uid = ownperm.other_user_uid
LEFT JOIN user_permission_sets AS otherperm ON pairs.user_uid = otherperm.other_user_uid
    AND pairs.other_user_uid = otherperm.user_uid
WHERE pairs.user_uid = req_uid
    AND u.uid = pairs.other_user_uid
    AND (
        (ownperm.user_uid = req_uid)
        OR (otherperm.other_user_uid = req_uid)
        )
);
end;
$$
language plpgsql;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_groups_group_temp_id2",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id8",
                table: "group_pairs");

            migrationBuilder.DropForeignKey(
                name: "fk_groups_users_owner_temp_id9",
                table: "groups");

            migrationBuilder.DropTable(
                name: "group_pair_preferred_permissions");

            migrationBuilder.DropTable(
                name: "user_default_preferred_permissions");

            migrationBuilder.DropTable(
                name: "user_permission_sets");

            migrationBuilder.RenameColumn(
                name: "prefer_disable_vfx",
                table: "groups",
                newName: "disable_vfx");

            migrationBuilder.RenameColumn(
                name: "prefer_disable_sounds",
                table: "groups",
                newName: "disable_sounds");

            migrationBuilder.RenameColumn(
                name: "prefer_disable_animations",
                table: "groups",
                newName: "disable_animations");

            migrationBuilder.AddColumn<bool>(
                name: "disable_animations",
                table: "group_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "disable_sounds",
                table: "group_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "disable_vfx",
                table: "group_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_paused",
                table: "group_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_receiving_messages",
                table: "client_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

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

            migrationBuilder.AddColumn<bool>(
                name: "disable_vfx",
                table: "client_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "is_paused",
                table: "client_pairs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_groups_group_temp_id1",
                table: "group_pairs",
                column: "group_gid",
                principalTable: "groups",
                principalColumn: "gid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_group_pairs_users_group_user_temp_id7",
                table: "group_pairs",
                column: "group_user_uid",
                principalTable: "users",
                principalColumn: "uid",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_groups_users_owner_temp_id8",
                table: "groups",
                column: "owner_uid",
                principalTable: "users",
                principalColumn: "uid");
        }
    }
}
