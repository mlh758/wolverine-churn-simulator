FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG WOLVERINE_VERSION=5.39.0
WORKDIR /src
COPY nuget.config .
COPY localfeed ./localfeed
COPY src/ChurnSim/ChurnSim.csproj .
RUN dotnet restore -p:WolverineVersion=$WOLVERINE_VERSION
COPY src/ChurnSim/ .
RUN dotnet publish -c Release -o /app --no-restore -p:WolverineVersion=$WOLVERINE_VERSION

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ChurnSim.dll"]
