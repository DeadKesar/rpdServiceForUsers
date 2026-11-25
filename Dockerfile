# Образ хоста API вместе с клиентом Blazor (этап 8, B4): клиент раздаёт сам хост, nginx стенда только завершает HTTPS.
# Сборка из корня репозитория: docker build -t rpdfos-api .

# --- Сборка ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только проекты: слой с восстановленными пакетами не пересобирается, пока не менялись зависимости.
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/DisciplineDataExtractor/DisciplineDataExtractor.csproj src/DisciplineDataExtractor/
COPY src/RpdFos.Core/RpdFos.Core.csproj src/RpdFos.Core/
COPY src/RpdFos.Web/RpdFos.Web.csproj src/RpdFos.Web/
COPY src/RpdFos.Api/RpdFos.Api.csproj src/RpdFos.Api/
RUN dotnet restore src/RpdFos.Api/RpdFos.Api.csproj

COPY src/ src/
# Клиент (Blazor WebAssembly) публикуется вместе с хостом: его файлы попадают в wwwroot хоста.
RUN dotnet publish src/RpdFos.Api/RpdFos.Api.csproj -c Release --no-restore -o /app

# --- Выполнение ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Тома: ключи Data Protection (без них после перезапуска перестают действовать вход и ссылки из писем)
# и сформированные файлы. Папки создаются для пользователя app, под которым запускается хост.
RUN mkdir -p /var/lib/rpdfos/keys /var/lib/rpdfos/generated && chown -R app:app /var/lib/rpdfos
VOLUME ["/var/lib/rpdfos"]

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DataProtection__KeysPath=/var/lib/rpdfos/keys \
    Generation__StoragePath=/var/lib/rpdfos/generated

USER app
EXPOSE 8080

# В образе нет curl: запрос /health — средствами bash.
HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "RpdFos.Api.dll"]
