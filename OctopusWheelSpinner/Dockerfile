FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["OctopusWheelSpinner/OctopusWheelSpinner.csproj", "OctopusWheelSpinner/"]
RUN dotnet restore "OctopusWheelSpinner/OctopusWheelSpinner.csproj"
COPY . .
WORKDIR "/src/OctopusWheelSpinner"
RUN dotnet build "./OctopusWheelSpinner.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./OctopusWheelSpinner.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "OctopusWheelSpinner.dll"]
