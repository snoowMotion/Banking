# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY BudgetTenable.sln Directory.Build.props ./
COPY src/BudgetTenable.Core/BudgetTenable.Core.csproj src/BudgetTenable.Core/
COPY src/BudgetTenable.Api/BudgetTenable.Api.csproj src/BudgetTenable.Api/
COPY tests/BudgetTenable.Tests/BudgetTenable.Tests.csproj tests/BudgetTenable.Tests/
RUN dotnet restore
COPY . .
RUN dotnet publish src/BudgetTenable.Api -c Release -o /app --no-restore

# Image Debian : elle embarque tzdata (calculs de période en Europe/Paris).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
COPY web ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080 \
    BUDGET_DATA_DIR=/data \
    DOTNET_gcServer=0
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "BudgetTenable.Api.dll"]
