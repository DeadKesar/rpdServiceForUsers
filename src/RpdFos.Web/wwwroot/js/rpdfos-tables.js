// Таблицы в редакторе форматированного текста (этап 7, B4, доработка по проверке автора): замена функций таблиц
// Radzen.Blazor по имени. Radzen вставляет и удаляет столбец по номеру ячейки в строке, а не по столбцу сетки, а
// строку — без учёта объединения по вертикали; при объединённых ячейках это ломает таблицу. Здесь строки и столбцы
// считаются по сетке (rpdfos-table-logic.js), остальные команды Radzen (объединение, разделение, копирование ячеек,
// удаление таблицы) остаются его собственными. Здесь же — проверка рисунков, вставленных разметкой (этап 7, B4а).
//
// Замена привязана к версии пакета: имена функций и порядок вызова сверены с Radzen.Blazor 12.1.11
// (execTableCommand зовёт this.addTableRow / deleteTableRow / addTableColumn / deleteTableColumn / insertTable и
// после команды ставит курсор в соседнюю ячейку, если текущая исчезла). При обновлении пакета сверьте заново.
(function () {
    'use strict';

    // Предупреждения показывает Blazor (MainLayout): он регистрирует себя здесь; без него — в консоль. Объект нужен
    // всегда, и когда замена ниже не состоялась: MainLayout обращается к нему при каждом запуске.
    var handler = null;
    window.rpdfosTables = {
        register: function (reference) { handler = reference; },
        unregister: function (reference) { if (handler === reference) handler = null; }
    };

    function warn(text) {
        if (handler) handler.invokeMethodAsync('Warn', text).catch(function () { });
        else console.warn(text);
    }

    var logic = window.RpdFosTableLogic;
    var needed = ['formatTableColumnHeader', 'addTableRow', 'deleteTableRow', 'addTableColumn', 'deleteTableColumn',
        'insertTable', 'getTableContext', 'createCommandResult'];
    var missing = !logic ? ['RpdFosTableLogic'] : !window.Radzen ? ['Radzen'] :
        needed.filter(function (name) { return typeof window.Radzen[name] !== 'function'; });
    if (missing.length) {
        // Свои функции без чужих не работают; редактор остаётся с таблицами Radzen как есть.
        console.error('rpdfos-tables: нет ' + missing.join(', ') + ' — версия Radzen.Blazor изменилась, ' +
            'сверьте rpdfos-tables.js с новым Radzen.Blazor.js; таблицы в редакторе работают как в Radzen.');
        return;
    }

    var Radzen = window.Radzen;

    // ---- Таблица <-> модель ----

    function span(cell, name) { return Math.max(parseInt(cell.getAttribute(name) || 1, 10) || 1, 1); }

    function setSpan(cell, name, value) {
        if (span(cell, name) === value) return;
        if (value > 1) cell.setAttribute(name, value);
        else cell.removeAttribute(name);
    }

    // Модель — только разделы таблицы и собственные ячейки строк; id — номера в массивах элементов.
    function build(table) {
        var built = { model: { sections: [] }, sections: [], rows: [], cells: [] };
        Array.prototype.forEach.call(table.children, function (element) {
            var kind = element.tagName.toLowerCase();
            if (kind !== 'thead' && kind !== 'tbody' && kind !== 'tfoot') return;
            var section = { id: built.sections.length, kind: kind, rows: [] };
            built.sections.push(element);
            Array.prototype.forEach.call(element.rows, function (tr) {
                var row = { id: built.rows.length, cells: [] };
                built.rows.push(tr);
                Array.prototype.forEach.call(tr.cells, function (td) {
                    row.cells.push({ id: built.cells.length, tag: td.tagName.toLowerCase(), colspan: span(td, 'colspan'), rowspan: span(td, 'rowspan') });
                    built.cells.push(td);
                });
                section.rows.push(row);
            });
            built.model.sections.push(section);
        });
        return built;
    }

    function createCell(tag) {
        var cell = document.createElement(tag);
        // Курсор встаёт только в непустую ячейку: пустая — с неразрывным пробелом, как её вставляет сам редактор.
        cell.innerHTML = '&nbsp;';
        return cell;
    }

    // Узел встаёт на место index среди элементов родителя; стоящий там же не трогается: перенос узла убирает курсор
    // из его текста, и следующая команда не находила бы ячейку.
    function place(parent, node, index) {
        var current = parent.children[index];
        if (current !== node) parent.insertBefore(node, current || null);
    }

    // Элементы — на места модели: лишние уходят, новые создаются, сдвинувшиеся (ячейка, перешедшая в следующую строку)
    // переставляются вместе с содержимым и атрибутами, остальные остаются на месте.
    function write(built) {
        var keptSections = {};
        var keptRows = {};
        var keptCells = {};
        built.model.sections.forEach(function (section) {
            keptSections[section.id] = true;
            section.rows.forEach(function (row) {
                if (row.id != null) keptRows[row.id] = true;
                row.cells.forEach(function (cell) { if (cell.id != null) keptCells[cell.id] = true; });
            });
        });
        built.cells.forEach(function (td, id) { if (!keptCells[id]) td.remove(); });
        built.rows.forEach(function (tr, id) { if (!keptRows[id]) tr.remove(); });
        built.sections.forEach(function (element, id) { if (!keptSections[id]) element.remove(); });

        built.model.sections.forEach(function (section) {
            var sectionElement = built.sections[section.id];
            section.rows.forEach(function (row, rowIndex) {
                var tr = row.id == null ? document.createElement('tr') : built.rows[row.id];
                place(sectionElement, tr, rowIndex);
                row.cells.forEach(function (cell, cellIndex) {
                    var td = cell.id == null ? createCell(cell.tag) : built.cells[cell.id];
                    place(tr, td, cellIndex);
                    setSpan(td, 'colspan', cell.colspan);
                    setSpan(td, 'rowspan', cell.rowspan);
                });
            });
        });
    }

    // Операция над текущей ячейкой контекста; изменённые элементы пишутся обратно, только если она удалась.
    function apply(context, operation) {
        var built = build(context.table);
        var id = built.cells.indexOf(context.cell);
        if (id < 0) return { ok: false, reason: 'no-cell' };
        var result = operation(built.model, id);
        if (result.ok) write(built);
        return result;
    }

    // ---- Замена функций Radzen ----

    // Заголовок новой таблицы и нового столбца пуст: «Столбец N» Radzen подписывает номером места вставки и остальные
    // не перенумеровывает, подписи оказывались повторяющимися.
    Radzen.formatTableColumnHeader = function () { return '&nbsp;'; };

    Radzen.addTableRow = function (context, command) {
        apply(context, function (model, id) { return logic.insertRow(model, id, command !== 'addRowBefore'); });
    };

    Radzen.deleteTableRow = function (context) {
        apply(context, function (model, id) { return logic.deleteRows(model, id); });
    };

    Radzen.addTableColumn = function (context, command) {
        apply(context, function (model, id) { return logic.insertColumn(model, id, command !== 'addColumnBefore'); });
    };

    Radzen.deleteTableColumn = function (context) {
        var result = apply(context, function (model, id) { return logic.deleteColumns(model, id); });
        if (!result.ok && result.reason === 'last-column') warn('Единственный столбец не удаляется. Чтобы убрать таблицу целиком, удалите таблицу.');
    };

    // Таблица в ячейке таблицы не бывает: кнопка «Таблица» в ячейке недоступна, это — страховка.
    var insertTable = Radzen.insertTable;
    Radzen.insertTable = function (ref, value) {
        var context = this.getTableContext(ref);
        if (context && context.cell) {
            warn('Таблицу нельзя вставить в ячейку другой таблицы.');
            return this.createCommandResult(ref, true);
        }
        return insertTable.call(this, ref, value);
    };

    // ---- Вложенная таблица, вставленная из буфера или перетаскиванием, — текстом ----
    // Тем же, что сохранит сервер (RichTextSanitizer.Tables): правила общие с тестами rpdfos-table-logic.js.
    // Обработчик Paste самого редактора не годится: с ним простой текст вставляется разметкой и теряет переносы строк.

    function cellOf(node, editor) {
        var cell = node && node.closest ? node.closest('td, th') : null;
        return cell && editor.contains(cell) ? cell : null;
    }

    function toTree(node) {
        if (node.nodeType === 3) return { text: node.data };
        var attrs = {};
        Array.prototype.forEach.call(node.attributes, function (attribute) { attrs[attribute.name] = attribute.value; });
        var children = [];
        Array.prototype.forEach.call(node.childNodes, function (child) {
            if (child.nodeType === 1 || child.nodeType === 3) children.push(toTree(child));
        });
        return { tag: node.localName, attrs: attrs, children: children };
    }

    function flattenNestedTables(editor) {
        var count = 0;
        Array.prototype.slice.call(editor.querySelectorAll('td, th')).forEach(function (cell) {
            // Ячейки верхней таблицы, в которых есть таблица; ячейки вложенных уйдут вместе с ними.
            if (!editor.contains(cell) || !cell.querySelector('table') || cellOf(cell.closest('table').parentElement, editor)) return;
            var tree = toTree(cell);
            var tables = logic.flattenNested(tree);
            if (!tables) return;
            logic.removeLayoutSpace(tree);
            var selection = getSelection();
            var hadCaret = selection && selection.anchorNode && cell.contains(selection.anchorNode);
            cell.innerHTML = logic.serializeChildren(tree);
            if (hadCaret) {
                var range = document.createRange();
                range.selectNodeContents(cell);
                range.collapse(false);
                selection.removeAllRanges();
                selection.addRange(range);
            }
            count += tables;
        });
        return count;
    }


    // ---- Рисунки, вставленные из буфера или перетаскиванием (этап 7, B4а) ----
    // Файл рисунка Radzen загружает сам. Здесь — рисунок, пришедший разметкой: свой («/api/images/…», браузер кладёт в буфер
    // абсолютный адрес) остаётся, остальные — из Word (адрес файла на компьютере автора, браузер его не читает), с сайта или
    // data: — убираются с предупреждением, иначе слова вставились бы, а рисунок молча пропал при сохранении.
    var OWN_IMAGE = /^\/api\/images\/[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

    function checkPastedImages(editor) {
        var foreign = 0;
        var local = 0;
        Array.prototype.slice.call(editor.querySelectorAll('img')).forEach(function (image) {
            var source = (image.getAttribute('src') || '').trim();
            if (source.indexOf(location.origin + '/api/images/') === 0) {
                source = source.substring(location.origin.length);
                image.setAttribute('src', source);
            }
            if (OWN_IMAGE.test(source)) return;
            if (/^file:/i.test(source)) local++;
            foreign++;
            image.remove();
        });
        if (local) warn('Рисунок из Word вставлен ссылкой на файл на вашем компьютере, браузер его не читает: вставьте рисунок отдельно — из файла или из буфера.');
        else if (foreign) warn('Рисунок с чужим адресом не сохраняется: сохраните его в файл и вставьте отдельно.');
        return foreign;
    }

    document.addEventListener('input', function (event) {
        if (event.inputType !== 'insertFromPaste' && event.inputType !== 'insertFromDrop') return;
        var editor = event.target && event.target.closest ? event.target.closest('.rz-html-editor-content') : null;
        if (!editor) return;
        var nested = flattenNestedTables(editor);
        if (nested) warn('Таблица внутри ячейки не сохраняется: её содержимое вставлено текстом.');
        var images = checkPastedImages(editor);
        // Редактор передаёт значение на каждой правке: после замены сообщаем итог.
        if (nested || images) editor.dispatchEvent(new Event('input', { bubbles: true }));
    });
})();
