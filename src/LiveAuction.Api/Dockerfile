# syntax=docker/dockerfile:1
# Multi-stage image: built with the SDK, run with the runtime only (smaller final image
# without build tools). Build context: repository root.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Restore packages copying ONLY the project files: this layer is cached and is not
#    repeated unless the dependencies change.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/LiveAuction.Domain/LiveAuction.Domain.csproj src/LiveAuction.Domain/
COPY src/LiveAuction.Application/LiveAuction.Application.csproj src/LiveAuction.Application/
COPY src/LiveAuction.Infrastructure/LiveAuction.Infrastructure.csproj src/LiveAuction.Infrastructure/
COPY src/LiveAuction.Api/LiveAuction.Api.csproj src/LiveAuction.Api/
RUN dotnet restore src/LiveAuction.Api/LiveAuction.Api.csproj

# 2) Build and publish.
COPY src/ src/
RUN dotnet publish src/LiveAuction.Api/LiveAuction.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user included in the official .NET images.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "LiveAuction.Api.dll"]
