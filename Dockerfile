# syntax=docker/dockerfile:1

# ---------------------------------------------------------------------------
# Build stage
# ---------------------------------------------------------------------------
# The build runs on the builder's own platform: the published app is portable, so an arm64 image needs no emulation here.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG Channel=local
ARG BuildNumber=0
ARG BuildDate=
WORKDIR /src

# Restore first (better layer caching).
COPY ["global.json", "Directory.Build.props", "./"]
COPY ["src/MatBill.Core/MatBill.Core.csproj", "src/MatBill.Core/"]
COPY ["src/MatBill/MatBill.csproj", "src/MatBill/"]
RUN dotnet restore "src/MatBill/MatBill.csproj"

COPY src src
# Fall back to today's date when BuildDate is not supplied, so the version never ends in "-".
RUN BUILD_DATE="${BuildDate:-$(date -u +%Y%m%d)}" && \
    dotnet publish "src/MatBill/MatBill.csproj" \
    -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false \
    -p:Channel=$Channel -p:BuildNumber=$BuildNumber -p:BuildDate=$BUILD_DATE

# ---------------------------------------------------------------------------
# Runtime stage (ASP.NET Core base image)
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# libfontconfig1: the PDF renderer (QuestPDF, Skia) asks fontconfig for fonts; fonts-dejavu-core: a font with every German letter for the invoices.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS="" \
    MATBILL_PORT=2505 \
    MATBILL_DATA=/data \
    TZ=Europe/Berlin \
    DOTNET_gcServer=0 \
    DOTNET_TieredPGO=1

# Configuration (JSON), the keys of the cookies and secrets, the PDFs of the invoices, the logo, scratch space of the backup: one volume.
RUN mkdir -p /data && chown -R $APP_UID:$APP_UID /data
VOLUME ["/data"]

EXPOSE 2505

COPY --from=build /app/publish .
USER $APP_UID

# The app checks itself, so the image needs no extra tools.
HEALTHCHECK --interval=30s --timeout=8s --start-period=40s --retries=3 \
    CMD ["dotnet", "MatBill.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "MatBill.dll"]
