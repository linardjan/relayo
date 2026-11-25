# Тестовый сайт. Собирается из корня репозитория:
#   docker compose up -d test-site
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY tools/TestSite/TestSite.csproj tools/TestSite/
RUN dotnet restore tools/TestSite/TestSite.csproj
COPY tools/TestSite/ tools/TestSite/
RUN dotnet publish tools/TestSite/TestSite.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER app
ENTRYPOINT ["dotnet", "TestSite.dll"]
