# ── Stage 1: build NexusAgent ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + project files first for layer-cached restore
COPY NexusAgent/NexusAgent.sln ./NexusAgent/
COPY NexusAgent/NexusAgent.Cli/NexusAgent.Cli.csproj          ./NexusAgent/NexusAgent.Cli/
COPY NexusAgent/NexusAgent.Core/NexusAgent.Core.csproj         ./NexusAgent/NexusAgent.Core/
COPY NexusAgent/NexusAgent.Tests/NexusAgent.Tests.csproj       ./NexusAgent/NexusAgent.Tests/
COPY NexusAgent/NexusAgent.VerifiedParts/NexusAgent.VerifiedParts.csproj ./NexusAgent/NexusAgent.VerifiedParts/
COPY NexusAgent/NexusAgent.MathlibIngestor/NexusAgent.MathlibIngestor.csproj ./NexusAgent/NexusAgent.MathlibIngestor/
RUN dotnet restore NexusAgent/NexusAgent.sln

# Copy remaining source and publish
COPY NexusAgent/ ./NexusAgent/
RUN dotnet publish NexusAgent/NexusAgent.Cli/NexusAgent.Cli.csproj \
      -c Release \
      --no-restore \
      -o /app/publish

# ── Stage 2: Lean 4 toolchain + .NET runtime ─────────────────────────────────
# We install elan (the Lean version manager) so the agent can call `lake env lean`
# against a mounted formal-conjectures project with pre-built oleans.
FROM mcr.microsoft.com/dotnet/runtime:10.0

# System deps for elan + lake
RUN apt-get update && apt-get install -y --no-install-recommends \
      curl ca-certificates git \
    && rm -rf /var/lib/apt/lists/*

# Install elan (Lean version manager) for the nexus user
RUN useradd -ms /bin/bash nexus
USER nexus
ENV HOME=/home/nexus PATH="/home/nexus/.elan/bin:$PATH"
RUN curl -sSf https://raw.githubusercontent.com/leanprover/elan/master/elan-init.sh \
      | sh -s -- -y --no-modify-path

# Pre-install the toolchain version used by formal-conjectures.
# When the user mounts their formal-conjectures directory, lake will detect
# the lean-toolchain file and use this cached toolchain automatically.
RUN /home/nexus/.elan/bin/elan toolchain install leanprover/lean4:v4.27.0 \
    && /home/nexus/.elan/bin/elan toolchain install leanprover/lean4:v4.29.1

USER root
WORKDIR /app
COPY --from=build /app/publish .
# Ensure the nexus user owns the app
RUN chown -R nexus:nexus /app

USER nexus

# Writable scratch space for _nexus_tmp Lean files
VOLUME /home/nexus/.cache
VOLUME /data

ENTRYPOINT ["dotnet", "/app/nexus.dll"]
