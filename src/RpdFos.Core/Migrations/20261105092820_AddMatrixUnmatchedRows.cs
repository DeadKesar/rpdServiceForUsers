using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMatrixUnmatchedRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "matrix_unmatched_rows",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    curriculum_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sheet = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    semesters = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    competence_codes = table.Column<List<string>>(type: "text[]", nullable: false),
                    candidates = table.Column<List<string>>(type: "text[]", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    discipline_id = table.Column<Guid>(type: "uuid", nullable: true),
                    added_competence_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    resolved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_matrix_unmatched_rows", x => x.id);
                    table.CheckConstraint("ck_matrix_unmatched_rows_status", "status IN ('pending', 'linked', 'ignored')");
                    table.ForeignKey(
                        name: "fk_matrix_unmatched_rows_curriculum_plans_curriculum_plan_id",
                        column: x => x.curriculum_plan_id,
                        principalTable: "curriculum_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_matrix_unmatched_rows_disciplines_discipline_id",
                        column: x => x.discipline_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_matrix_unmatched_rows_users_resolved_by",
                        column: x => x.resolved_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_matrix_unmatched_rows_curriculum_plan_id",
                table: "matrix_unmatched_rows",
                column: "curriculum_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_matrix_unmatched_rows_discipline_id",
                table: "matrix_unmatched_rows",
                column: "discipline_id");

            migrationBuilder.CreateIndex(
                name: "ix_matrix_unmatched_rows_resolved_by",
                table: "matrix_unmatched_rows",
                column: "resolved_by");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "matrix_unmatched_rows");
        }
    }
}
