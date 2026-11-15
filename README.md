# Octopus Wheel Spinner

A small .NET service that logs into your [Octopus Energy](https://octopus.energy) account on a schedule and spins the
Octoplus **Wheel of Fortune** for every spin you are allowed, for both electricity and gas. Once a week it also
scratches your Octoplus **scratch card**. Every attempt and its outcome is stored in a local SQLite database and can be
read back as JSON.

> **Unofficial.** This project is not affiliated with or endorsed by Octopus Energy. It uses Octopus's GraphQL API, and
> the wheel of fortune and scratch card calls are not part of the documented public API, so they may change or stop working without
> notice. Use it at your own risk and check that it is consistent with your Octopus terms.

## How it works

1. On the configured schedule ([Quartz](https://www.quartz-scheduler.net/) cron), it authenticates with your Octopus
   **API key**.
2. For each fuel (electricity, gas) it asks Octopus how many spins are available.
3. It spins that many times, recording each attempt (time, fuel, success, points won, error) in SQLite. If a spin
   fails, the error is recorded and that fuel is skipped for the run.

Spins are only granted about once a month, so the default schedule is 09:00 on the 1st of each month. Running more
often is harmless: if no spins are available, nothing is spun.

Scratch cards run on their own schedule (`SCRATCH_CRON`), by default 09:00 every Monday, when Octoplus opens the week's
scratch card session:

1. It asks Octopus for the active scratch card session. If none is running, or the card has already been scratched,
   nothing happens.
2. Otherwise it scratches the card and records the attempt (time, session, status, prize, error) in SQLite.

It only scratches: a prize is **not** claimed automatically. If you win an offer, the notification tells you to claim it
in the Octoplus tab of the Octopus app.

## Requirements

- An Octopus Energy account with the Wheel of Fortune and Octoplus scratch cards available
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
| `SCRATCH_CRON` | no | `0 0 9 ? * MON` | When to scratch the weekly scratch card, as a Quartz cron expression (see below) |
| `OCTOPUS_API_URL` | no | `https://api.octopus.energy/v1/graphql/` | Override the Octopus token endpoint, for testing against a mock |
| `OCTOPUS_BACKEND_URL` | no | `https://api.backend.octopus.energy/v1/graphql/` | Override the Octopus backend endpoint, for testing against a mock |
| `DB_PATH` | no | `data/spins.db` | Where the SQLite database is written |
| `HEALTH_PORT` | no | `8080` | Port for the HTTP endpoints |
| `LOG_FORMAT` | no | text | Set to `json` for structured JSON logs, one event per line |
| `NTFY_URL` | no | | Base URL of your [ntfy](https://ntfy.sh) server, e.g. `https://ntfy.dutton.family`. Enables notifications (with `NTFY_TOPIC`) |
| `NTFY_TOPIC` | no | | ntfy topic to publish to. Both this and `NTFY_URL` must be set to enable notifications |
| `NTFY_USERNAME` | no | | ntfy username, for servers that need authentication |
| `NTFY_API_KEY` | no | | ntfy access token (or password). Sent as Basic auth with `NTFY_USERNAME`, or as a Bearer token if no username is set |

### Notifications

If `NTFY_URL` and `NTFY_TOPIC` are set, a notification is sent after a run that spun the wheel (listing the points won
per fuel), at high priority if any spin failed, and also if the run errors. Nothing is sent when no spins were
available. A scratch card run sends one notification: the prize won (with a reminder to claim offers in the Octopus app),
"No prize this week" at low priority, or a high priority problem notice if the scratch failed. Nothing is sent when
there is no card to scratch. A failure to deliver a notification is logged and never fails the run. Setting only one of the two required
variables stops the app at startup.

### Schedule format

`SPIN_CRON` and `SCRATCH_CRON` use the Quartz format, which has a leading **seconds** field and is not the usual five-field cron:

```
sec min hour day-of-month month day-of-week [year]
```

You must put `?` in either day-of-month or day-of-week.

| Expression | Meaning |
| --- | --- |
| `0 0 9 1 * ?` | 09:00 on the 1st of every month (default) |
| `0 0 9 1-3 * ?` | 09:00 on the 1st, 2nd and 3rd of every month |
| `0 0 9 ? * MON` | 09:00 every Monday (default for `SCRATCH_CRON`) |
| `0 0 */6 * * ?` | Every 6 hours |
| `0 * * * * ?` | Every minute (for testing only) |

Times are in the machine's time zone. In Docker that is UTC unless you set `TZ`, which is still inside Monday in UK time
at 09:00. An invalid expression stops the app at
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
- To keep memory low, the image sets `DOTNET_GCgen0size=0x400000` and `DOTNET_TieredPGO=0`, and the app uses invariant
  globalisation. Together these keep it at roughly 50 MiB after activity, against 60-85 MiB with the .NET defaults.
  Override the environment variables with `-e` or `environment:` if you need to.

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

The author's deployment is at <https://octopuswheelspinner.dutton.family>, behind a sign-in gateway; opening it shows
the status page.

### `GET /`

A lightweight status page (the embedded `status.html`: plain HTML, CSS and JavaScript, with no external requests). It
shows the service version, whether the Octopus API is reachable, each job's schedule with its last and next run, and the
recorded spins and scratch cards with summary figures and filters. It reads `/health`, `/schedule`, `/spins` and
`/scratches` from the browser and refreshes every 30 seconds. A link in the header opens the API reference.

### `GET /health`

Returns `200` whenever the service is running. The Docker image's `HEALTHCHECK` uses it (via `curl`), so an Octopus
outage doesn't get the container restarted over and over.

The response includes the running `version`, and says whether the Octopus API is reachable. That check sends an unauthenticated `{ __typename }`
query to the Octopus endpoints, so it does not use your credentials or touch your account:

```json
{ "status": "healthy", "version": "1.2.3", "octopus": "slow", "error": "api.octopus.energy: no response within 10s" }
```

`octopus` is one of:

| Value | Meaning |
| --- | --- |
| `reachable` | Both endpoints answered within 10 seconds |
| `slow` | A connection was made, but an endpoint didn't answer within 10 seconds. Octopus is up but responding slowly |
| `unreachable` | An endpoint couldn't be connected to within 5 seconds, failed DNS or TLS, or returned an HTTP error |
| `unknown` | The first check hasn't finished yet |

If the endpoints differ, the worse result is reported, and `error` lists each endpoint that wasn't reachable. The check
runs in the background and its result is cached for 30 seconds, so `/health` answers immediately and never waits on
Octopus.

### `GET /schedule`

The cron expression, previous run and next run of each job (`spin` and `scratch`):

```json
{
  "jobs": [
    { "job": "spin", "cron": "0 0 9 1 * ?", "previousRun": null, "nextRun": "2026-12-01T09:00:00+00:00" },
    { "job": "scratch", "cron": "0 0 9 ? * MON", "previousRun": null, "nextRun": "2026-11-16T09:00:00+00:00" }
  ]
}
```

`previousRun` only covers runs since the process started, because Quartz keeps its state in memory; it is `null` until a
job next fires after a restart.

### `GET /spins`

Returns the recorded spin attempts, newest first, with a summary.

| Query parameter | Description |
| --- | --- |
| `limit` | Maximum attempts returned, 1 to 1000 (default 100). The summary ignores this. |
| `fuel` | `electricity` or `gas` (case doesn't matter) |
| `success` | `true` or `false` |

An invalid parameter returns `400` with a [problem details](https://www.rfc-editor.org/rfc/rfc9457) body saying which
parameter was wrong.

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

### `GET /scratches`

Returns the recorded scratch card attempts, newest first, with a summary.

| Query parameter | Description |
| --- | --- |
| `limit` | Maximum attempts returned, 1 to 1000 (default 100). The summary ignores this. |
| `success` | `true` or `false` |

An invalid parameter returns `400` with a problem details body, as for `/spins`. In the summary, `won` counts successful
attempts that had a prize.

```sh
curl "localhost:8080/scratches?limit=10"
```

```json
{
  "summary": { "attempts": 1, "successful": 1, "failed": 0, "won": 1 },
  "attempts": [
    {
      "id": 1,
      "attemptedAt": "2026-11-16T09:00:01.123456+00:00",
      "sessionReference": "0b6f3c1e-5d0a-4a58-9a4e-3f1c2d7e8a90",
      "success": true,
      "status": "PRIZE_NOT_YET_CLAIMED",
      "prize": "50 stamps",
      "error": null
    }
  ]
}
```

`status` is Octopus's own value, for example `DID_NOT_WIN`, `PRIZE_NOT_YET_CLAIMED`, `PRIZE_CLAIMED`, `PRIZE_REJECTED` or
`PRIZE_NOT_CLAIMED_IN_TIME`. `prize` is null when nothing was won.

### `GET /scalar`

An interactive [Scalar](https://scalar.com) API reference for the endpoints above, where you can send requests from
your browser: open <http://localhost:8080/scalar/> (or follow the link on the status page at `/`). It is built from the OpenAPI document served at `GET /openapi.json`,
which is generated from the endpoint definitions in `Api.cs`.

> **Security:** the endpoints have no authentication. Do not expose the port to the internet. To limit it to your own
> machine, publish it as `127.0.0.1:8080:8080`.

## Data

Spin attempts are stored in the `spin_attempts` table:

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

Scratch card attempts are stored in the `scratch_attempts` table:

| Column | Description |
| --- | --- |
| `id` | Auto-incrementing id |
| `attempted_at` | UTC timestamp of the attempt |
| `account_number` | Octopus account number |
| `session_reference` | The Octopus scratch card session the attempt belongs to |
| `success` | `1` if the scratch succeeded, otherwise `0` |
| `status` | Octopus's status for the card, e.g. `DID_NOT_WIN` (null on failure) |
| `prize` | Prize name or stamps won (null if nothing won, or on failure) |
| `error` | Error message (null on success) |

## Project layout

| File | Purpose |
| --- | --- |
| `Program.cs` | Configuration, logging (Serilog) and service wiring |
| `SpinJob.cs` | The Quartz job that checks and spins for each fuel |
| `ScratchJob.cs` | The Quartz job that scratches the weekly scratch card |
| `Notifier.cs` | Optional ntfy notifications |
| `OctopusClient.cs` | GraphQL client: authentication, spins allowed, spin, scratch card query and scratch, connectivity check |
| `SpinStore.cs` | SQLite storage and queries |
| `Api.cs` | The `/`, `/health`, `/schedule`, `/spins`, `/scratches`, `/scalar` and `/openapi.json` endpoints (ASP.NET Core minimal APIs) |
| `status.html` | The status page served at `/`, embedded in the assembly |
| `OctopusHealth.cs` | The cached background check of the Octopus API reported by `/health` |
| `OctopusWheelSpinner.Tests/` | Integration tests (xUnit v3, Testcontainers, WireMock.Net), a sibling project at the repository root |

## Tests

`OctopusWheelSpinner.Tests` is a black-box integration suite using [Testcontainers for .NET](https://dotnet.testcontainers.org/).
It builds the real Docker image from the `Dockerfile`, then runs each test against a fresh app container and a fresh
[WireMock.Net](https://github.com/WireMock-Net/WireMock.Net) container standing in for Octopus and ntfy (via
`OCTOPUS_API_URL`, `OCTOPUS_BACKEND_URL` and `NTFY_URL`). Docker must be running.

```sh
dotnet test
```

The first run builds the image, which takes a few minutes; later runs reuse the Docker layer cache.

The same tests also run in GitHub Actions on pushes to `main`, `v*` tags and pull requests to `main`, and the image is
only published if they pass.
