# Single image: the API serves the built PWA, so a shop runs one container.
FROM node:20-alpine AS web
WORKDIR /web
COPY AlgeriaPos.WebUI/package*.json ./
RUN npm ci
COPY AlgeriaPos.WebUI/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api
WORKDIR /src
COPY Directory.Build.props AlgeriaPos.sln ./
# Project files first, so the restore layer is reused until a dependency changes.
COPY AlgeriaPos.Domain/AlgeriaPos.Domain.csproj AlgeriaPos.Domain/
COPY AlgeriaPos.Application/AlgeriaPos.Application.csproj AlgeriaPos.Application/
COPY AlgeriaPos.Infrastructure/AlgeriaPos.Infrastructure.csproj AlgeriaPos.Infrastructure/
COPY AlgeriaPos.Migrations.SqlServer/AlgeriaPos.Migrations.SqlServer.csproj AlgeriaPos.Migrations.SqlServer/
COPY AlgeriaPos.API/AlgeriaPos.API.csproj AlgeriaPos.API/
RUN dotnet restore AlgeriaPos.API/AlgeriaPos.API.csproj
COPY AlgeriaPos.Domain/ AlgeriaPos.Domain/
COPY AlgeriaPos.Application/ AlgeriaPos.Application/
COPY AlgeriaPos.Infrastructure/ AlgeriaPos.Infrastructure/
COPY AlgeriaPos.Migrations.SqlServer/ AlgeriaPos.Migrations.SqlServer/
COPY AlgeriaPos.API/ AlgeriaPos.API/
COPY --from=web /web/dist/algeria-pos-web-ui/browser/ AlgeriaPos.API/wwwroot/
RUN dotnet publish AlgeriaPos.API/AlgeriaPos.API.csproj -c Release -o /app --no-restore

# Debian rather than alpine or a chiselled base, on purpose: rental due dates and the
# integration schedule convert through the container's tzdata, and the conversion falls back to
# UTC silently when a zone is missing — due dates would move by an hour with no message.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=api /app ./
# curl is for the HEALTHCHECK below and for the runbook's own probe: in production the pos
# service publishes no port, so the only way to ask it how it is doing is from inside.
# SQLite databases (company and platform) live on a volume so they survive a rebuild; the
# image runs as the unprivileged user the base image provides.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data && chown "$APP_UID" /data
VOLUME /data
USER $APP_UID
# Deliberately no ConnectionStrings__Default and no Platform__ConnectionString. Baking
# "Data Source=/data/pos.db" meant that a compose file which forgot Database__Provider ran the
# whole company on a file inside the container while the nightly backup faithfully copied an
# empty server database — and nothing failed anywhere. Every deployment now names its own database,
# and outside Development the API refuses Sqlite unless Database__AllowSqliteInProduction says
# a file database really is what was meant.
# The files folder (logos, contract scans) sits on the same volume: the database keeps only paths.
ENV ASPNETCORE_URLS=http://0.0.0.0:5080 \
    DataProtection__KeysPath=/data/keys \
    Files__Root=/data/files
EXPOSE 5080
# start-period covers the bounded wait for SQL Server plus the migrations. After that, a database
# that stops answering turns the container unhealthy, which is what `docker compose ps` and any
# external monitor read.
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5080/health || exit 1
ENTRYPOINT ["dotnet", "AlgeriaPos.API.dll"]
