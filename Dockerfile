# EasyMulti relay — multi-stage Docker build.
#
# 正常情况下不用手动构建：推代码到 GitHub，Actions 会跑测试、构建镜像并推到
# ghcr.io，服务器只 docker pull（见 docs/DEPLOY.md）。要本地构建就：
#   docker build -t easymulti .
#   docker run -d -p 7777:7777/tcp -p 7777:7777/udp -e EASYMULTI_TOKEN=xxx easymulti
#
# The relay reads its config from env vars (see docs/DEPLOY.md), so no config file
# is needed inside the container.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/EasyMulti.Relay/EasyMulti.Relay.csproj -c Release -o /app/publish /p:UseAppHost=false

# alpine 变体：最终镜像小一半（~110MB vs ~200MB）。发布的是可移植 IL，
# 所以构建阶段用 glibc 的 SDK、运行阶段用 musl 的 runtime 没有问题。
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# WebSocket (TCP) and UDP share port 7777 by default — different protocol stacks.
EXPOSE 7777/tcp
EXPOSE 7777/udp

ENTRYPOINT ["dotnet", "easymulti-relay.dll"]
