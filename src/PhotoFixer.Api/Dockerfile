# Build stage (DIAGNOSTIC - temporary)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ .
RUN find /src -maxdepth 3 -type f | sort | head -80
RUN dotnet restore src/PhotoFixer.Api/PhotoFixer.Api.csproj
RUN dotnet publish src/PhotoFixer.Api/PhotoFixer.Api.csproj -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PhotoFixer.Api.dll"]
