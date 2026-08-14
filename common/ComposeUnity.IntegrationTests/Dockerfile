FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /source
COPY common/ComposeUnity ./ComposeUnity
RUN dotnet publish ComposeUnity/ComposeUnity.csproj \
        --nologo \
        --configuration Release \
        --runtime linux-x64 \
        --output /publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim
COPY --chmod=755 common/ComposeUnity.IntegrationTests/composer /usr/local/bin/composer
COPY --from=build /publish/compose-unity /usr/local/bin/compose-unity
EXPOSE 8080
HEALTHCHECK --interval=1s --timeout=5s --start-period=1s --retries=30 \
    CMD ["compose-unity", "sidecar", "health"]
CMD ["compose-unity", "sidecar"]
