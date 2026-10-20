# Каталог закладок DOCX-шаблонов оформления (РПД и ФОС)

*Материал для приложения и главы 4 (задача Т-301). Извлечён из действующих шаблонов оформления МГТУ «СТАНКИН» (`DWP_TemplateBookmarks.docx`, `FOS_TemplateBookmarks.docx`). Закладки именуются по конвенции `Autofill<Поле><N>`, где N — порядковый номер вставки: одно и то же значение подставляется во все одноимённые закладки (fan-out). Каталог задаёт маппинг «закладка → поле схемы → источник данных» и является входом для генератора DOCX.*

## Шаблон РПД (52 закладки)

### Реквизиты дисциплины и программы

| Закладки (кратность) | Значение | Источник (x-source) |
|---|---|---|
| `AutofillDiscipline1–7` (7) | Наименование дисциплины | `parser.discipline.name` |
| `AutofillWayCode1–4` (4) | Код направления подготовки | `reference.direction.code` |
| `AutofillWayName1–4` (4) | Наименование направления | `reference.direction.name` |
| `AutofillWaySection1–2` (2) | Направленность (профиль) | `reference.profile.name` |
| `AutofillEducationLevel1–2` (2) | Уровень образования | `reference.direction.level` |
| `AutofillEducationForm1–2` (2) | Форма обучения | `reference.curriculum_plan.education_form` |
| `AutofillPartType1–2` (2) | Цикл дисциплины и его часть | `parser.discipline.block` + `part_type` |
| `AutofillDepartment1–2` (2) | Кафедра | `reference.department.name` |
| `AutofillYear1` (1) | Год | `document.year` |
| `AutofillKurs1` (1) | Фраза о курсовом проекте (работе) в разделе 3.5 «Курсовой проект (курсовая работа)»: «По дисциплине … AutofillKurs»; без курсового проекта по плану — стандартная заглушка раздела (FR-TPL-14). *Исправлено по шаблонам, этап 3: было «Курс, производное из семестров».* | `payload`: `course_work` (условный раздел) |
| `AutofillSemester1` (1) | Семестр(ы) | `parser.discipline.semesters` |

### Трудоёмкость и содержательные блоки

| Закладки | Значение | Источник |
|---|---|---|
| `AutofillZeAtAll1` | Общая трудоёмкость, ЗЕТ | `parser.discipline.ze_total` |
| `AutofillByPlan1` | Академических часов по плану (в той же фразе, что и ЗЕТ) | `parser.discipline.hours_total` |
| `AutofillCompetencies1` | Перечень компетенций (текстовый блок) | `competence_matrix` по дисциплине |
| `AutofillCompetenciesTable1` | Таблица компетенций и индикаторов | `competence_matrix` + `competence_indicators` |
| `AutofillLaboriousnessTable1` | Таблица трудоёмкости по видам работы и семестрам | `discipline_semester_details` |
| `AutofillDisciplinePartitionTable1` | Таблица разделов (тематический план) | `payload`: тематический план |
| `AutofillLaboratiesClassTable1` | Таблица лабораторных работ | `payload`: лабораторные (условный раздел) |
| `AutofillPracticleClassTable1` | Таблица практических занятий | `payload`: практические (условный раздел) |
| `AutofillRequirementsLast1` | Входные требования (пререквизиты) — текстовый блок | `payload` (ручной ввод / полуавтоматическая подстановка) |
| `AutofillRequirementsNext1` | Последующие дисциплины (постреквизиты) — текстовый блок | `payload` (ручной ввод / полуавтоматическая подстановка) |
| `AutofillPredWay1` | Председатель учебно-методической комиссии по направлению подготовки — ФИО | резолвер `lookup.role:umk_chair` (через направление документа) |

### Подписанты (пары «должность + ФИО», источник — резолверы и справочник должностей)

| Закладки | Подписант | Резолвер / источник |
|---|---|---|
| `AutofillPositionOdimp1` / `AutofillPositionOdimpName1` | Проректор по ОДиМП (гриф «УТВЕРЖДАЮ», титул) | `lookup.role:vice_rector_edu`, placement: title |
| `AutofillPositionKaf1` / `AutofillPositionKafName1` / `AutofillPositionKafForDoc1` | Заведующий кафедрой (краткая должность / ФИО / наименование кафедры под строкой подписи) | `discipline.department.head`; `ForDoc` — сокращение кафедры заведующего («ИТиВС»), `departments.short_name`. *Исправлено по шаблонам, этап 3: было `ForDoc` ↔ `nameForDoc` парсера должностей.* |
| `AutofillPositionInst1` / `AutofillPositionInstName1` | Директор института | `discipline.department.institute.director` |
| `AutofillPositionUmu1` / `AutofillPositionUmuName1` | Начальник УМУ | `lookup.role:umu_head` |
| `AutofillPositionBib1` / `AutofillPositionBibName1` | Директор научно-технической библиотеки | `lookup.role:library_director` |
| `AutofillPositionUitp1` / `AutofillPositionUitpName1` | Начальник УИТП | `lookup.role:it_directorate_head` |

## Шаблон ФОС (46 закладок)

