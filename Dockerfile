# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj và restore
COPY LobbyServer/*.csproj ./LobbyServer/
RUN dotnet restore LobbyServer/LobbyServer.csproj

# Copy toàn bộ source và build
COPY . .
WORKDIR /src/LobbyServer
RUN dotnet publish -c Release -o /app

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "LobbyServer.dll"]
