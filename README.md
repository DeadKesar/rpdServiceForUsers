# RpdFos — АС создания РПД и ФОС (прототип)

Скелет v2: ASP.NET Core (Carter, вертикальные срезы) + прикладное ядро + PostgreSQL + EF Core.
Аутентификация и Blazor-клиент добавляются на этапе 1, фоновый обработчик и парсер — на этапе 2.

```
src/RpdFos.Core   прикладное ядро: сущности, AppDbContext, миграции, перехватчик версий
src/RpdFos.Api    хост: модули Carter, OpenAPI + Swagger UI, обработка ошибок
tests/            smoke-тест и тесты правил
```

Быстрый старт (подробно — runbook этапа 0, `ВКР_Прототип_Спринт0.md`):

```
dotnet new sln -n RpdFos
dotnet sln add src/RpdFos.Core src/RpdFos.Api tests/RpdFos.Api.Tests
docker compose up -d db
dotnet restore
dotnet tool install --global dotnet-ef
dotnet ef migrations add Initial -p src/RpdFos.Core -s src/RpdFos.Api
dotnet run --project src/RpdFos.Api        # http://localhost:5080/swagger
dotnet test
```

Строка подключения для разработки лежит в `appsettings.json`; на сервере её задаёт
переменная окружения `ConnectionStrings__Default`.
