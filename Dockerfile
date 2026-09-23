FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/NovaWalletLedger.Domain/NovaWalletLedger.Domain.csproj", "src/NovaWalletLedger.Domain/"]
COPY ["src/NovaWalletLedger.Application/NovaWalletLedger.Application.csproj", "src/NovaWalletLedger.Application/"]
COPY ["src/NovaWalletLedger.Infrastructure/NovaWalletLedger.Infrastructure.csproj", "src/NovaWalletLedger.Infrastructure/"]
COPY ["src/NovaWalletLedger.Api/NovaWalletLedger.Api.csproj", "src/NovaWalletLedger.Api/"]
RUN dotnet restore "src/NovaWalletLedger.Api/NovaWalletLedger.Api.csproj"

COPY src/ src/
WORKDIR /src/src/NovaWalletLedger.Api
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN useradd --uid 5678 --user-group --no-create-home novawallet

# COPY always runs as root regardless of any preceding USER instruction, so
# the app's files are copied first (as root) and then explicitly handed to
# the non-root user — setting USER before COPY would leave the published
# files root-owned while the process itself runs unprivileged, relying on
# incidental world-readable permissions rather than a guaranteed grant.
COPY --from=build /app/publish .
RUN chown -R novawallet:novawallet /app
USER novawallet

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "NovaWalletLedger.Api.dll"]
