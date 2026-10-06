# Build + publish the API (and its Core/Data deps), then run on the ASP.NET runtime.
# Build context is the repo root (see docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the projects the API depends on.
COPY src/TrendsTracker.Core/   src/TrendsTracker.Core/
COPY src/TrendsTracker.Data/   src/TrendsTracker.Data/
COPY src/TrendsTracker.Api/    src/TrendsTracker.Api/
# The API csproj links a shared config from here (appsettings.json if present,
# else the committed appsettings.sample.json). Only the sample is committed.
COPY config/                   config/

RUN dotnet restore src/TrendsTracker.Api/TrendsTracker.Api.csproj
RUN dotnet publish src/TrendsTracker.Api/TrendsTracker.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TrendsTracker.Api.dll"]
