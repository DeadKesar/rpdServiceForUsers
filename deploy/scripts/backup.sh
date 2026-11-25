#!/bin/sh
# Резервные копии базы стенда (этап 8, B7). Служба compose `backup` запускает его без аргументов (loop); остальные команды —
# вручную из папки deploy: docker compose run --rm backup <команда>
#
#   loop                   (по умолчанию) ежедневно в BACKUP_TIME делать копию, хранить BACKUP_KEEP последних
#   now                    сделать копию сейчас
#   list                   показать копии и их размер
#   verify [файл]          проверить копию: восстановить во временную базу и сосчитать записи (по умолчанию — последнюю)
#   restore [файл] [--replace]
#                          восстановить копию в базу стенда (по умолчанию — последнюю). Не пустую базу заменяет
#                          только с --replace, предварительно сохранив её копией before-restore-…
#   check                  проверка здоровья службы: копия или запуск не старше 26 часов
#
# Копия — pg_dump в формате custom: вся база целиком, а в ней исходные файлы, шаблоны оформления и рисунки (они лежат
# в базе). Сформированные файлы (PDF/DOCX) не копируются: их формируют заново, срок их хранения — 30 дней.
set -eu
umask 077

DIR=/backups
KEEP="${BACKUP_KEEP:-30}"
AT="${BACKUP_TIME:-03:00}"
DB="${PGDATABASE:-rpdfos}"
# Подключение к базе задают PGHOST, PGUSER, PGPASSWORD из compose.

log() { echo "$(date '+%F %T') $*"; }
fail() { echo "Ошибка: $1" >&2; exit 1; }

# Размер файла в мегабайтах.
mb() { awk -v b="$(wc -c < "$1")" 'BEGIN { printf "%.1f", b / 1048576 }'; }

# Копии по расписанию: rpdfos-ГГГГММДД-ЧЧММСС.dump. Копии before-restore-… в хранение не входят: их удаляют вручную.
scheduled() { ls -1 "$DIR" | grep -E '^rpdfos-[0-9]{8}-[0-9]{6}\.dump$' | sort || true; }

# Одна копия: во временный файл, проверка оглавления, затем переименование — частичной копии под настоящим именем не бывает.
# Ошибки возвращаются кодом, а не exit: цикл службы должен пережить неудачу (set -e внутри условий не действует).
make_backup() {
    name="$1-$(date +%Y%m%d-%H%M%S).dump"
    part="$DIR/$name.partial"
    began=$(date +%s)
    if ! pg_dump -Fc -f "$part" "$DB"; then
        rm -f "$part"
        echo "Ошибка: pg_dump не выполнился" >&2
        return 1
    fi
    if ! pg_restore --list "$part" > /dev/null; then
        rm -f "$part"
        echo "Ошибка: копия не читается, удалена" >&2
        return 1
    fi
    mv "$part" "$DIR/$name"
    log "копия $name: $(mb "$DIR/$name") МБ, $(($(date +%s) - began)) с"
}

# Лишние старые копии — только после удачной новой.
prune() {
    scheduled | sort -r | tail -n +$((KEEP + 1)) | while read -r old; do
        rm -f "$DIR/$old"
        log "удалена старая копия $old"
    done
    find "$DIR" -name '*.partial' -mmin +120 -delete
}

# Копия по имени, по пути или самая свежая.
pick() {
    if [ -z "$1" ]; then
        found=$(scheduled | tail -n 1)
        [ -n "$found" ] || fail "копий нет: сделайте её командой now"
        echo "$DIR/$found"
    elif [ -f "$DIR/$1" ]; then
        echo "$DIR/$1"
    elif [ -f "$1" ]; then
        echo "$1"
    else
        fail "копия $1 не найдена в папке копий; чужой файл сначала положите туда"
    fi
}

loop() {
    touch /tmp/started
    at_s=$(printf '%s' "$AT" | awk -F: '{ print $1 * 3600 + $2 * 60 }')
    # День последней копии: после перезагрузки ночью копию не делаем второй раз, а после пропущенной ночи — сразу.
    last=$(scheduled | tail -n 1 | cut -c8-15)
    retry=0
    log "служба копирования: ежедневно в $AT, хранится $KEEP копий, папка $DIR"
    while true; do
        today=$(date +%Y%m%d)
        now_s=$(date +'%H %M %S' | awk '{ print $1 * 3600 + $2 * 60 + $3 }')
        epoch=$(date +%s)
        # Пока хост не применил миграции, база пуста: такую копию не делаем.
        if [ "$(table_count "$DB")" -eq 0 ]; then
            sleep 30
            continue
        fi
        # Условие «после времени копии и сегодня ещё не делали», а не точное время: переживает сон компьютера.
        if [ "$today" != "$last" ] && [ "$now_s" -ge "$at_s" ] && [ "$epoch" -ge "$retry" ]; then
            if make_backup rpdfos; then
                last=$today
                prune
            else
                retry=$((epoch + 3600))
                log "повтор через час"
            fi
        fi
        sleep 30
    done
}

