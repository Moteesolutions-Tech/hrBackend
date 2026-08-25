# Build from the repository's backend/ directory:
#
#     docker build -t motee-api .
#     docker run -p 8080:8080 -e ConnectionStrings__Default="..." motee-api
#
# Two stages so the SDK — roughly 1GB of compilers and analyzers — never reaches the
# image that runs in production.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first, and nothing else. Docker caches each layer, so restore is only
# repeated when a project file or dependency actually changes — not on every edit to
# a .cs file. Directory.Build.props belongs here too: it carries the analyzer package
# reference and the target framework, so restore needs it.
COPY Directory.Build.props ./
COPY Motee.slnx ./
COPY src/Motee.Domain/Motee.Domain.csproj src/Motee.Domain/
COPY src/Motee.Application/Motee.Application.csproj src/Motee.Application/
COPY src/Motee.Infrastructure/Motee.Infrastructure.csproj src/Motee.Infrastructure/
COPY src/Motee.Api/Motee.Api.csproj src/Motee.Api/

RUN dotnet restore src/Motee.Api/Motee.Api.csproj

# .editorconfig sets the analyzer severities, including the one that makes `var` a
# build error. Without it the image would build code the local build rejects.
COPY .editorconfig ./
COPY src/ src/

RUN dotnet publish src/Motee.Api/Motee.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql probes for GSSAPI during authentication negotiation. The runtime image does not
# ship the Kerberos library, so every start logs "Cannot load library
# libgssapi_krb5.so.2" before the logger is even configured. Connections work regardless
# - it falls back - but the line looks like a failure at exactly the moment someone is
# checking whether a deploy came up cleanly. Installing it costs a few hundred KB.
#
# curl is here for the compose health check, which otherwise has nothing to run.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./

# The base image ships a non-root user. Running as root inside a container is a
# needless second chance for anything that escapes the process.
USER $APP_UID

EXPOSE 8080

# Hosts disagree about how they tell an app which port to use: Render and Cloud Run
# set PORT, others expect a fixed one. Reading $PORT with a default covers both.
#
# exec so that dotnet becomes PID 1 and receives SIGTERM directly — otherwise the
# shell swallows it and the platform kills the container mid-request instead of
# letting it drain.
ENTRYPOINT ["/bin/sh", "-c", "ASPNETCORE_HTTP_PORTS=${PORT:-8080} exec dotnet Motee.Api.dll"]
