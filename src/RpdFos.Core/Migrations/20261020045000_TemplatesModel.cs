using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <inheritdoc />
    public partial class TemplatesModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_signatory_role",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_signatory_scope",
                table: "users");

            // Единый набор кодов категорий подписантов — как в листах согласования шаблонов.
            migrationBuilder.Sql("UPDATE users SET signatory_role = 'vice_rector_edu' WHERE signatory_role = 'vice_rector';");
            migrationBuilder.Sql("UPDATE users SET signatory_role = 'it_directorate_head' WHERE signatory_role = 'it_dept_head';");

            // Год шаблона этапа 0 заменён редакцией: это разные величины, а не переименование.
            migrationBuilder.DropColumn(
                name: "year",
                table: "document_templates");

            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "document_templates",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "document_templates",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "document_templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "document_templates",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<Guid>(
                name: "created_by",
                table: "document_templates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "curriculum_plan_id",
                table: "document_templates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "document_templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lineage_id",
                table: "document_templates",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "print_mapping",
                table: "document_templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "print_template_file_id",
                table: "document_templates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                table: "document_templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "schema",
                table: "document_templates",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "signatures_config",
                table: "document_templates",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Значения по умолчанию нужны только существующим строкам: новые получают снимок из реестра типов.
            migrationBuilder.Sql("""
                ALTER TABLE document_templates
                    ALTER COLUMN revision DROP DEFAULT,
                    ALTER COLUMN lineage_id DROP DEFAULT,
                    ALTER COLUMN schema DROP DEFAULT,
                    ALTER COLUMN signatures_config DROP DEFAULT;
                """);

            // Шаблоны этапов 0–1 — тестовые («Создать тестовый шаблон»): без учебного плана и снимка структуры
            // их не опубликовать. Каждый начинает свою линию редакций и удаляется мягко — запись остаётся.
            migrationBuilder.Sql("""
                UPDATE document_templates
                SET lineage_id = id, status = 'deleted', is_deleted = true, deleted_at = now();
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_signatory_role",
                table: "users",
                sql: "signatory_role IS NULL OR signatory_role IN ('department_head', 'department_head_acting', 'institute_director', 'rector', 'vice_rector_edu', 'umu_head', 'library_director', 'it_directorate_head', 'umk_chair')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_signatory_scope",
                table: "users",
                sql: "(signatory_role IN ('department_head', 'department_head_acting') AND department_id IS NOT NULL) OR (signatory_role = 'institute_director' AND institute_id IS NOT NULL AND department_id IS NULL) OR (signatory_role IN ('rector', 'vice_rector_edu', 'umu_head', 'library_director', 'it_directorate_head', 'umk_chair') AND department_id IS NULL AND institute_id IS NULL) OR (signatory_role IS NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_created_by",
                table: "document_templates",
                column: "created_by");

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_curriculum_plan_id",
                table: "document_templates",
                column: "curriculum_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_lineage_id",
                table: "document_templates",
                column: "lineage_id",
                unique: true,
                filter: "status = 'published'");

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_lineage_id_revision",
                table: "document_templates",
                columns: new[] { "lineage_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_print_template_file_id",
                table: "document_templates",
                column: "print_template_file_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_templates_type_status",
                table: "document_templates",
                columns: new[] { "type", "status" });

            migrationBuilder.AddForeignKey(
                name: "fk_document_templates_curriculum_plans_curriculum_plan_id",
                table: "document_templates",
                column: "curriculum_plan_id",
                principalTable: "curriculum_plans",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_document_templates_employees_created_by",
                table: "document_templates",
                column: "created_by",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_document_templates_uploaded_files_print_template_file_id",
                table: "document_templates",
                column: "print_template_file_id",
                principalTable: "uploaded_files",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_document_templates_curriculum_plans_curriculum_plan_id",
                table: "document_templates");

            migrationBuilder.DropForeignKey(
                name: "fk_document_templates_employees_created_by",
                table: "document_templates");

            migrationBuilder.DropForeignKey(
                name: "fk_document_templates_uploaded_files_print_template_file_id",
                table: "document_templates");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_signatory_role",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_signatory_scope",
                table: "users");

            // Прежние коды; председателя УМК в прежнем наборе нет.
            migrationBuilder.Sql("UPDATE users SET signatory_role = 'vice_rector' WHERE signatory_role = 'vice_rector_edu';");
            migrationBuilder.Sql("UPDATE users SET signatory_role = 'it_dept_head' WHERE signatory_role = 'it_directorate_head';");
            migrationBuilder.Sql("UPDATE users SET signatory_role = NULL WHERE signatory_role = 'umk_chair';");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_created_by",
                table: "document_templates");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_curriculum_plan_id",
                table: "document_templates");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_lineage_id",
                table: "document_templates");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_lineage_id_revision",
                table: "document_templates");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_print_template_file_id",
                table: "document_templates");

            migrationBuilder.DropIndex(
                name: "ix_document_templates_type_status",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "created_by",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "curriculum_plan_id",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "lineage_id",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "print_mapping",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "print_template_file_id",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "schema",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "signatures_config",
                table: "document_templates");

            migrationBuilder.DropColumn(
                name: "revision",
                table: "document_templates");

            migrationBuilder.AddColumn<int>(
                name: "year",
                table: "document_templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "type",
                table: "document_templates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "status",
                table: "document_templates",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "document_templates",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_signatory_role",
                table: "users",
                sql: "signatory_role IS NULL OR signatory_role IN ('department_head', 'department_head_acting', 'institute_director', 'vice_rector', 'rector', 'umu_head', 'library_director', 'it_dept_head')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_signatory_scope",
                table: "users",
                sql: "(signatory_role IN ('department_head', 'department_head_acting') AND department_id IS NOT NULL) OR (signatory_role = 'institute_director' AND institute_id IS NOT NULL AND department_id IS NULL) OR (signatory_role IN ('rector', 'vice_rector', 'umu_head', 'library_director', 'it_dept_head') AND department_id IS NULL AND institute_id IS NULL) OR (signatory_role IS NULL)");
        }
    }
}
