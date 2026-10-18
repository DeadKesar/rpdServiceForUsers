# RpdFos — АС создания РПД и ФОС (прототип)

Прототип к магистерской ВКР: автоматизированная система формирования рабочих программ дисциплин (РПД)
и фондов оценочных средств (ФОС).

Стек: .NET 10, ASP.NET Core (Carter, вертикальные срезы), EF Core + PostgreSQL 17,
ASP.NET Core Identity + OpenIddict (OpenID Connect, authorization code + PKCE), Blazor WebAssembly.

## Структура

```
src/RpdFos.Core         прикладное ядро: сущности, AppDbContext, миграции, перехватчик версий
src/RpdFos.Api          хост: модули Carter, сервер авторизации OpenIddict, страница входа,
                        OpenAPI + Swagger UI; раздаёт веб-клиент
src/RpdFos.Web          веб-клиент Blazor WebAssembly — отдельно не запускается
tests/RpdFos.Api.Tests  тесты API и правил домена
docs/                   проект, задания этапов, runbook-и, журнал разработки
```

## Запуск

Нужны .NET SDK 10 (версия — в `global.json`) и Docker.

```
dotnet dev-certs https --trust          # один раз: OpenIddict принимает только HTTPS
docker compose up -d db
dotnet user-secrets set "Seed:AdminEmail" "admin@rpdfos.local" --project src/RpdFos.Api
dotnet user-secrets set "Seed:AdminPassword" '<пароль>' --project src/RpdFos.Api
dotnet run --project src/RpdFos.Api
```

- https://localhost:5081 — веб-клиент, https://localhost:5081/swagger — Swagger UI, `/health` — проверка.
- Профиль `https` из `Properties/launchSettings.json` включает окружение Development: при старте
  применяются миграции и создаются роли, клиент OIDC `rpdfos-web` и первый администратор.
- Импорт исходных данных — страница «Импорт данных» (методист и администратор): учебный план (.xlsx),
  список и матрица компетенций (.docx), должности (.xlsx); разбор идёт в фоне.
- Пароль администратора хранится в user-secrets и в репозиторий не попадает. Требования: не короче
  8 символов, заглавная и строчная буквы, цифра и спецсимвол. Остальные учётные записи создаёт
  администратор на странице «Пользователи».

## Сборка и тесты

```
dotnet build                            # без ошибок и предупреждений
dotnet test                             # нужен запущенный Docker
```

Интеграционные тесты (`tests/RpdFos.Api.Tests/Integration`) поднимают чистую PostgreSQL 17 в контейнере
(Testcontainers) и применяют к ней миграции; база разработки не используется. Аутентификация в них —
тестовая схема (роль из заголовка `X-Test-Role`), политики доступа — рабочие.

## Миграции

Миграции живут в ядре (`-p`), конфигурацию берут из хоста (`-s`):

```
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Имя> -p src/RpdFos.Core -s src/RpdFos.Api
```

В Development миграции применяются при запуске. Схема меняется только новыми миграциями,
существующие не правятся.

## Конфигурация

| Ключ | Назначение |
| --- | --- |
| `ConnectionStrings:Default` | Строка подключения. Для разработки — в `appsettings.json`, на сервере — переменная окружения `ConnectionStrings__Default` |
| `Database:AutoMigrate` | Миграции и начальные данные при запуске (включено в Development) |
| `Auth:PublicUrl` | Публичный адрес хоста. По нему при запуске сверяются адреса возврата клиента OIDC |
| `Seed:AdminEmail`, `Seed:AdminPassword` | Первый администратор — только в user-secrets |
| `Jobs:Enabled` | Сервер фоновых задач Hangfire и панель `/hangfire` (выключено в окружении Testing) |
| `Jobs:QueuePollSeconds` | Как часто сервер задач опрашивает очередь (по умолчанию 15 с) |

Фоновые задачи (импорт исходных файлов) — Hangfire, хранилище в той же базе, схема `hangfire`.
Панель задач — https://localhost:5081/hangfire, только для администратора: вход — через страницу входа системы.

## Документация

- [CLAUDE.md](CLAUDE.md) — соглашения проекта.
- [docs/design](docs/design) — проект системы (глава 2 ВКР).
- [docs/tasks](docs/tasks) — задания этапов.
- [docs/runbooks](docs/runbooks) — пошаговые инструкции этапов.
- [docs/journal.md](docs/journal.md) — журнал разработки (основа главы 4 ВКР).
