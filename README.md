# polish-spot-price-loxone

Proste API pod Loxone, które pobiera ceny z TGE RDN (Polska) i wystawia je jako:

- jeden endpoint JSON `h0..h23`
- opcjonalnie pojedyncze endpointy `h0`, `h1`, ..., `h23`

Serwis trzyma dane w cache i odświeża je automatycznie po publikacji.

## Endpointy

Zakładając, że aplikacja działa pod `https://twoja-apka.azurewebsites.net`:

- `GET /health`  
  status aplikacji i cache

- `GET /loxone/prices`  
  JSON tylko z polami `h0..h23`, np.:
  ```json
  {
    "h0": 0.42111,
    "h1": 0.39821,
    "h2": 0.3777
  }
  ```

- `GET /loxone/h0` ... `GET /loxone/h23`  
  pojedyncza liczba `text/plain`

- `POST /admin/refresh`  
  ręczne wymuszenie odświeżenia cache

## Jak czytać `h0..h23`

- `h0` = cena dla bieżącej godziny
- `h1` = cena za 1 godzinę
- `h5` = cena za 5 godzin
- `h23` = cena za 23 godziny

Wartości są w `zł/kWh`.

## Źródło danych

TGE RDN:

- strona: `https://tge.pl/energia-elektryczna-rdn`
- parser używa `dateShow=DD-MM-YYYY` i czyta dane godzinowe

## Konfiguracja (`appsettings.json`)

Sekcja:

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

## Szybki start lokalnie

```bash
dotnet build
dotnet run --project PolishSpotPriceToLoxone
```

Domyślny lokalny adres (z launch settings):  
`http://localhost:5154`

Przykład:

`http://localhost:5154/loxone/prices`

## Deploy na Azure (Web App Linux F1)

Przykład (CLI):

```bash
az webapp up \
  --name <unikalna-nazwa-appki> \
  --resource-group <resource-group> \
  --plan <app-service-plan> \
  --location westeurope \
  --runtime "DOTNETCORE:10.0" \
  --os-type Linux
```

Po deployu ustaw URL w Loxone na:

`https://<unikalna-nazwa-appki>.azurewebsites.net/loxone/prices`
