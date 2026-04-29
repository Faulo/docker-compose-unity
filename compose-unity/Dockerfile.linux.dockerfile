FROM debian:bookworm-slim

ARG DEBIAN_FRONTEND=noninteractive

USER root

COPY machine-id /etc/machine-id

WORKDIR /var/workspace

# Base tools + locale
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        git-lfs \
        gnupg \
        locales \
        nano \
        pciutils \
        wget \
        zip \
        unzip \
        xz-utils \
        libxfixes3 \
        libxi6 \
        libxxf86vm1 \
        libxrender1 \
        libxkbcommon0 \
        libsm6 \
        libice6 \
        libgl1 \
        libegl1 \
        libdbus-1-3 \
        libfontconfig1 \
        libfreetype6 && \
    echo "$LANG UTF-8" > /etc/locale.gen && \
    locale-gen "$LANG" && \
    install -m 0755 -d /etc/apt/keyrings && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    git config --global --add safe.directory * && \
    curl --version

# NodeJS 22
RUN curl -fsSL "https://deb.nodesource.com/setup_22.x" | bash - && \
    apt-get install -y --no-install-recommends nodejs && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
	npm --version

# itch.io
RUN curl -fsSL "https://broth.itch.zone/butler/linux-amd64/LATEST/archive/default" -o /tmp/butler.zip && \
    unzip /tmp/butler.zip -d /tmp/butler && \
    chmod +x /tmp/butler/butler && \
    mv /tmp/butler/butler /usr/local/bin/butler && \
    rm -rf /tmp/butler /tmp/butler.zip && \
    butler --help

# Steam SDK
RUN dpkg --add-architecture i386 && \
    apt-get update && \
    apt-get install -y --no-install-recommends lib32gcc-s1 && \
    curl -fsSL "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz" -o /tmp/steamcmd.tar.gz && \
    tar -xzf /tmp/steamcmd.tar.gz -C /usr/local/bin && \
    ln -sf /usr/local/bin/steamcmd.sh /usr/local/bin/steamcmd && \
    rm -f /tmp/steamcmd.tar.gz && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \	
    steamcmd +quit

# PHP
ENV PHP_VERSION=8.2
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        php \
        php-curl \
        php-exif \
        php-imap \
        php-intl \
        php-mbstring \
        php-sockets \
        php-xml \
        php-zip && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
	php --version

# Farah
ENV COMPOSE_UNITY="composer -d /var/unity"
ENV COMPOSER_ALLOW_SUPERUSER="1"

ENV UNITY_LOGGING="stdin stdout stderr"
ENV UNITY_ACCELERATOR_ENDPOINT=""
ENV UNITY_NO_GRAPHICS="1"

COPY --chmod=755 --from=composer:2 /usr/bin/composer /usr/local/bin/composer
COPY --chmod=755 unity/composer.json /var/unity/
COPY --chmod=755 unity/config /var/unity/config/
COPY --chmod=755 unity/compose-unity /usr/local/bin/

RUN compose-unity update --no-dev && \
	compose-unity exec unity-build

# .NET SDK
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV DOTNET_CLI_UI_LANGUAGE=en
ENV PATH="/root/.dotnet/tools:${PATH}"
RUN curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb && \
    dpkg -i /tmp/packages-microsoft-prod.deb && \
    rm /tmp/packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y --no-install-recommends dotnet-sdk-8.0 && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    dotnet --version

# DocFX
ENV FrameworkPathOverride="/usr/lib/mono/4.7.1-api/"
RUN apt-get update && \
    apt-get install -y --no-install-recommends mono-devel && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
    dotnet tool update -g docfx

# Blender
ARG BLENDER_SERIES=4.5

RUN set -eux; \
    arch="$(dpkg --print-architecture)"; \
    case "$arch" in \
      amd64) blender_arch="x64" ;; \
      arm64) blender_arch="arm64" ;; \
      *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; \
    esac; \
    base_url="https://download.blender.org/release/Blender${BLENDER_SERIES}/"; \
    version="$(curl -fsSL "$base_url" \
      | grep -oE "blender-${BLENDER_SERIES}\.[0-9]+-linux-${blender_arch}\.tar\.xz" \
      | sed -E "s/^blender-([0-9]+\.[0-9]+\.[0-9]+)-linux-.*$/\1/" \
      | sort -V \
      | tail -n1)"; \
    test -n "$version"; \
    curl -fsSL "${base_url}blender-${version}-linux-${blender_arch}.tar.xz" -o /tmp/blender.tar.xz; \
    rm -rf /opt/blender-*; \
    tar -xJf /tmp/blender.tar.xz -C /opt; \
    ln -sfn "/opt/blender-${version}-linux-${blender_arch}/blender" /usr/local/bin/blender; \
    rm -f /tmp/blender.tar.xz; \
    blender --version

# Unity Hub
RUN install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://hub.unity3d.com/linux/keys/public | gpg --dearmor -o /etc/apt/keyrings/unityhub.gpg && \
    chmod a+r /etc/apt/keyrings/unityhub.gpg && \
    echo "deb [arch=amd64 signed-by=/etc/apt/keyrings/unityhub.gpg] https://hub.unity3d.com/linux/repos/deb stable main" \
        > /etc/apt/sources.list.d/unityhub.list && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        apparmor \
        cpio \
        ffmpeg \
        libasound2 \
        libc6-dev \
        libgbm-dev \
        libgconf-2-4 \
        libglu1-mesa \
        libncurses5 \
        libtinfo5 \
        p7zip-full \
        python3 \
        unityhub=3.12.1 \
        xvfb \
        xz-utils && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Unity 2021 OpenSSL compatibility
RUN curl -fsSL https://security.debian.org/debian-security/pool/updates/main/o/openssl/libssl1.1_1.1.1w-0+deb11u5_amd64.deb -o /tmp/libssl1.1.deb && \
    apt install -y /tmp/libssl1.1.deb && \
    rm /tmp/libssl1.1.deb

# VNC Server
ENV USER="root"
ENV RESOLUTION="1920x1080"
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        dbus-x11 \
        tightvncserver \
        xfce4 \
        xfce4-goodies \
        xfonts-base && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

# Test
RUN compose-unity exec unity-help
