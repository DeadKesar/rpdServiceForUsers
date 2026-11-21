namespace RpdFos.Web.Forms.Widgets;

/// <summary>Ответ диалога «Таблица»: строк всего (с заголовком), столбцов и есть ли строка заголовка.</summary>
public sealed record TableSize(int Rows, int Columns, bool HeaderRow);
