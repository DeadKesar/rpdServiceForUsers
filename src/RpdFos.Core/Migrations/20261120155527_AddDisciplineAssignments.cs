using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDisciplineAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "discipline_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discipline_assignments", x => x.id);
                    table.ForeignKey(
                        name: "fk_discipline_assignments_disciplines_discipline_id",
                        column: x => x.discipline_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_discipline_assignments_employees_assigned_by",
                        column: x => x.assigned_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_discipline_assignments_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_discipline_assignments_assigned_by",
                table: "discipline_assignments",
                column: "assigned_by");

            migrationBuilder.CreateIndex(
                name: "ix_discipline_assignments_discipline_id_employee_id",
                table: "discipline_assignments",
                columns: new[] { "discipline_id", "employee_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discipline_assignments_employee_id",
                table: "discipline_assignments",
                column: "employee_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "discipline_assignments");
        }
    }
}
