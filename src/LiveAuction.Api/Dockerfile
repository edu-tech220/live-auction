# syntax=docker/dockerfile:1
# Imagen multi-etapa: se compila con el SDK y se ejecuta solo con el runtime (imagen final
# más pequeña y sin herramientas de compilación). Contexto de build: raíz del repositorio.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 1) Restaurar paquetes copiando SOLO los archivos de proyecto: esta capa se cachea y no se
#    repite mientras no cambien las dependencias.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/LiveAuction.Domain/LiveAuction.Domain.csproj src/LiveAuction.Domain/
COPY src/LiveAuction.Application/LiveAuction.Application.csproj src/LiveAuction.Application/
COPY src/LiveAuction.Infrastructure/LiveAuction.Infrastructure.csproj src/LiveAuction.Infrastructure/
COPY src/LiveAuction.Api/LiveAuction.Api.csproj src/LiveAuction.Api/
RUN dotnet restore src/LiveAuction.Api/LiveAuction.Api.csproj

# 2) Compilar y publicar.
COPY src/ src/
RUN dotnet publish src/LiveAuction.Api/LiveAuction.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Usuario sin privilegios incluido en las imágenes oficiales de .NET.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "LiveAuction.Api.dll"]
