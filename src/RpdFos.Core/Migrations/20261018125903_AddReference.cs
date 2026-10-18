using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "institutes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    short_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_institutes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    institute_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    short_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_departments", x => x.id);
                    table.ForeignKey(
                        name: "fk_departments_institutes_institute_id",
                        column: x => x.institute_id,
                        principalTable: "institutes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "study_directions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    education_level = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    responsible_department_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_study_directions", x => x.id);
                    table.ForeignKey(
                        name: "fk_study_directions_departments_responsible_department_id",
                        column: x => x.responsible_department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    full_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    short_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    name_for_document = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    official_designation = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    position = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    academic_degree = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    signatory_role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    institute_id = table.Column<Guid>(type: "uuid", nullable: true),
                    can_login = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    identity_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_can_login", "can_login = false OR (email IS NOT NULL AND identity_subject IS NOT NULL)");
                    table.CheckConstraint("ck_users_signatory_role", "signatory_role IS NULL OR signatory_role IN ('department_head', 'department_head_acting', 'institute_director', 'vice_rector', 'rector', 'umu_head', 'library_director', 'it_dept_head')");
                    table.CheckConstraint("ck_users_signatory_scope", "(signatory_role IN ('department_head', 'department_head_acting') AND department_id IS NOT NULL) OR (signatory_role = 'institute_director' AND institute_id IS NOT NULL AND department_id IS NULL) OR (signatory_role IN ('rector', 'vice_rector', 'umu_head', 'library_director', 'it_dept_head') AND department_id IS NULL AND institute_id IS NULL) OR (signatory_role IS NULL)");
                    table.ForeignKey(
                        name: "fk_users_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_institutes_institute_id",
                        column: x => x.institute_id,
                        principalTable: "institutes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "fgos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    order_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    approved_on = table.Column<DateOnly>(type: "date", nullable: false),
                    document_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_fgos", x => x.id);
                    table.ForeignKey(
                        name: "fk_fgos_study_directions_direction_id",
                        column: x => x.direction_id,
                        principalTable: "study_directions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "study_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_study_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_study_profiles_study_directions_direction_id",
                        column: x => x.direction_id,
                        principalTable: "study_directions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "uploaded_files",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by = table.Column<Guid>(type: "uuid", nullable: false),
                    file_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    original_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    storage_path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    content = table.Column<byte[]>(type: "bytea", nullable: true),
                    mime_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    uploaded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_uploaded_files", x => x.id);
                    table.ForeignKey(
                        name: "fk_uploaded_files_users_uploaded_by",
                        column: x => x.uploaded_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "competences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    fgos_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    category_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    formulation = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competences", x => x.id);
                    table.ForeignKey(
                        name: "fk_competences_fgos_fgos_id",
                        column: x => x.fgos_id,
                        principalTable: "fgos",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "curriculum_plans",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    study_form = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    source_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_curriculum_plans", x => x.id);
                    table.ForeignKey(
                        name: "fk_curriculum_plans_study_directions_direction_id",
                        column: x => x.direction_id,
                        principalTable: "study_directions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_curriculum_plans_study_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "study_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_curriculum_plans_uploaded_files_source_file_id",
                        column: x => x.source_file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "competence_indicators",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    indicator_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competence_indicators", x => x.id);
                    table.ForeignKey(
                        name: "fk_competence_indicators_competences_competence_id",
                        column: x => x.competence_id,
                        principalTable: "competences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "disciplines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    curriculum_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    block = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    part_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    entry_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    total_hours = table.Column<int>(type: "integer", nullable: false),
                    total_credits = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_disciplines", x => x.id);
                    table.ForeignKey(
                        name: "fk_disciplines_curriculum_plans_curriculum_plan_id",
                        column: x => x.curriculum_plan_id,
                        principalTable: "curriculum_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_disciplines_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_disciplines_disciplines_parent_id",
                        column: x => x.parent_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "parser_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_by = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comp_list_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    comp_matrix_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    employees_file_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    extracted_disciplines_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    extracted_competences_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    extracted_employees_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    errors_log = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    created_curriculum_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_parser_runs", x => x.id);
                    table.ForeignKey(
                        name: "fk_parser_runs_curriculum_plans_created_curriculum_plan_id",
                        column: x => x.created_curriculum_plan_id,
                        principalTable: "curriculum_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parser_runs_uploaded_files_comp_list_file_id",
                        column: x => x.comp_list_file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parser_runs_uploaded_files_comp_matrix_file_id",
                        column: x => x.comp_matrix_file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parser_runs_uploaded_files_employees_file_id",
                        column: x => x.employees_file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parser_runs_uploaded_files_plan_file_id",
                        column: x => x.plan_file_id,
                        principalTable: "uploaded_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_parser_runs_users_started_by",
                        column: x => x.started_by,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "competence_matrix",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_competence_matrix", x => x.id);
                    table.ForeignKey(
                        name: "fk_competence_matrix_competences_competence_id",
                        column: x => x.competence_id,
                        principalTable: "competences",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_competence_matrix_disciplines_discipline_id",
                        column: x => x.discipline_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "discipline_semester_details",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    discipline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    semester = table.Column<int>(type: "integer", nullable: false),
                    contact_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lectures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    practice = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lab_hours = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    self_study = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    control = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    credits = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    control_types = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_discipline_semester_details", x => x.id);
                    table.CheckConstraint("ck_discipline_semester_details_control_types", "control_types <@ ARRAY['exam', 'credit', 'graded_credit', 'course_project', 'course_work']::text[]");
                    table.CheckConstraint("ck_discipline_semester_details_hours", "lectures >= 0 AND practice >= 0 AND lab_hours >= 0 AND self_study >= 0 AND control >= 0 AND credits >= 0");
                    table.ForeignKey(
                        name: "fk_discipline_semester_details_disciplines_discipline_id",
                        column: x => x.discipline_id,
                        principalTable: "disciplines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_competence_indicators_competence_id_code",
                table: "competence_indicators",
                columns: new[] { "competence_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_competence_matrix_competence_id",
                table: "competence_matrix",
                column: "competence_id");

            migrationBuilder.CreateIndex(
                name: "ix_competence_matrix_discipline_id_competence_id",
                table: "competence_matrix",
                columns: new[] { "discipline_id", "competence_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_competences_category",
                table: "competences",
                column: "category");

            migrationBuilder.CreateIndex(
                name: "ix_competences_fgos_id_code",
                table: "competences",
                columns: new[] { "fgos_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_plans_direction_id_profile_id_year",
                table: "curriculum_plans",
                columns: new[] { "direction_id", "profile_id", "year" });

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_plans_profile_id",
                table: "curriculum_plans",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "ix_curriculum_plans_source_file_id",
                table: "curriculum_plans",
                column: "source_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_code",
                table: "departments",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_institute_id",
                table: "departments",
                column: "institute_id");

            migrationBuilder.CreateIndex(
                name: "ix_departments_name",
                table: "departments",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_departments_short_name",
                table: "departments",
                column: "short_name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_discipline_semester_details_discipline_id_semester",
                table: "discipline_semester_details",
                columns: new[] { "discipline_id", "semester" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disciplines_block_part_type",
                table: "disciplines",
                columns: new[] { "block", "part_type" });

            migrationBuilder.CreateIndex(
                name: "ix_disciplines_curriculum_plan_id_code",
                table: "disciplines",
                columns: new[] { "curriculum_plan_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_disciplines_department_id",
                table: "disciplines",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_disciplines_name",
                table: "disciplines",
                column: "name")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:TsVectorConfig", "russian");

            migrationBuilder.CreateIndex(
                name: "ix_disciplines_parent_id",
                table: "disciplines",
                column: "parent_id");

            migrationBuilder.CreateIndex(
                name: "ix_fgos_direction_id_is_current",
                table: "fgos",
                columns: new[] { "direction_id", "is_current" });

            migrationBuilder.CreateIndex(
                name: "ix_fgos_direction_id_order_number_approved_on",
                table: "fgos",
                columns: new[] { "direction_id", "order_number", "approved_on" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "ix_institutes_code",
                table: "institutes",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_institutes_name",
                table: "institutes",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_comp_list_file_id",
                table: "parser_runs",
                column: "comp_list_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_comp_matrix_file_id",
                table: "parser_runs",
                column: "comp_matrix_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_created_curriculum_plan_id",
                table: "parser_runs",
                column: "created_curriculum_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_employees_file_id",
                table: "parser_runs",
                column: "employees_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_plan_file_id",
                table: "parser_runs",
                column: "plan_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_parser_runs_started_by",
                table: "parser_runs",
                column: "started_by");

            migrationBuilder.CreateIndex(
                name: "ix_study_directions_code",
                table: "study_directions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_study_directions_responsible_department_id",
                table: "study_directions",
                column: "responsible_department_id");

            migrationBuilder.CreateIndex(
                name: "ix_study_profiles_direction_id_name",
                table: "study_profiles",
                columns: new[] { "direction_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_uploaded_files_checksum",
                table: "uploaded_files",
                column: "checksum");

            migrationBuilder.CreateIndex(
                name: "ix_uploaded_files_uploaded_by",
                table: "uploaded_files",
                column: "uploaded_by");

            migrationBuilder.CreateIndex(
                name: "ix_users_can_login_is_active",
                table: "users",
                columns: new[] { "can_login", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_users_department_id",
                table: "users",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true,
                filter: "email IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_users_identity_subject",
                table: "users",
                column: "identity_subject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_institute_id",
                table: "users",
                column: "institute_id");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "competence_indicators");

            migrationBuilder.DropTable(
                name: "competence_matrix");

            migrationBuilder.DropTable(
                name: "discipline_semester_details");

            migrationBuilder.DropTable(
                name: "parser_runs");

            migrationBuilder.DropTable(
                name: "competences");

            migrationBuilder.DropTable(
                name: "disciplines");

            migrationBuilder.DropTable(
                name: "fgos");

            migrationBuilder.DropTable(
                name: "curriculum_plans");

            migrationBuilder.DropTable(
                name: "study_profiles");

            migrationBuilder.DropTable(
                name: "uploaded_files");

            migrationBuilder.DropTable(
                name: "study_directions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "institutes");
        }
    }
}
