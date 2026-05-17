# syntax=docker/dockerfile:1

FROM node:22-alpine AS frontend-build
WORKDIR /src/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY PolishSpotPriceToLoxone.sln ./
COPY PolishSpotPriceToLoxone/PolishSpotPriceToLoxone.csproj PolishSpotPriceToLoxone/
RUN dotnet restore PolishSpotPriceToLoxone/PolishSpotPriceToLoxone.csproj
COPY PolishSpotPriceToLoxone/ PolishSpotPriceToLoxone/
RUN dotnet publish PolishSpotPriceToLoxone/PolishSpotPriceToLoxone.csproj -c Release -o /app/publish -p:BuildFrontend=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=backend-build /app/publish ./
COPY --from=frontend-build /src/frontend/dist ./wwwroot
RUN mkdir -p /app/data
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "PolishSpotPriceToLoxone.dll"]
