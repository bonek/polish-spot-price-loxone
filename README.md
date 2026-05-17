# polish-spot-price-loxone

A simple API for Loxone that fetches Polish day-ahead spot prices from TGE RDN and exposes them as:

- one JSON endpoint with `h0..h23`
- optional single-value endpoints `h0`, `h1`, ..., `h23`

The service keeps data in a local cache and refreshes it automatically after publication.

## Endpoints

Assuming the app is running at `https://your-app.azurewebsites.net`:

- `GET /health`  
  app and cache status

- `GET /loxone/prices`  
  JSON with only `h0..h23`, for example:
  ```json
  {
    "h0": 0.42111,
    "h1": 0.39821,
    "h2": 0.3777
  }
  ```

- `GET /loxone/h0` ... `GET /loxone/h23`  
  single `text/plain` numeric value

- `POST /admin/refresh`  
  force cache refresh manually

## Meaning of `h0..h23`

- `h0` = current hour price
- `h1` = price in 1 hour
- `h5` = price in 5 hours
- `h23` = price in 23 hours

Values are returned in `PLN/kWh`.

## Data source

TGE RDN:

- page: `https://tge.pl/energia-elektryczna-rdn`
- parser uses `dateShow=DD-MM-YYYY` and reads hourly data

## Configuration (`appsettings.json`)

Section:

```json
"Prices": {
  "TgeUrl": "https://tge.pl/energia-elektryczna-rdn",
  "CacheFile": "data/prices-cache.json",
  "DefaultUnit": "kwh",
  "MarketColumn": "fixing1",
  "RefreshStartHour": 10,
  "RefreshStartMinute": 35,
  "RefreshEndHour": 14,
  "RetryMinutes": 15,
  "RegularRefreshMinutes": 60
}
```

## Local quick start

```bash
dotnet build
dotnet run --project PolishSpotPriceToLoxone
```

Default local URL (from launch settings):  
`http://localhost:5154`

Example:

`http://localhost:5154/loxone/prices`

## Docker deployment

The repository includes a production `Dockerfile` and `docker-compose.yml`.
The image builds the React dashboard first, publishes the .NET API, then serves everything from one ASP.NET Core container.

### Build and run with Docker Compose

```bash
docker compose up -d --build
```

Default local URL:

- `http://localhost:5154/`
- `http://localhost:5154/loxone/docs`
- `http://localhost:5154/loxone/prices`

The compose file stores price cache files in a named volume mounted at `/app/data`.

### Optional SQL cache

For Azure SQL or another SQL Server, set:

```yaml
environment:
  ConnectionStrings__PricesSql: "Server=tcp:your-server.database.windows.net,1433;Initial Catalog=your-db;User ID=...;Password=...;Encrypt=True;TrustServerCertificate=False;"
```

Without `ConnectionStrings__PricesSql`, the app runs normally with the local JSON cache.

### Build only

```bash
docker build -t polish-spot-loxone:latest .
docker run -p 5154:8080 -v polish-spot-price-data:/app/data polish-spot-loxone:latest
```

## Azure deployment

You can deploy to your own Azure App Service with the included PowerShell script.

Files:

- `scripts/deploy.azure.example.json` - example config
- `scripts/Deploy-AzureAppService.ps1` - deployment script

### 1. Prepare config

Copy:

```powershell
Copy-Item .\scripts\deploy.azure.example.json .\scripts\deploy.azure.json
```

Then fill in:

- `subscriptionId` - your Azure subscription
- `resourceGroup` - resource group name
- `location` - for example `westeurope`
- `planName` - App Service plan name
- `appName` - globally unique Web App name
- `sku` - for example `F1`
- `runtime` - default is `DOTNETCORE:10.0`
- `connectionStrings.PricesSql` - optional Azure SQL connection string
- `appSettings` - optional extra app settings

If `PricesSql` is empty, the app still works and uses file cache.

### 2. Run deploy

```powershell
az login
.\scripts\Deploy-AzureAppService.ps1
```

Or with a custom config path:

```powershell
.\scripts\Deploy-AzureAppService.ps1 -ConfigPath .\scripts\deploy.azure.json
```

### 3. What the script does

- creates the resource group if missing
- creates the Linux App Service plan if missing
- creates the Web App if missing
- sets `https-only`
- optionally sets app settings
- optionally sets the `PricesSql` Azure SQL connection string
- publishes the app
- packs a zip deployment
- deploys it to Azure Web App

After deployment, use:

- `https://<app-name>.azurewebsites.net/`
- `https://<app-name>.azurewebsites.net/loxone/docs`
- `https://<app-name>.azurewebsites.net/loxone/prices`
