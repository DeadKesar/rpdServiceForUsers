namespace RpdFos.Core.DocumentTypes;

/// <summary>
/// Источники автозаполнения — данные учебного плана, справочников и самого документа. На них ссылаются
/// <c>x-source</c> и <c>x-visible-if</c> в схемах и цели вида <c>source</c> в сопоставлении закладок.
/// Имена — по каталогу закладок DOCX (docs/design); значение берётся из таблиц этапа 2.
/// </summary>
public static class DocumentSources
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["parser.discipline.name"] = "Наименование дисциплины (disciplines.name)",
        ["parser.discipline.ind"] = "Индекс дисциплины по плану (disciplines.code)",
        ["parser.discipline.part_type"] = "Часть плана: обязательная, формируемая участниками… (disciplines.part_type)",
        ["parser.discipline.semesters"] = "Семестры изучения (discipline_semester_details.semester)",
        ["parser.discipline.course"] = "Курс — производное из семестров изучения",
        ["parser.discipline.ze_total"] = "Общая трудоёмкость, з.е. (disciplines.total_credits)",
        ["parser.discipline.hours_total"] = "Академических часов по плану (disciplines.total_hours)",
        ["parser.discipline.details"] = "Часы по видам занятий по семестрам (discipline_semester_details)",
        ["parser.discipline.attestation"] = "Виды промежуточной аттестации (discipline_semester_details.control_types)",
        ["reference.direction.code"] = "Код направления подготовки (study_directions.code)",
        ["reference.direction.name"] = "Наименование направления (study_directions.name)",
        ["reference.direction.level"] = "Уровень образования (study_directions.education_level)",
        // Явной квалификации в разобранном плане пока нет — производная от уровня, словарём на этапе 5.
        ["reference.direction.qualification"] =
            "Квалификация: явная из плана, иначе по уровню (бакалавриат → бакалавр, магистратура → магистр, специалитет → специалист)",
        ["reference.profile.name"] = "Направленность (профиль) (study_profiles.name)",
        ["reference.curriculum_plan.education_form"] = "Форма обучения (curriculum_plans.study_form)",
        ["reference.department.name"] = "Кафедра дисциплины (departments.name)",
        ["db.competence_matrix.by_discipline"] = "Компетенции и индикаторы дисциплины (competence_matrix, competences, competence_indicators)",
        // Подписантов в содержимом нет: их определяют правила листа согласования шаблона (SignatureResolvers).
        ["db.user.current"] = "Текущий пользователь — первый составитель (users: id, name_for_document, position)",
        ["linked_rpd.competences.indicators"] = "Индикаторы из связанной РПД (для ФОС)",
        ["document.year"] = "Год документа"
    };

    public static bool IsKnown(string path) => All.ContainsKey(path);
}

/// <summary>
/// Каталог правил валидации (<c>x-validation-rule</c>), раздел 2.x.5 и правки по сверке.
/// Сами правила реализуются на этапе 5; здесь — только имена.
/// </summary>
public static class ValidationRules
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "hours_match_curriculum", "topic_hours_match_total", "all_indicators_covered",
        "linked_rpd_must_be_approved_or_under_review", "all_indicators_have_means", "scale_matches_control_type",
        "unique_competence_codes", "literature_main_count", "fos_passport_consistent", "control_forms_match"
    };
}

/// <summary>Именованные построители таблиц DOCX (цели вида <c>table</c>). Реализация — этап 5.</summary>
public static class TableBuilders
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["competences"] = "Компетенции и индикаторы дисциплины",
        ["laboriousness"] = "Трудоёмкость по видам работы и семестрам",
        ["discipline_partition"] = "Разделы дисциплины (тематический план)",
        ["practical_classes"] = "Практические занятия",
        ["lab_works"] = "Лабораторные работы",
        ["fos_passport"] = "Паспорт ФОС: компетенции, индикаторы, разделы и задания"
    };
}

/// <summary>
/// Правила определения подписантов (резолверы) в листе согласования шаблона. Только имена —
/// выполнение на этапе 6.
/// </summary>
public static class SignatureResolvers
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["discipline.department.head"] = "Заведующий кафедрой дисциплины",
        ["discipline.department.institute.director"] = "Директор института кафедры дисциплины",
        ["lookup.role:vice_rector_edu"] = "Проректор по образовательной деятельности",
        ["lookup.role:umu_head"] = "Начальник учебно-методического управления",
        ["lookup.role:library_director"] = "Директор научно-технической библиотеки",
        ["lookup.role:it_directorate_head"] = "Начальник УИТП",
        ["lookup.role:umk_chair"] = "Председатель УМК по направлению документа"
    };
}
