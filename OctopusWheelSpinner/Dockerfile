FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
# curl is used by the HEALTHCHECK
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0
ARG INFORMATIONAL_VERSION=0.0.0
WORKDIR /src
COPY ["OctopusWheelSpinner/OctopusWheelSpinner.csproj", "OctopusWheelSpinner/"]
RUN dotnet restore "OctopusWheelSpinner/OctopusWheelSpinner.csproj"
COPY . .
WORKDIR "/src/OctopusWheelSpinner"
RUN dotnet build "./OctopusWheelSpinner.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:Version=$VERSION /p:InformationalVersion=$INFORMATIONAL_VERSION

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0
ARG INFORMATIONAL_VERSION=0.0.0
RUN dotnet publish "./OctopusWheelSpinner.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:Version=$VERSION /p:InformationalVersion=$INFORMATIONAL_VERSION

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
  CMD curl --fail --silent --show-error http://localhost:${HEALTH_PORT:-8080}/health || exit 1
ENTRYPOINT ["dotnet", "OctopusWheelSpinner.dll"]
