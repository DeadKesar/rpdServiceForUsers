namespace DisciplineDataExtractor;

/// <summary>Исходный файл: имя — для привязки ошибок, содержимое — как хранится в <c>uploaded_files.content</c>.</summary>
public sealed record SourceFile(string FileName, byte[] Content)
{
    public static SourceFile FromPath(string path) => new(Path.GetFileName(path), File.ReadAllBytes(path));
}

/// <summary>Файлы одного импорта. Файл должностей необязателен (<c>parser_runs.employees_file_id NULL</c>).</summary>
public sealed record ExtractionInput(
    SourceFile Plan,
    SourceFile CompetenceList,
    SourceFile CompetenceMatrix,
    SourceFile? Employees = null);
