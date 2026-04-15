FROM debian:bookworm-slim

ARG DEBIAN_FRONTEND=noninteractive

USER root

COPY machine-id /etc/machine-id

WORKDIR /var/workspace

# Base tools + locale
ENV LANG=C.UTF-8
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    git \
    gnupg \
    locales \
    nano \
    pciutils \
    wget \
    zip \
    unzip && \
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
COPY --chmod=755 --from=composer:2 /usr/bin/composer /usr/local/bin/composer
COPY --chmod=755 unity/composer.json /var/unity/
COPY --chmod=755 unity/config /var/unity/config/
COPY --chmod=755 unity/compose-unity /usr/local/bin/
RUN compose-unity update --no-dev && \
	compose-unity exec unity-build

# .NET SDK
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1"
ENV DOTNET_CLI_UI_LANGUAGE="en"
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
RUN apt-get update && \
    apt-get install -y --no-install-recommends blender && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* && \
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
RUN curl -fsSL http://archive.ubuntu.com/ubuntu/pool/main/o/openssl/libssl1.1_1.1.1f-1ubuntu2_amd64.deb -o /tmp/libssl1.1.deb && \
    dpkg -i /tmp/libssl1.1.deb && \
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
