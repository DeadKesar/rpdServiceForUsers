using System.Text.Json;
using RpdFos.Web.Forms.Widgets;

namespace RpdFos.Web.Forms;

/// <summary>
/// Реестр виджетов по <c>x-ui-widget</c> схемы. Особое поле получает виджет по имени, остальное рисует универсальный
/// обработчик (<see cref="SchemaField"/>) по типу. Новый виджет — компонент от <see cref="FieldBase"/> и строка здесь;
/// обработчик не меняется. Так новый тип документа не требует нового кода интерфейса, пока ему хватает виджетов.
/// </summary>
public static class WidgetRegistry
{
    private static readonly Dictionary<string, Type> Widgets = new()
    {
        ["richtext"] = typeof(RichTextWidget),
        ["list"] = typeof(StringListWidget),
        ["hours_table"] = typeof(HoursTableWidget),
        ["topic_plan_table"] = typeof(TopicPlanWidget),
        ["competence_picker"] = typeof(CompetencePickerWidget),
        ["developer_picker"] = typeof(DeveloperPickerWidget),
        ["class_table"] = typeof(ObjectTableWidget),
        ["literature_list"] = typeof(ObjectTableWidget)
    };

    /// <summary>Виджет поля: по имени из схемы, а для списков без имени — список строк или таблица записей.</summary>
    public static Type? Find(JsonElement schema, SchemaContext context)
    {
        if (SchemaContext.Text(schema, "x-ui-widget") is { } name && Widgets.TryGetValue(name, out var widget)) return widget;
        if (SchemaContext.Type(schema) != "array") return null;
        var items = context.Resolve(schema.TryGetProperty("items", out var i) ? i : default);
        return SchemaContext.Type(items) == "object" ? typeof(ObjectTableWidget) : typeof(StringListWidget);
    }
}
