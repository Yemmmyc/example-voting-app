# because of dotnet, we always build on amd64, and target platforms in cli
# dotnet doesn't support QEMU for building or running.
# (errors common in arm/v7 32bit) https://github.com/dotnet/dotnet-docker/issues/1537
# https://hub.docker.com/_/microsoft-dotnet
# hadolint ignore=DL3029
# to build for a different platform than your host, use --platform=<platform>
# for example, if you were on Intel (amd64) and wanted to build for ARM, you would use:
# docker buildx build --platform "linux/arm64/v8" .

# build compiles the program for the builder's local platform
ARG BUILDPLATFORM=linux/amd64
FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETPLATFORM=linux/amd64
ARG TARGETARCH=amd64
ARG BUILDPLATFORM=linux/amd64
RUN echo "I am running on $BUILDPLATFORM, building for $TARGETPLATFORM"

WORKDIR /source
COPY *.csproj .
RUN case "${TARGETARCH:-amd64}" in \
        "amd64"|"x64") ARCH=x64 ;; \
        "arm64") ARCH=arm64 ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet restore -a $ARCH

COPY . .
RUN case "${TARGETARCH:-amd64}" in \
        "amd64"|"x64") ARCH=x64 ;; \
        "arm64") ARCH=arm64 ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" >&2; exit 1 ;; \
    esac && \
    dotnet publish -c release -o /app -a $ARCH --self-contained false --no-restore

# app image
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app

# Create a non-root system user and group using standard Linux groupadd/useradd
RUN groupadd --system --gid 10001 appgroup && \
    useradd --system --uid 10001 --gid appgroup --create-home appuser

# Copy built binaries with non-root ownership
COPY --chown=appuser:appgroup --from=build /app .

# Switch to non-root user
USER appuser

ENTRYPOINT ["dotnet", "Worker.dll"]
