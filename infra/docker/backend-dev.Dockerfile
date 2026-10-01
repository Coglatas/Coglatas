FROM mcr.microsoft.com/dotnet/sdk:10.0.401@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29

WORKDIR /workspace

ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=true \
    NUGET_PACKAGES=/root/.nuget/packages \
    BACKEND_USE_WATCH=false

EXPOSE 8080

CMD ["bash", "-lc", "mkdir -p src/Coglatas.Web/wwwroot && dotnet tool restore && dotnet restore src/Coglatas.Web/Coglatas.Web.csproj /p:RestoreFallbackFolders= && dotnet ef database update --project src/Coglatas.Infrastructure --startup-project src/Coglatas.Web && if [ \"${BACKEND_USE_WATCH}\" = \"true\" ]; then dotnet watch --project src/Coglatas.Web/Coglatas.Web.csproj run --no-launch-profile; else dotnet run --project src/Coglatas.Web/Coglatas.Web.csproj --no-launch-profile; fi"]
