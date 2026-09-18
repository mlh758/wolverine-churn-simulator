FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG WOLVERINE_VERSION=5.39.0
# 'postgres' or 'ravendb' -- selects the message store package AND the backend wiring file
# (see ChurnSim.csproj). The image is single-backend by construction; k8s/churnsim*.yaml
# declares the matching SIM_BACKEND and ChurnSim refuses to start on a mismatch.
ARG SIM_BACKEND=postgres
WORKDIR /src
COPY nuget.config .
COPY localfeed ./localfeed
COPY src/ChurnSim/ChurnSim.csproj .
RUN dotnet restore -p:WolverineVersion=$WOLVERINE_VERSION -p:SimBackend=$SIM_BACKEND
COPY src/ChurnSim/ .
RUN dotnet publish -c Release -o /app --no-restore \
    -p:WolverineVersion=$WOLVERINE_VERSION -p:SimBackend=$SIM_BACKEND

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ChurnSim.dll"]