| Закладки (кратность) | Значение | Источник |
|---|---|---|
| `AutofillDiscipline1,2,4–8` (7) | Наименование дисциплины | `parser.discipline.name` |
| `AutofillWayCode1–4` (4) | Код направления | `reference.direction.code` |
| `AutofillWayName1,2,7` (3) | Наименование направления | `reference.direction.name` |
| `AutofillWaySection1` | Профиль | `reference.profile.name` |
| `AutofillEducationLevel1` | Квалификация («Квалификация магистр»). *Исправлено по шаблонам, этап 3: было «Уровень образования / квалификация» с источником `reference.direction.level`.* | `reference.direction.qualification`: явная из плана, иначе производная от уровня (бакалавриат → бакалавр, магистратура → магистр, специалитет → специалист) |
| `AutofillEducationLvl1` | Уровень образования в шапке экзаменационного билета («для обучающихся N курса магистратуры»), родительный падеж | `reference.direction.level` |
| `AutofillEducationForm1` | Форма обучения | `reference.curriculum_plan.education_form` |
| `AutofillCurs1` | Курс («для обучающихся N курса») | производное из семестров |
| `AutofillYear1–6` (6) | Год (в датах подписей и на титуле) | `document.year` |
| `AutofillPassDestination1` | Фраза назначения ФОС (текстовый блок) | шаблонная фраза + `discipline.name` |
| `AutofillMonitoringU1`, `AutofillMonitoring2–3` | Формы текущего контроля (текстовые блоки) | `payload`: формы контроля |
| `AutofillExaming1` | Вид промежуточной аттестации (заголовок приложения с билетами) | `discipline_semester_details.assessment_type` |
| `AutofillCompetenciesTable1` | Таблица паспорта ФОС | `payload`: паспорт + `competence_indicators` |
| `AutofillDepartment1,3–7` (6) | Наименование кафедры (титул, протокол, подписи) | `reference.department.name` |
| `AutofillProOD1` / `AutofillProODName1` | Проректор по ОДиМП | `lookup.role:vice_rector_edu` |
| `AutofillPositionKaf1` / `KafName1` / `KafForDoc1` | Заведующий кафедрой | `discipline.department.head` |
| `AutofillPositionInst1` / `InstName1` | Директор института | `discipline.department.institute.director` |
| `AutofillPositionUmu1` / `UmuName1` | Начальник УМУ | `lookup.role:umu_head` |

## Замечания и следствия для системы

1. **Конвенция fan-out.** Одно значение → несколько одноимённых закладок с индексами. Генератор подставляет значение во все закладки с совпадающим префиксом, не полагаясь на непрерывность нумерации.
2. **Несогласованность имён в действующих шаблонах:** `Kurs` (РПД) против `Curs` (ФОС), `EducationLevel` против `EducationLvl`, разрывы нумерации (`Discipline3` и `WayName3–6` отсутствуют в ФОС). Следствие: маппинг «закладка → поле» должен храниться как данные шаблона (а не выводиться из имени жёстко), а при загрузке DOCX-шаблона оформления система валидирует его: каждая закладка `Autofill*` сопоставлена полю схемы, несопоставленные — ошибка публикации шаблона (дополнение к финальной проверке FR-TPL-11).
3. **Закладки покрывают только автозаполнение.** Методическое содержимое (цели, темы, банк заданий) вставляется не точечными закладками, а генерацией целых разделов; крупные блоки (`RequirementsLast/Next`, `Monitoring*`, `PassDestination`) — плейсхолдеры для многострочного текста.
4. **Хранение шаблона оформления.** DOCX-шаблон оформления — артефакт шаблона документа: ссылка из `document_templates` на файл в `uploaded_files` (если поле ещё не предусмотрено — добавить, например `print_template_file_id`).

## Реализация-предшественник: генератор DisciplineWorkProgram

Источник шаблонов и механики заполнения — существующий генератор автора: **github.com/DeadKesar/rpd** (.NET 7, Avalonia 11, Open-XML-SDK; настольное приложение, на базе которого выделена библиотека-парсер DisciplineDataExtractor).

**Механика заполнения (переиспользуется в задаче Т-301):**
- `Ooxml.GetBookmarks(doc, "Autofill")` собирает закладки по префиксу; ключ — имя без префикса, индекс-суффикс отбрасывается при сопоставлении (fan-out одного значения во все одноимённые закладки);
- скалярные значения: в документе плейсхолдером служит сам текст имени закладки (`AutofillDiscipline`), заполнение — поиск `Text`-элемента с этим текстом внутри закладки и замена;
- таблицы (`WriteCompetenciesTable`, `WriteLaboriousnessTable`, `WriteDisciplinePartitionTable`, `WritePracticleClassTable`, `WriteLaboratiesClassTable` и др.) строятся через хелперы `Tables`;
- документ создаётся `CreateFromTemplate` и сохраняется клонированием пакета (`SaveDoc`).

**Важная находка:** в генераторе уже есть шаблоны оформления для **четырёх типов документов** — РПД (`DWP_…`), ФОС (`FOS_…`), **практика** (`PRAC_…`) и **ВКР** (`VKR_…`). Для разрабатываемой системы это готовые шаблоны оформления будущих типов: расширение на практику и ВКР потребует только описания их JSON Schema — печатные формы уже существуют. Это усиливает обоснование расширяемой модели документов.

**Ограничения предшественника (мотивация новой системы):**
- тип документа диспетчеризуется жёстко в коде (`if (dwpDir == "vkr/") …`) — в новой системе это заменяется декларативной схемой и `x-visible-if`;
- сопоставление «закладка → данные» зашито в методы, без валидации полноты закладок шаблона;
- статическое состояние (`Section.Disciplines`) исключает параллельную обработку;
- индекс закладки отбрасывается как один символ — нумерация свыше 9 одноимённых вставок не поддерживается;
- нет жизненного цикла, согласования, валидации содержимого и многопользовательской работы — генератор решает только задачу «данные плана → заполненный DOCX».
