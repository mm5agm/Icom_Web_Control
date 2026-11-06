# Icom Web Control, radio control only, on Linux (amd64 and arm64).
# For a shack PC or Raspberry Pi as the always-on controller. Voice, the CW and
# RTTY audio pages and Remote Audio are Windows only for now and aren't in this image.
# From Yaesu Web Control's Dockerfile.
#
# Build (current arch):
#   docker build -t iwc:local .
#
# Run: see docker-compose.yml

# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

# Build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG TARGETARCH
WORKDIR /src

# Only the project files, so the restore layer stays cached across source edits.
# core/ is a ProjectReference, so its .csproj has to be here too: restore does not
# fail on a missing referenced project, the publish below does, with NETSDK1004.
COPY Icom_Web_Control.csproj ./
COPY core/RadioWebControl.Core.csproj core/
RUN arch="$TARGETARCH"; \
    if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet restore Icom_Web_Control.csproj -r "linux-$arch"

COPY . .
# The radio-control target only.
RUN arch="$TARGETARCH"; \
    if [ "$arch" = "amd64" ]; then arch=x64; fi; \
    dotnet publish Icom_Web_Control.csproj \
      -c Release \
      -f net10.0 \
      -r "linux-$arch" \
      --self-contained false \
      --no-restore \
      -o /app/publish \
      /p:UseAppHost=true \
    && test -f /app/publish/wwwroot/favicon.ico \
    && test -f /app/publish/USER_MANUAL.md

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app

# System.IO.Ports on Linux talks to USB serial via libudev.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libudev1 \
    && rm -rf /var/lib/apt/lists/*

# Settings and logs live under XDG_CONFIG_HOME (SpecialFolder.ApplicationData).
# The official aspnet images ship a non-root `app` user.
ENV DOTNET_RUNNING_IN_CONTAINER=true \
    ASPNETCORE_URLS= \
    XDG_CONFIG_HOME=/data \
    HOME=/home/app \
    TZ=UTC

# compose also adds the host's dialout group, so /dev/ttyUSB* permissions match.
RUN usermod -aG dialout app \
    && mkdir -p /data \
    && chown -R app:app /data /home/app

COPY --from=build /app/publish .
COPY docker/entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh \
    && chown -R app:app /app

# Stay root for ENTRYPOINT so a bind-mounted data folder can be chown'd, then the
# entrypoint drops to `app` via setpriv.
USER root
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["/entrypoint.sh"]
