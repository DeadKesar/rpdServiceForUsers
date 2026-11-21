// Логика таблиц форматированного текста без DOM (этап 7, B4, доработка): строки и столбцы — по сетке, вложенная
// таблица — текстом. Подключается в браузере (rpdfos-tables.js) и в тестах через Jint
// (tests/RpdFos.Api.Tests/Documents/RichTextTableLogicTests.cs): чистые функции над данными проверяются без браузера.
//
// Модель таблицы — обычные объекты, их можно переслать как JSON:
//   { sections: [ { kind: 'thead' | 'tbody' | 'tfoot', rows: [ { cells: [ { id, tag, colspan, rowspan } ] } ] } ] }
// В rows[i].cells — только собственные ячейки строки, в порядке разметки; ячейка, объединённая сверху, в строке
// не повторяется. Сетку по такой разметке строит layout() — так её строит и браузер. id — чем адаптер узнаёт свою
// ячейку; у новой ячейки id == null.
// Операции меняют модель на месте и возвращают { ok: true } или { ok: false, reason }.
var RpdFosTableLogic = (function () {
    'use strict';

    function fail(reason) { return { ok: false, reason: reason }; }

    // ---- Сетка ----

    // Сетка раздела: grid[строка][столбец] — ячейка, занимающая это место. Ячейка встаёт на первое свободное место
    // строки, объединение по строкам не выходит за раздел (так его читает браузер).
    function layout(section) {
        var grid = [];
        var rows = section.rows;
        for (var r = 0; r < rows.length; r++) {
            if (!grid[r]) grid[r] = [];
            var c = 0;
            var cells = rows[r].cells;
            for (var i = 0; i < cells.length; i++) {
                var cell = cells[i];
                while (grid[r][c]) c++;
                for (var dr = 0; dr < cell.rowspan && r + dr < rows.length; dr++) {
                    if (!grid[r + dr]) grid[r + dr] = [];
                    for (var dc = 0; dc < cell.colspan; dc++) grid[r + dr][c + dc] = cell;
                }
                c += cell.colspan;
            }
        }
        return grid;
    }

    // Места ячейки в сетке: первая и последняя строка, первый и последний столбец.
    function extent(grid, cell) {
        var e = { r0: -1, r1: -1, c0: -1, c1: -1 };
        for (var r = 0; r < grid.length; r++)
            for (var c = 0; c < grid[r].length; c++)
                if (grid[r][c] === cell) {
                    if (e.r0 < 0 || r < e.r0) e.r0 = r;
                    if (r > e.r1) e.r1 = r;
                    if (e.c0 < 0 || c < e.c0) e.c0 = c;
                    if (c > e.c1) e.c1 = c;
                }
        return e;
    }

    function widthOf(grid) {
        var width = 0;
        for (var r = 0; r < grid.length; r++) width = Math.max(width, grid[r].length);
        return width;
    }

    function columnCount(model) {
        var width = 0;
        model.sections.forEach(function (section) { width = Math.max(width, widthOf(layout(section))); });
        return width;
    }

    function find(model, id) {
        for (var s = 0; s < model.sections.length; s++) {
            var section = model.sections[s];
            var grid = layout(section);
            for (var r = 0; r < grid.length; r++)
                for (var c = 0; c < grid[r].length; c++) {
                    var cell = grid[r][c];
                    if (cell && cell.id === id) return { section: section, grid: grid, cell: cell, e: extent(grid, cell) };
                }
        }
        return null;
    }

    // Для тестов и отладки: id ячеек по местам сетки, '+' — новая ячейка, '?' — пустое место.
    function gridOf(model) {
        return model.sections.map(function (section) {
            var grid = layout(section);
            var width = widthOf(grid);
            return {
                kind: section.kind,
                rows: grid.map(function (row) {
                    var line = [];
                    for (var c = 0; c < width; c++) line.push(!row[c] ? '?' : row[c].id == null ? '+' : String(row[c].id));
                    return line;
                })
            };
        });
    }

    function newCell(section) {
        return { id: null, tag: section.kind === 'thead' ? 'th' : 'td', colspan: 1, rowspan: 1 };
    }

    function removeFromRows(section, cell) {
        section.rows.forEach(function (row) {
            var at = row.cells.indexOf(cell);
            if (at >= 0) row.cells.splice(at, 1);
        });
    }

    function startOf(grid, cell) { return extent(grid, cell).c0; }

    // ---- Строки ----

    // Новая строка выше или ниже ячейки (целиком, если ячейка объединена по строкам). Ячейка, объединённая через
    // место вставки, растягивается; в остальных столбцах — пустая ячейка.
    function insertRow(model, id, after) {
        var f = find(model, id);
        if (!f) return fail('no-cell');
        var section = f.section;
        var grid = f.grid;
        var at = after ? f.e.r1 + 1 : f.e.r0;
        var width = widthOf(grid);
        var cells = [];
        var stretched = [];
        for (var c = 0; c < width; c++) {
            var above = at > 0 ? grid[at - 1][c] : undefined;
            var below = at < section.rows.length ? grid[at][c] : undefined;
            if (above && above === below) {
                if (stretched.indexOf(above) < 0) {
                    var ea = extent(grid, above);
                    above.rowspan = ea.r1 - ea.r0 + 2;
                    stretched.push(above);
                }
            } else cells.push(newCell(section));
        }
        section.rows.splice(at, 0, { cells: cells });
        return { ok: true };
    }

    // Удаляются строки, которые занимает ячейка. Ячейка, начатая выше, сужается; начатая в удаляемых строках и
    // уходящая дальше — переходит в следующую строку вместе с содержимым; остальные уходят с удаляемыми строками.
    function deleteRows(model, id) {
        var f = find(model, id);
        if (!f) return fail('no-cell');
        var section = f.section;
        var grid = f.grid;
        var r0 = f.e.r0;
        var r1 = f.e.r1;
        var width = columnCount(model);

        var seen = [];
        var moved = [];
        for (var r = r0; r <= r1; r++)
            for (var c = 0; c < grid[r].length; c++) {
                var cell = grid[r][c];
                if (!cell || seen.indexOf(cell) >= 0) continue;
                seen.push(cell);
                var e = extent(grid, cell);
                if (e.r0 < r0) cell.rowspan = e.r1 - e.r0 + 1 - (Math.min(e.r1, r1) - r0 + 1);
                else if (e.r1 > r1) moved.push({ cell: cell, e: e });
            }

        if (moved.length) {
            var target = section.rows[r1 + 1];
            moved.sort(function (a, b) { return a.e.c0 - b.e.c0; });
            moved.forEach(function (m) {
                removeFromRows(section, m.cell);
                m.cell.rowspan = m.e.r1 - r1;
                var at = 0;
                while (at < target.cells.length && startOf(grid, target.cells[at]) < m.e.c0) at++;
                target.cells.splice(at, 0, m.cell);
            });
        }
        section.rows.splice(r0, r1 - r0 + 1);

        if (!section.rows.length) {
            var hasOtherBody = model.sections.some(function (s) { return s !== section && s.kind === 'tbody'; });
            if (section.kind === 'tbody' && !hasOtherBody) {
                // Таблица без строк в теле не бывает: остаётся одна пустая.
                var cells = [];
                for (var i = 0; i < width; i++) cells.push(newCell(section));
                section.rows.push({ cells: cells });
            } else model.sections.splice(model.sections.indexOf(section), 1);
        }
        return { ok: true };
    }

    // ---- Столбцы ----

    // Новый столбец слева или справа от ячейки (целиком, если ячейка объединена по столбцам), во всех разделах.
    // Ячейка, объединённая через место вставки, растягивается; в остальных строках — пустая ячейка.
    function insertColumn(model, id, after) {
        var f = find(model, id);
        if (!f) return fail('no-cell');
        var p = after ? f.e.c1 + 1 : f.e.c0;
        model.sections.forEach(function (section) {
            var grid = layout(section);
            var stretched = [];
            section.rows.forEach(function (row, r) {
                var left = p > 0 ? grid[r][p - 1] : undefined;
                var right = grid[r][p];
                if (left && left === right) {
                    if (stretched.indexOf(left) < 0) {
                        var el = extent(grid, left);
                        left.colspan = el.c1 - el.c0 + 2;
                        stretched.push(left);
                    }
                    return;
                }
                var at = 0;
                while (at < row.cells.length && startOf(grid, row.cells[at]) < p) at++;
                row.cells.splice(at, 0, newCell(section));
            });
        });
        return { ok: true };
    }

    // Удаляются столбцы, которые занимает ячейка, во всех разделах. Ячейка, занимающая и другие столбцы, сужается;
    // ячейка только в удаляемых — исчезает. Последний столбец не удаляется.
    function deleteColumns(model, id) {
        var f = find(model, id);
        if (!f) return fail('no-cell');
        var c0 = f.e.c0;
        var c1 = f.e.c1;
        if (c1 - c0 + 1 >= columnCount(model)) return fail('last-column');
        model.sections.forEach(function (section) {
            var grid = layout(section);
            var seen = [];
            grid.forEach(function (row) {
                row.forEach(function (cell) {
                    if (!cell || seen.indexOf(cell) >= 0) return;
                    seen.push(cell);
                    var e = extent(grid, cell);
                    var overlap = Math.min(e.c1, c1) - Math.max(e.c0, c0) + 1;
                    if (overlap <= 0) return;
                    if (overlap >= e.c1 - e.c0 + 1) removeFromRows(section, cell);
                    else cell.colspan = e.c1 - e.c0 + 1 - overlap;
                });
            });
        });
        return { ok: true };
    }

    // ---- Вложенная таблица — текстом ----
    // Дерево — обычные объекты: элемент { tag, attrs, children }, текст { text }. Правила — как у санитайзера на
    // сервере (RichTextSanitizer.Tables/Flatten): в редакторе вложенная таблица сразу становится тем текстом,
    // который сохранит сервер. Совпадение проверяют тесты на тех же разметках.

    function isEl(node) { return node.tag !== undefined; }
    function isTag(node, tags) { return isEl(node) && tags.indexOf(node.tag) >= 0; }
    function isText(node) { return node.text !== undefined; }
    function br() { return { tag: 'br', attrs: {}, children: [] }; }
    function isBlank(text) { return /^\s*$/.test(text); }

    // Вложенные таблицы в порядке документа: те, что внутри ячейки. Корень-ячейка тоже считается ячейкой.
    function nestedTables(root) {
        var found = [];
        (function walk(node, inCell) {
            node.children.forEach(function (child) {
                if (!isEl(child)) return;
                if (child.tag === 'table' && inCell) found.push({ parent: node, table: child });
                walk(child, inCell || child.tag === 'td' || child.tag === 'th');
            });
        })(root, isTag(root, ['td', 'th']));
        return found;
    }

    function descendants(node, tag, into) {
        node.children.forEach(function (child) {
            if (!isEl(child)) return;
            if (child.tag === tag) into.push(child);
            descendants(child, tag, into);
        });
        return into;
    }

    // Ячейки вложенной таблицы — подряд через пробел, строки и текст вокруг — через перенос: слова не склеиваются.
    function flatten(parent, table) {
        var kids = parent.children;
        function put(node) { kids.splice(kids.indexOf(table), 0, node); }
        function previous() { return kids[kids.indexOf(table) - 1]; }
        function neighbor(step) {
            var i = kids.indexOf(table) + step;
            while (kids[i] && isText(kids[i]) && isBlank(kids[i].text)) i += step;
            return kids[i];
        }
        function space() {
            var prev = previous();
            if (prev && isText(prev) && /[ ]$/.test(prev.text)) return;
            put({ text: ' ' });
        }
        function trimTail() {
            var prev = previous();
            if (prev && isText(prev) && /[ ]$/.test(prev.text)) prev.text = prev.text.replace(/\s+$/, '');
        }

        var before = neighbor(-1);
        if (before && !isTag(before, ['br'])) put(br());
        var after = neighbor(1);

        var rows = descendants(table, 'tr', []);
        rows.forEach(function (row, r) {
            if (r > 0) {
                trimTail();
                put(br());
            }
            var cells = row.children.filter(function (child) { return isTag(child, ['td', 'th']); });
            cells.forEach(function (cell, c) {
                if (c > 0) space();
                cell.children.slice().forEach(function (child) {
                    if (isTag(child, ['p'])) {
                        // Абзац внутри абзаца не бывает: слова абзаца ячейки — просто текст строки.
                        child.children.slice().forEach(put);
                        space();
                    } else put(child);
                });
            });
        });
        trimTail();
        if (after && !isTag(after, ['br'])) put(br());
        kids.splice(kids.indexOf(table), 1);
    }

    // Все вложенные таблицы под корнем — текстом; сначала самые глубокие. Возвращает, сколько их было.
    function flattenNested(root) {
        var found = nestedTables(root);
        for (var i = found.length - 1; i >= 0; i--) flatten(found[i].parent, found[i].table);
        return found.length;
    }

    // Отступы и переводы строк между строками и ячейками, по краям ячейки и между её абзацами (так выглядит таблица
    // из Word) — не текст. Пробел между словами внутри ячейки остаётся, как и неразрывный пробел.
    function removeLayoutSpace(root) {
        var doomed = [];
        (function walk(node) {
            if (isTag(node, ['table', 'thead', 'tbody', 'tfoot', 'tr', 'td', 'th'])) {
                node.children.forEach(function (child, i) {
                    if (!isText(child) || !/^[ \t\r\n\f]*$/.test(child.text)) return;
                    var prev = node.children[i - 1];
                    var next = node.children[i + 1];
                    if (isTag(node, ['table', 'thead', 'tbody', 'tfoot', 'tr']) || !prev || isTag(prev, ['p', 'ul', 'ol']) ||
                        !next || isTag(next, ['p', 'ul', 'ol'])) doomed.push({ parent: node, child: child });
                });
            }
            node.children.forEach(function (child) { if (isEl(child)) walk(child); });
        })(root);
        doomed.forEach(function (d) { d.parent.children.splice(d.parent.children.indexOf(d.child), 1); });
    }

    // ---- Разметка ----

    var VOID = ['br', 'hr', 'img', 'col', 'input', 'wbr'];

    function escapeText(text) {
        return text.replace(/&/g, '&amp;').replace(/ /g, '&nbsp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function escapeAttribute(value) {
        return value.replace(/&/g, '&amp;').replace(/ /g, '&nbsp;').replace(/"/g, '&quot;');
    }

    function serialize(node) {
        if (isText(node)) return escapeText(node.text);
        var attrs = '';
        Object.keys(node.attrs || {}).forEach(function (name) { attrs += ' ' + name + '="' + escapeAttribute(node.attrs[name]) + '"'; });
        return '<' + node.tag + attrs + '>' + (VOID.indexOf(node.tag) >= 0 ? '' : serializeChildren(node) + '</' + node.tag + '>');
    }

    function serializeChildren(node) { return node.children.map(serialize).join(''); }

    return {
        layout: layout,
        extent: extent,
        columnCount: columnCount,
        gridOf: gridOf,
        insertRow: insertRow,
        deleteRows: deleteRows,
        insertColumn: insertColumn,
        deleteColumns: deleteColumns,
        flattenNested: flattenNested,
        removeLayoutSpace: removeLayoutSpace,
        serialize: serialize,
        serializeChildren: serializeChildren
    };
})();
