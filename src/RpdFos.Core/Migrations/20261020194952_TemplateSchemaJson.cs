using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RpdFos.Core.Migrations
{
    /// <summary>
    /// Снимок схемы шаблона — json вместо jsonb: jsonb хранит ключи в своём порядке, а редактор документа строит
    /// разделы и поля по порядку свойств схемы. У существующих строк порядок уже потерян — их шаблоны создаются заново.
    /// </summary>
    public partial class TemplateSchemaJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE document_templates ALTER COLUMN schema TYPE json USING schema::json;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE document_templates ALTER COLUMN schema TYPE jsonb USING schema::jsonb;");
        }
    }
}
