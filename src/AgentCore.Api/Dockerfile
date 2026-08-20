# The image a deployment runs. Build it from the repository root:
#   docker build -f src/AgentCore.Api/Dockerfile -t agentcore .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/

# SkipClientAppBuild uses the wwwroot committed under src/AgentCore.Chat, so this stage needs no
# Node. See the comment on that ItemGroup in AgentCore.Chat.csproj.
RUN dotnet restore src/AgentCore.Api/AgentCore.Api.csproj
RUN dotnet publish src/AgentCore.Api/AgentCore.Api.csproj \
        --no-restore \
        -c Release \
        -p:SkipClientAppBuild=true \
        -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app ./

# The document lives outside the image: a deployment mounts its own at this path. Nothing mounted
# is a startup failure and not a silent default, because the wrong agent answering a real phone
# line is worse than no agent answering it. The annotated example ships beside it as config/example.yaml.
ENV AgentCore__ConfigurationPath=/app/config/config.yaml \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080

# APP_UID comes from the base image. A mounted document must be world-readable to reach this user.
USER $APP_UID

ENTRYPOINT ["dotnet", "AgentCore.Api.dll"]
