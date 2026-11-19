# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/PhotoFixer.Core/PhotoFixer.Core.csproj src/PhotoFixer.Core/
COPY src/PhotoFixer.Api/PhotoFixer.Api.csproj src/PhotoFixer.Api/
RUN dotnet restore src/PhotoFixer.Api/PhotoFixer.Api.csproj
COPY src/ .
RUN dotnet publish src/PhotoFixer.Api/PhotoFixer.Api.csproj -c Release -o /app --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PhotoFixer.Api.dll"]
