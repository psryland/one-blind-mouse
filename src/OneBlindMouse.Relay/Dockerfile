# One-Blind-Mouse relay container image.
# Build from the repository root so the shared Core library is in the build context:
#   docker build -f src/OneBlindMouse.Relay/Dockerfile -t obm-relay .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Restore first, using only the project files, so the restore layer is cached between source edits
COPY Directory.Build.props ./
COPY src/OneBlindMouse.Core/OneBlindMouse.Core.csproj src/OneBlindMouse.Core/
COPY src/OneBlindMouse.Relay/OneBlindMouse.Relay.csproj src/OneBlindMouse.Relay/
RUN dotnet restore src/OneBlindMouse.Relay/OneBlindMouse.Relay.csproj

# Build and publish the relay
COPY src/OneBlindMouse.Core/ src/OneBlindMouse.Core/
COPY src/OneBlindMouse.Relay/ src/OneBlindMouse.Relay/
RUN dotnet publish src/OneBlindMouse.Relay/OneBlindMouse.Relay.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./

# Listen on 8080 as the built-in non-root 'app' user
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "OneBlindMouse.Relay.dll"]
