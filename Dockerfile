# EasyMulti relay — multi-stage Docker build.
#
# Build:  docker build -t easymulti .
# Run:    docker run -d -p 7777:7777/tcp -p 7777:7777/udp #             -e EASYMULTI_TOKEN=your-secret-token easymulti
#
# The relay reads its config from env vars (see docs/DEPLOY.md), so no config file
# is needed inside the container.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/EasyMulti.Relay/EasyMulti.Relay.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# WebSocket (TCP) and UDP share port 7777 by default — different protocol stacks.
EXPOSE 7777/tcp
EXPOSE 7777/udp

ENTRYPOINT ["dotnet", "easymulti-relay.dll"]
