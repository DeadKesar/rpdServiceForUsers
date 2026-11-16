using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUmkChairDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "direction_id",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_direction_id",
                table: "users",
                column: "direction_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_signatory_role_direction_id",
                table: "users",
                columns: new[] { "signatory_role", "direction_id" },
                unique: true,
                filter: "signatory_role = 'umk_chair' AND direction_id IS NOT NULL AND is_active");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_direction_scope",
                table: "users",
                sql: "direction_id IS NULL OR (signatory_role IS NOT NULL AND signatory_role = 'umk_chair')");

            migrationBuilder.AddForeignKey(
                name: "fk_users_study_directions_direction_id",
                table: "users",
                column: "direction_id",
                principalTable: "study_directions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_study_directions_direction_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_direction_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_signatory_role_direction_id",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_direction_scope",
                table: "users");

            migrationBuilder.DropColumn(
                name: "direction_id",
                table: "users");
        }
    }
}
