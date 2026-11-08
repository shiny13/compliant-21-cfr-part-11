# syntax=docker/dockerfile:1
# RecordSignatures.Api: electronic signatures (21 CFR 11.50, 11.70, 11.100, 11.200, 11.300)
# Build context MUST be the repository root (the service references AuthenticationCore.Shared):
#   docker build -f RecordSignatures/Dockerfile -t part11/record-signatures .
# or: scripts/build-images.sh

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so package downloads are cached until a project file changes.
COPY AuthenticationCore/src/Directory.Build.props AuthenticationCore/src/
COPY AuthenticationCore/src/AuthenticationCore.Shared/AuthenticationCore.Shared.csproj AuthenticationCore/src/AuthenticationCore.Shared/
COPY RecordSignatures/Directory.Build.props RecordSignatures/
COPY RecordSignatures/src/RecordSignatures.Core/RecordSignatures.Core.csproj RecordSignatures/src/RecordSignatures.Core/
COPY RecordSignatures/src/RecordSignatures.Database/RecordSignatures.Database.csproj RecordSignatures/src/RecordSignatures.Database/
COPY RecordSignatures/src/RecordSignatures.Api/RecordSignatures.Api.csproj RecordSignatures/src/RecordSignatures.Api/
RUN dotnet restore RecordSignatures/src/RecordSignatures.Api/RecordSignatures.Api.csproj

COPY AuthenticationCore/src/ AuthenticationCore/src/
COPY RecordSignatures/src/ RecordSignatures/src/
RUN dotnet publish RecordSignatures/src/RecordSignatures.Api/RecordSignatures.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# Chiseled runtime image: no shell, no package manager, non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "RecordSignatures.Api.dll"]
