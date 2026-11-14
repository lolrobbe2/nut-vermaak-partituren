
# ==========================================
# Build
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy source code and project files.
COPY . .

# Publish the actual web application.
RUN dotnet publish "nut-vermaak-partituren/nut-vermaak-partituren.csproj" \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false


# ==========================================
# Runtime
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

# Render defaults to port 10000.
EXPOSE 10000

# Respect Render's PORT environment variable.
ENTRYPOINT ["sh", "-c", "exec dotnet nut-vermaak-partituren.dll --urls http://0.0.0.0:${PORT:-10000}"]
