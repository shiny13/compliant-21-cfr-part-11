# syntax=docker/dockerfile:1
# AuthenticationCore.Service: login, tokens, revocation (21 CFR 11.10(d), 11.200, 11.300)
# Build context MUST be the repository root (the service references AuthenticationCore.Shared):
#   docker build -f AuthenticationCore/Dockerfile -t part11/authentication-core .
# or: scripts/build-images.sh

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, so package downloads are cached until a project file changes.
COPY AuthenticationCore/src/Directory.Build.props AuthenticationCore/src/
COPY AuthenticationCore/src/AuthenticationCore.Shared/AuthenticationCore.Shared.csproj AuthenticationCore/src/AuthenticationCore.Shared/
COPY AuthenticationCore/src/AuthenticationCore.Service/AuthenticationCore.Service.csproj AuthenticationCore/src/AuthenticationCore.Service/
RUN dotnet restore AuthenticationCore/src/AuthenticationCore.Service/AuthenticationCore.Service.csproj

COPY AuthenticationCore/ AuthenticationCore/
RUN dotnet publish AuthenticationCore/src/AuthenticationCore.Service/AuthenticationCore.Service.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# Chiseled runtime image: no shell, no package manager, non-root user.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "AuthenticationCore.Service.dll"]
