# syntax=docker/dockerfile:1
# RecordAuditTrails.Api: records and the 21 CFR 11.10(e) audit trail
# Build context MUST be the repository root (the service references AuthenticationCore.Shared):
#   docker build -f RecordAuditTrails/Dockerfile -t part11/record-audit-trails .
# or: scripts/build-images.sh

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so package downloads are cached until a project file changes.
COPY AuthenticationCore/src/Directory.Build.props AuthenticationCore/src/
COPY AuthenticationCore/src/AuthenticationCore.Shared/AuthenticationCore.Shared.csproj AuthenticationCore/src/AuthenticationCore.Shared/
COPY RecordAuditTrails/Directory.Build.props RecordAuditTrails/
COPY RecordAuditTrails/src/RecordAuditTrails.Models/RecordAuditTrails.Models.csproj RecordAuditTrails/src/RecordAuditTrails.Models/
COPY RecordAuditTrails/src/RecordAuditTrails.Core/RecordAuditTrails.Core.csproj RecordAuditTrails/src/RecordAuditTrails.Core/
COPY RecordAuditTrails/src/RecordAuditTrails.Database/RecordAuditTrails.Database.csproj RecordAuditTrails/src/RecordAuditTrails.Database/
COPY RecordAuditTrails/src/RecordAuditTrails.Services/RecordAuditTrails.Services.csproj RecordAuditTrails/src/RecordAuditTrails.Services/
COPY RecordAuditTrails/src/RecordAuditTrails.Api/RecordAuditTrails.Api.csproj RecordAuditTrails/src/RecordAuditTrails.Api/
RUN dotnet restore RecordAuditTrails/src/RecordAuditTrails.Api/RecordAuditTrails.Api.csproj

COPY AuthenticationCore/src/ AuthenticationCore/src/
COPY RecordAuditTrails/src/ RecordAuditTrails/src/
RUN dotnet publish RecordAuditTrails/src/RecordAuditTrails.Api/RecordAuditTrails.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# Chiseled runtime image: no shell, no package manager, non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "RecordAuditTrails.Api.dll"]
