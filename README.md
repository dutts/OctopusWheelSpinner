# Octopus Wheel Spinner

A small .NET service that logs into your [Octopus Energy](https://octopus.energy) account on a schedule and spins the
Octoplus **Wheel of Fortune** for every spin you are allowed, for both electricity and gas. Every attempt and its
outcome is stored in a local SQLite database and can be read back as JSON.

> **Unofficial.** This project is not affiliated with or endorsed by Octopus Energy. It uses Octopus's GraphQL API, and
> the wheel of fortune calls are not part of the documented public API, so they may change or stop working without
> notice. Use it at your own risk and check that it is consistent with your Octopus terms.

## How it works

1. On the configured schedule ([Quartz](https://www.quartz-scheduler.net/) cron), it authenticates with your Octopus
   **API key**.
2. For each fuel (electricity, gas) it asks Octopus how many spins are available.
3. It spins that many times, recording each attempt (time, fuel, success, points won, error) in SQLite. If a spin
   fails, the error is recorded and that fuel is skipped for the run.

Spins are only granted about once a month, so the default schedule is 09:00 on the 1st of each month. Running more
often is harmless: if no spins are available, nothing is spun.

## Requirements

- An Octopus Energy account with the Wheel of Fortune available
- Your **API key** and **account number** (e.g. `A-1234ABCD`). The API key is under *Developer settings* in your
  Octopus online dashboard.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build and run locally, **or** Docker

## Configuration

All configuration is through environment variables.

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `OCTOPUS_API_KEY` | yes | | Your Octopus API key |
| `OCTOPUS_ACCOUNT_NUMBER` | yes | | Your account number, e.g. `A-1234ABCD` |
| `SPIN_CRON` | no | `0 0 9 1 * ?` | When to spin, as a Quartz cron expression (see below) |
| `DB_PATH` | no | `data/spins.db` | Where the SQLite database is written |
| `HEALTH_PORT` | no | `8080` | Port for the HTTP endpoints |
| `LOG_FORMAT` | no | text | Set to `json` for structured JSON logs, one event per line |

### Schedule format

`SPIN_CRON` uses the Quartz format, which has a leading **seconds** field and is not the usual five-field cron:

```
sec min hour day-of-month month day-of-week [year]
```

You must put `?` in either day-of-month or day-of-week.

| Expression | Meaning |
| --- | --- |
| `0 0 9 1 * ?` | 09:00 on the 1st of every month (default) |
| `0 0 9 1-3 * ?` | 09:00 on the 1st, 2nd and 3rd of every month |
| `0 0 */6 * * ?` | Every 6 hours |
| `0 * * * * ?` | Every minute (for testing only) |

Times are in the machine's time zone. In Docker that is UTC unless you set `TZ`. An invalid expression stops the app at
startup with an error. If the app was down at a scheduled time, it runs once when it next starts.

## Run locally

```sh
export OCTOPUS_API_KEY=your-api-key
export OCTOPUS_ACCOUNT_NUMBER=A-1234ABCD
dotnet run --project OctopusWheelSpinner
```

Build only:

```sh
dotnet build
```

## Run the published Docker image

A multi-architecture image (`linux/amd64` and `linux/arm64`) is built by GitHub Actions and published to the GitHub
Container Registry on every push to `main`:

```
ghcr.io/dutts/octopuswheelspinner:latest
```

| Tag | Description |
| --- | --- |
| `latest` | The most recent build of `main` |
| `<major>.<minor>.<patch>` | A specific version, e.g. `1.2.3`, to pin to |
| `sha-<commit>` | A specific commit |

Pin to a version tag rather than `latest` if you want to control when you upgrade.

Create a `.env` file next to where you will run the container. Use plain `KEY=value` lines with no quotes and no
`export`:

```
OCTOPUS_API_KEY=your-api-key
OCTOPUS_ACCOUNT_NUMBER=A-1234ABCD
SPIN_CRON=0 0 9 1 * ?
```

### `docker run`

```sh
docker run -d \
  --name octopus-wheel-spinner \
  --restart unless-stopped \
  --env-file .env \
  -e DB_PATH=/data/spins.db \
  -v "$PWD/data:/data" \
  -p 127.0.0.1:8080:8080 \
  ghcr.io/dutts/octopuswheelspinner:latest
```

### Docker Compose

```yaml
services:
  octopus-wheel-spinner:
    image: ghcr.io/dutts/octopuswheelspinner:latest
    container_name: octopus-wheel-spinner
    env_file:
      - .env
    environment:
      DB_PATH: /data/spins.db
    volumes:
      - ./data:/data
    ports:
      - "127.0.0.1:8080:8080"
    restart: unless-stopped
```

```sh
docker compose up -d
docker compose logs -f
```

The image already contains a `HEALTHCHECK` that calls `/health`, so it does not need repeating in your compose file.
`docker ps` shows `(healthy)` once it passes.

Notes:

- The port is published on `127.0.0.1` only, because the endpoints have no authentication. Change it to `8080:8080`
  only if you trust your network.
- The database is stored in `./data` on the host. The container runs as a non-root user (UID 1654), so on Linux make
  sure that folder is writable by it.
- If you fork this repository, the image is published under your own account and name, and GHCR packages are private by
  default. Make the package public in its settings if you want others to pull it without logging in.

## Build and run from source with Docker Compose

To build the image yourself instead, create `OctopusWheelSpinner/.env` (it is git-ignored). Use plain `KEY=value` lines
with no quotes and no `export`:

```
OCTOPUS_API_KEY=your-api-key
OCTOPUS_ACCOUNT_NUMBER=A-1234ABCD
SPIN_CRON=0 0 9 1 * ?
```

Then, from the repository root, use the `docker-compose.yml` in this repo, which builds from the `Dockerfile`:

```sh
docker compose up -d --build
docker compose logs -f
```

The database is stored in `./data` on the host (mounted at `/data` in the container). The container runs as a non-root
user (UID 1654), so on Linux make sure that folder is writable by it.

Or build and run the image directly:

```sh
docker build -f OctopusWheelSpinner/Dockerfile -t octopus-wheel-spinner .
docker run -d --name octopus-wheel-spinner --env-file OctopusWheelSpinner/.env -e DB_PATH=/data/spins.db -v "$PWD/data:/data" -p 8080:8080 octopus-wheel-spinner
```

## HTTP endpoints

The service listens on port `8080` (see `HEALTH_PORT`).

### `GET /health`

Checks that the Octopus API is reachable. It sends an unauthenticated `{ __typename }` query to the Octopus endpoints,
so it does not use your credentials or touch your account. Returns `200` when healthy and `503` when not:

```json
{ "status": "healthy", "error": null }
```

The result is cached for 30 seconds. The Docker image and compose file both use this endpoint for their `HEALTHCHECK`
(via `curl`).

### `GET /spins`

Returns the recorded spin attempts, newest first, with a summary.

| Query parameter | Description |
| --- | --- |
| `limit` | Maximum attempts returned, 1 to 1000 (default 100). The summary ignores this. |
| `fuel` | `electricity` or `gas` |
| `success` | `true` or `false` |

```sh
curl "localhost:8080/spins?fuel=gas&limit=10"
```

```json
{
  "summary": { "attempts": 2, "successful": 2, "failed": 0, "totalPointsWon": 16 },
  "attempts": [
    {
      "id": 4,
      "attemptedAt": "2026-10-02T09:12:28.277752+00:00",
      "fuel": "Gas",
      "success": true,
      "pointsWon": 8,
      "error": null
    }
  ]
}
```

> **Security:** the endpoints have no authentication. Do not expose the port to the internet. To limit it to your own
> machine, publish it as `127.0.0.1:8080:8080`.

## Data

Attempts are stored in the `spin_attempts` table:

| Column | Description |
| --- | --- |
| `id` | Auto-incrementing id |
| `attempted_at` | UTC timestamp of the attempt |
| `account_number` | Octopus account number |
| `fuel_type` | `Electricity` or `Gas` |
| `success` | `1` if the spin succeeded, otherwise `0` |
| `points_won` | Octoplus points won (null on failure) |
| `error` | Error message (null on success) |

```sh
sqlite3 data/spins.db "select * from spin_attempts order by id desc"
```

## Project layout

| File | Purpose |
| --- | --- |
| `Program.cs` | Configuration, logging (Serilog) and service wiring |
| `SpinJob.cs` | The Quartz job that checks and spins for each fuel |
| `OctopusClient.cs` | GraphQL client: authentication, spins allowed, spin, connectivity check |
| `SpinStore.cs` | SQLite storage and queries |
| `ApiServer.cs` | The `/health` and `/spins` endpoints |
