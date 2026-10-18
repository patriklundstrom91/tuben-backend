# tuben. — Backend

.NET 10 minimal API for SL station discovery, departures, and journey planning.

## Run locally

```powershell
cd Tube.Api
dotnet run --urls http://localhost:5080
```

Deploy `Tube.Api/` as the backend service. Configure the production CORS origin to match the Azure Static Web App. Account storage with PostgreSQL/Neon and weather integration are planned, not implemented yet.
