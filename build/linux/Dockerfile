# CI's Linux test environment (ubuntu-24.04 runner, x86_64) for running the unit, editor and lavapipe render tests
# locally: `just test-linux` / `just render-tests-linux` (build/linux/run.sh, docs/design/testing.md#linux-tests-in-docker).
# Same apt packages as .github/workflows/ci.yml (render-tests + build-test gettext); .NET SDK pinned by global.json.
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/usr/share/dotnet \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl git rsync libicu74 gettext \
      mesa-vulkan-drivers vulkan-validationlayers libvulkan1 vulkan-tools xvfb xauth \
      libx11-6 libx11-xcb1 libxext6 libxfixes3 libxrandr2 libxinerama1 libxcursor1 libxi6 libxss1 libxkbcommon0 \
 && rm -rf /var/lib/apt/lists/*

COPY global.json /tmp/global.json
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --jsonfile /tmp/global.json --install-dir "$DOTNET_ROOT" \
 && ln -s "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet \
 && rm /tmp/dotnet-install.sh \
 && dotnet --version

# What the CI render-tests job sets: lavapipe only, SDL on X11 (Xvfb), no sound device; CI=true makes a missing
# validation layer a failure instead of a skip.
ENV VK_DRIVER_FILES=/usr/share/vulkan/icd.d/lvp_icd.json \
    VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
    SDL_VIDEODRIVER=x11 \
    SDL_AUDIODRIVER=dummy \
    CI=true \
    NUGET_PACKAGES=/cache/nuget \
    DOTNET_EnableWriteXorExecute=0
# DOTNET_EnableWriteXorExecute=0: under Docker Desktop's x86_64 emulation (Rosetta) the runtime's W^X double mapping
# made the parallel unit-test process stall (every thread idle) after ~190 tests in most runs; with it off the stall
# is rare (inside.sh retries an aborted run once). Native x86_64 hosts (CI) need neither.

WORKDIR /work
