# nb as a container layer. Build it once, then copy /opt/nb onto whatever base a
# fixture needs:
#
#   podman build -t nb .                      # or: docker build -f Containerfile -t nb .
#
#   FROM localhost/nb:latest AS nb
#   FROM mcr.microsoft.com/dotnet/sdk:10.0
#   COPY --from=nb /opt/nb /opt/nb
#   ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
#
# The image is also runnable on its own (`/opt/nb/nb --version`, a Mock program with
# a config on a mount), which is how a build is smoke-tested. See docs/distribution.md
# and docs/containers.md.
#
# What ends up in /opt/nb is the distribution publish and nothing else: the
# self-contained linux-x64 binary, providers/, appsettings.example.json. The
# developer's appsettings.json and mcp.json never enter the build context
# (.dockerignore is an allowlist), so they cannot reach a layer.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# nb.csproj alone, never the solution: nb's own publish targets build the provider
# plugins and collect them into providers/ (docs/distribution.md).
RUN dotnet publish nb.csproj -c Release -r linux-x64 --self-contained -o /opt/nb

# runtime-deps carries the native libraries a self-contained .NET binary needs and no
# runtime of its own, which is right for a binary that brings one.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
COPY --from=build /opt/nb /opt/nb
# A consumer's COPY --from does not inherit this ENV; its Containerfile sets it again
# (or passes -e). Without it self-contained .NET aborts at startup on a base with no
# libicu (bugs/Publishing_Nb_Into_A_Container_Is_Undocumented.md §4).
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
ENV NO_COLOR=1
ENTRYPOINT ["/opt/nb/nb"]
