# TeeTmeFinder


Key features
- ASP.NET Core (.NET 10) background worker (BackgroundService) that polls the SGF slots API
- Thread-safe MonitorState with optional persistence to `monitorstate.json`
- Duplicate suppression with TTL (configurable) and rate-limited Discord webhook sends
- Simple API (Controller) to check status, start and stop monitoring
- Optional API-key protection (X-Api-Key) for start/stop endpoints
- React frontend (functional components) with Tailwind-friendly CSS for settings and live logs

Requirements
- .NET 10 SDK
- Node.js (for client dev) and npm or yarn
- A Discord server + a channel where you can create a webhook

Quick start — backend
1. From repository root run the API (defaults to the addresses shown by ASP.NET):

```powershell
# (optional) bind to localhost:5000
$env:ASPNETCORE_URLS = "http://localhost:5000"

dotnet run --project .\TeeTmeFinder.csproj
```

2. (Optional) Protect start/stop endpoints by setting an API key:

```powershell
$env:MONITOR_API_KEY = "a-strong-secret"
dotnet run --project .\TeeTmeFinder.csproj
```

Quick start — client (development)
1. Open a new terminal and run:

```powershell
cd Client
npm install
npm start
```

2. By default the React client expects the API at `http://localhost:5000`. Change `API_BASE` in `Client/src/components/GolfMonitor.jsx` if your backend listens elsewhere.

Creating a Discord webhook
1. In Discord open Server Settings → Integrations → Webhooks → New Webhook, or open a channel → Edit Channel → Integrations.
2. Copy the webhook URL and paste it into the frontend Webhook field before starting the monitor. Keep the URL private.

API endpoints
- GET `/api/monitor/status` — returns the current MonitorState (isActive, settings, logs)
- POST `/api/monitor/start` — accepts JSON payload and starts monitoring
- POST `/api/monitor/stop` — stops monitoring

Start payload example (send with `Content-Type: application/json`):

```json
{
  "StartDate": "2026-10-05T00:00:00",
  "EndDate": "2026-10-12T00:00:00",
  "MinTime": "06:00:00",
  "MaxTime": "22:00:00",
  "RequiredSlots": 3,
  "WebhookUrl": "https://discord.com/api/webhooks/...."
}
```

Curl examples

Get status:
```bash
curl http://localhost:5000/api/monitor/status
```

Start monitor (with API key):
```bash
curl -X POST http://localhost:5000/api/monitor/start \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: your-key" \
  -d '{ "StartDate":"2026-10-05T00:00:00", "EndDate":"2026-10-12T00:00:00", "MinTime":"06:00:00", "MaxTime":"22:00:00", "RequiredSlots":3, "WebhookUrl":"<webhook>" }'
```

Stop monitor:
```bash
curl -X POST http://localhost:5000/api/monitor/stop -H "X-Api-Key: your-key"
```

Security and privacy notes
- The Discord webhook URL is a secret: anyone with it can post to the target channel. Do not commit it to source control.
- `monitorstate.json` stores the webhook URL in plain text — protect the file or remove the webhook before sharing the project.
- If you enable `MONITOR_API_KEY`, clients must include `X-Api-Key` header to call /start or /stop.
- For public deployments require HTTPS and restrict CORS origins.

Operational notes
- The background service enforces a nighttime blackout window (01:00–07:00) by default.
- Duplicate alerts are suppressed using an in-memory cache with TTL; entries older than the configured TTL are removed.
- Webhook sends are rate-limited per identical payload to avoid spam.

Development notes
- If you want the frontend and API to be served from the same host, build the React app (`npm run build`) and copy the `build` output into the ASP.NET `wwwroot` (or configure the project to serve static files).
- The repository includes a simple Tailwind-friendly CSS fallback. For full Tailwind support initialize Tailwind in `Client/` and add the content paths.

Contributing
- Open an issue or create a pull request. Suggested improvements: encrypted webhook storage, authentication, more robust testing, or adding a small persistence store for historic alerts.

License
- MIT — see LICENSE file (add one if you need it).

Questions or help
- If you want, I can:
  - Wire the React UI to send `X-Api-Key` when an API key is configured.
  - Automatically load `monitorstate.json` at startup.
  - Add server-side encryption for the webhook URL.

Enjoy — and happy tee-time hunting!
