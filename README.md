# tuben. — Backend

.NET 10 minimal API for SL station discovery, departures, journey planning, and SMHI weather forecasts.

## Run locally

```powershell
cd Tube.Api
dotnet run --urls http://localhost:5080
```

Deploy `Tube.Api/` as the backend service. Configure the production CORS origin to match the Azure Static Web App. Account storage with PostgreSQL/Neon is planned, not implemented yet.

The local launch profile uses `http://localhost:5080`, matching the Vite proxy. In Azure App Service, set `FrontendOrigins` to the full Static Web App origin (for example, `https://your-app.azurestaticapps.net`) so the browser can call this API. The frontend polls SL departures every 60 seconds while the page is open.

Nearby metro stations are identified from SL's `/stop-points` data (`stop_area.type = METROSTN`) and ranked against `/sites?expand=true` coordinates. This avoids treating the Journey Planner's coordinate lookup result (a reverse-geocoded address) as a transit stop. The metro stop-area list is cached in memory for 12 hours.

`GET /api/weather?lat=59.33&lon=18.07` returns a normalized SMHI SNOW1gv1 point forecast. The response includes current conditions, the next forecast hours, precipitation probability for the next six hours, and the lowest forecast temperature this evening when available. Forecasts are cached for 15 minutes per rounded coordinate. Attribute weather data to SMHI in the UI.

Weather source: [SMHI Meteorological forecast API (SNOW1gv1)](https://opendata.smhi.se/metfcst/snow1gv1).