list() {
    shown=0
    for f in "$DIR"/*.dump; do
        [ -e "$f" ] || break
        shown=1
        printf '%s  %s МБ\n' "$(basename "$f")" "$(mb "$f")"
    done
    [ "$shown" -eq 1 ] || echo "Копий нет."
}

# Базы-черновики: в служебную базу postgres, потому что к удаляемой подключиться нельзя.
admin_sql() { psql -d postgres -v ON_ERROR_STOP=1 -qtA -c "$1"; }

# Число таблиц приложения: пустая база (хост ещё не применил миграции) — 0.
table_count() {
    psql -d "$1" -v ON_ERROR_STOP=1 -qtA -c "select count(*) from information_schema.tables where table_schema in ('public', 'hangfire')"
}

# Короткая сводка по базе для сравнения до и после.
summary() {
    psql -d "$1" -v ON_ERROR_STOP=1 -qtA -F ' ' <<'SQL'
select 'таблиц', count(*) from information_schema.tables where table_schema in ('public', 'hangfire');
select 'сотрудников', count(*) from users;
select 'учётных записей', count(*) from "AspNetUsers";
select 'учебных планов', count(*) from curriculum_plans;
select 'шаблонов', count(*) from document_templates;
select 'документов', count(*) from documents;
select 'исходных файлов', count(*) from uploaded_files;
select 'рисунков', count(*) from document_images;
select 'последняя миграция', max(migration_id) from "__EFMigrationsHistory";
SQL
}

verify() {
    f=$(pick "${1:-}")
    tmp=rpdfos_check
    log "проверка $(basename "$f"): восстановление во временную базу $tmp"
    admin_sql "DROP DATABASE IF EXISTS $tmp WITH (FORCE)"
    admin_sql "CREATE DATABASE $tmp"
    began=$(date +%s)
    if ! pg_restore -d "$tmp" --no-owner --exit-on-error "$f"; then
        admin_sql "DROP DATABASE IF EXISTS $tmp WITH (FORCE)"
        fail "копия не восстанавливается"
    fi
    if [ "$(table_count "$tmp")" -eq 0 ]; then
        admin_sql "DROP DATABASE $tmp WITH (FORCE)"
        fail "в копии нет таблиц: она снята с пустой базы"
    fi
    log "восстановлено за $(($(date +%s) - began)) с; в копии:"
    summary "$tmp"
    admin_sql "DROP DATABASE $tmp WITH (FORCE)"
    log "копия годна"
}

restore() {
    replace=0
    file=
    for arg in "$@"; do
        case "$arg" in
            --replace) replace=1 ;;
            *) file=$arg ;;
        esac
    done
    f=$(pick "$file")

    # Хост работает с базой постоянно (фоновые задачи): пока он запущен, подменять базу нельзя.
    busy=$(admin_sql "select count(*) from pg_stat_activity where datname = '$DB'")
    [ "$busy" -eq 0 ] || fail "к базе подключено соединений: $busy. Остановите хост: docker compose stop api"

    exists=$(admin_sql "select count(*) from pg_database where datname = '$DB'")
    tables=0
    [ "$exists" -eq 0 ] || tables=$(table_count "$DB")
    if [ "$tables" -gt 0 ]; then
        [ "$replace" -eq 1 ] || fail "в базе уже есть данные ($tables таблиц): восстановление их заменит. Если так и нужно, добавьте --replace"
        log "база не пуста: сначала копия текущих данных"
        make_backup before-restore || fail "копия текущих данных не сделана, база не тронута"
    fi

    # Сначала во временную базу: неудача не оставит стенд без базы.
    tmp=rpdfos_restore
    log "восстановление $(basename "$f")"
    admin_sql "DROP DATABASE IF EXISTS $tmp WITH (FORCE)"
    admin_sql "CREATE DATABASE $tmp"
    began=$(date +%s)
    if ! pg_restore -d "$tmp" --no-owner --exit-on-error "$f"; then
        admin_sql "DROP DATABASE IF EXISTS $tmp WITH (FORCE)"
        fail "копия не восстанавливается, база стенда не тронута"
    fi
    admin_sql "DROP DATABASE IF EXISTS $DB WITH (FORCE)"
    admin_sql "ALTER DATABASE $tmp RENAME TO $DB"
    log "восстановлено за $(($(date +%s) - began)) с; в базе:"
    summary "$DB"
    log "готово. Запустите хост: docker compose up -d --wait"
}

# Здоровье: служба жива и копии идут — свежая копия или недавний запуск (до первой копии).
check() {
    # 1560 минут — сутки и два часа запаса на медленную копию.
    find "$DIR" -name 'rpdfos-*.dump' -mmin -1560 | grep -q . && return 0
    find /tmp/started -mmin -1560 | grep -q .
}

cmd="${1:-loop}"
[ "$#" -eq 0 ] || shift
case "$cmd" in
    loop) loop ;;
    now) make_backup rpdfos || exit 1; prune ;;
    list) list ;;
    verify) verify "$@" ;;
    restore) restore "$@" ;;
    check) check ;;
    *) fail "неизвестная команда $cmd (loop, now, list, verify, restore, check)" ;;
esac
