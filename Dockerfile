FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TodoSync.Api/TodoSync.Api.csproj TodoSync.Api/
RUN dotnet restore TodoSync.Api/TodoSync.Api.csproj
COPY TodoSync.Api/ TodoSync.Api/
RUN dotnet publish TodoSync.Api/TodoSync.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_ENVIRONMENT=Development
EXPOSE 3000
ENTRYPOINT ["dotnet", "TodoSync.Api.dll"]
