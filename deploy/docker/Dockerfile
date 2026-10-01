# syntax=docker/dockerfile:1
#
# One Dockerfile for all DataHub images; pick the app with --target (web, api, worker, migrator).
# Build from the repository root:
#   docker build -f deploy/docker/Dockerfile --target web --build-arg VERSION=1.0.0 -t datahub-web:1.0.0 .
# scripts/build-images.ps1 builds all four.

ARG DOTNET_VERSION=10.0

# ---- build: restore and publish every app once; the app targets below only copy their output ----
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG VERSION=0.0.0-local
WORKDIR /src

# Project files first, so the restore layer is reused while only code changes.
COPY src/DataHub.Domain/DataHub.Domain.csproj                 src/DataHub.Domain/
COPY src/DataHub.Oris/DataHub.Oris.csproj                     src/DataHub.Oris/
COPY src/DataHub.Application/DataHub.Application.csproj       src/DataHub.Application/
COPY src/DataHub.Infrastructure/DataHub.Infrastructure.csproj src/DataHub.Infrastructure/
COPY src/DataHub.Hosting/DataHub.Hosting.csproj               src/DataHub.Hosting/
COPY src/DataHub.Web/DataHub.Web.csproj                       src/DataHub.Web/
COPY src/DataHub.Api/DataHub.Api.csproj                       src/DataHub.Api/
COPY src/DataHub.Worker/DataHub.Worker.csproj                 src/DataHub.Worker/
COPY src/DataHub.Migrator/DataHub.Migrator.csproj             src/DataHub.Migrator/
RUN for p in Web Api Worker Migrator; do dotnet restore src/DataHub.$p/DataHub.$p.csproj; done

COPY src/ src/
RUN for p in Web Api Worker Migrator; do \
      dotnet publish src/DataHub.$p/DataHub.$p.csproj -c Release --no-restore -o /out/$p \
        -p:Version=$VERSION -p:UseAppHost=false || exit 1; \
    done

# ---- runtime base: chiseled Ubuntu (no shell, no package manager), non-root ----
# "-extra" adds ICU and time zone data (SqlClient and culture-aware formatting need ICU).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    # Keep the GC heap at 75% (hex 4B) of the container memory limit, leaving room for native memory.
    DOTNET_GCHeapHardLimitPercent=4B \
    TMPDIR=/tmp
# The image's non-root "app" user, by number so Kubernetes runAsNonRoot can verify it.
USER 1654
EXPOSE 8080

FROM runtime AS web
COPY --from=build /out/Web .
ENTRYPOINT ["dotnet", "DataHub.Web.dll"]

FROM runtime AS api
COPY --from=build /out/Api .
ENTRYPOINT ["dotnet", "DataHub.Api.dll"]

FROM runtime AS worker
COPY --from=build /out/Worker .
ENTRYPOINT ["dotnet", "DataHub.Worker.dll"]

FROM runtime AS migrator
COPY --from=build /out/Migrator .
ENTRYPOINT ["dotnet", "DataHub.Migrator.dll"]
