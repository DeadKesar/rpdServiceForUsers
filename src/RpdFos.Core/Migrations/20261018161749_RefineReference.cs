using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class RefineReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_official_designation",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_signatory_role_department_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_signatory_role_institute_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_competences_fgos_id_code",
                table: "competences");

            migrationBuilder.AddColumn<Guid>(
                name: "profile_id",
                table: "competences",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_official_designation",
                table: "users",
                column: "official_designation",
                unique: true,
                filter: "official_designation IS NOT NULL AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_users_signatory_role_department_id",
                table: "users",
                columns: new[] { "signatory_role", "department_id" },
                unique: true,
                filter: "signatory_role IN ('department_head', 'department_head_acting') AND department_id IS NOT NULL AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_users_signatory_role_institute_id",
                table: "users",
                columns: new[] { "signatory_role", "institute_id" },
                unique: true,
                filter: "signatory_role = 'institute_director' AND is_active");

            migrationBuilder.CreateIndex(
                name: "ix_competences_fgos_id_code",
                table: "competences",
                columns: new[] { "fgos_id", "code" },
                unique: true,
                filter: "profile_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_competences_profile_id_code",
                table: "competences",
                columns: new[] { "profile_id", "code" },
                unique: true,
                filter: "profile_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_competences_study_profiles_profile_id",
                table: "competences",
                column: "profile_id",
                principalTable: "study_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_competences_study_profiles_profile_id",
                table: "competences");

            migrationBuilder.DropIndex(
                name: "ix_users_official_designation",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_signatory_role_department_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_users_signatory_role_institute_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_competences_fgos_id_code",
                table: "competences");

            migrationBuilder.DropIndex(
                name: "ix_competences_profile_id_code",
                table: "competences");

            migrationBuilder.DropColumn(
                name: "profile_id",
                table: "competences");

            migrationBuilder.CreateIndex(
                name: "ix_users_official_designation",
                table: "users",
                column: "official_designation",
                unique: true,
                filter: "official_designation IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_signatory_role_department_id",
                table: "users",
                columns: new[] { "signatory_role", "department_id" },
                unique: true,
                filter: "signatory_role IN ('department_head', 'department_head_acting') AND department_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_signatory_role_institute_id",
                table: "users",
                columns: new[] { "signatory_role", "institute_id" },
                unique: true,
                filter: "signatory_role = 'institute_director'");

            migrationBuilder.CreateIndex(
                name: "ix_competences_fgos_id_code",
                table: "competences",
                columns: new[] { "fgos_id", "code" },
                unique: true);
        }
    }
}
