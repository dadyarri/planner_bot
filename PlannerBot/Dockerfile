FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER root
WORKDIR /app
RUN apt install libkrb5-3

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PlannerBot/PlannerBot.csproj", "PlannerBot/"]
RUN dotnet restore "PlannerBot/PlannerBot.csproj"
COPY . .
WORKDIR "/src/PlannerBot"
RUN dotnet build "./PlannerBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PlannerBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
USER $APP_UID
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PlannerBot.dll"]
