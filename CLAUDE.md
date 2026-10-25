# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-project .NET 10 console service (`OctopusWheelSpinner/`) that, on a Quartz cron schedule, authenticates to
Octopus Energy's GraphQL API and spins the Octoplus Wheel of Fortune for each fuel, recording every attempt in SQLite and
exposing `/health` and `/spins` over HTTP. `README.md` is the user-facing reference for configuration, endpoints and
deployment.

## Commands

```sh
dotnet build                                        # build (solution: OctopusWheelSpinner.slnx)
dotnet run --project OctopusWheelSpinner            # needs OCTOPUS_API_KEY and OCTOPUS_ACCOUNT_NUMBER set
SPIN_CRON="0 * * * * ?" dotnet run --project OctopusWheelSpinner   # fire every minute for manual testing
docker compose up -d --build                        # build from source; reads OctopusWheelSpinner/.env (git-ignored)
docker build -f OctopusWheelSpinner/Dockerfile -t octopus-wheel-spinner .   # build context must be the repo root
```

There is no test project and no linter configured.

## Architecture

- **`Program.cs`** reads and validates all configuration from environment variables up front (missing required vars,
  invalid `SPIN_CRON`, or only one of `NTFY_URL`/`NTFY_TOPIC` set all fail at startup), configures Serilog, then wires a
  generic host: typed `HttpClient`s for `OctopusClient` and `Notifier`, a singleton `SpinStore`, the Quartz job/trigger,
  and `ApiServer` as a hosted service. Options are plain records (`OctopusOptions`, `NtfyOptions`) registered as
  singletons — there is no `IOptions`/appsettings configuration.
- **`SpinJob`** (`[DisallowConcurrentExecution]`) loops over `FuelType` values, asks for spins allowed, spins that many
  times, and records each result. A failed spin stops that fuel for the run. It sends one ntfy summary at the end (none
  if nothing was spun), or an error notification and rethrow on an unexpected exception. The trigger uses
  `FireAndProceed` misfire handling so a missed schedule runs once on startup.
- **`OctopusClient`** talks to two GraphQL endpoints: `api.octopus.energy` to exchange the API key for a Kraken token
  (cached until ~5 min before expiry), and `api.backend.octopus.energy` for the wheel-of-fortune query/mutation. Queries
  are built by string interpolation (raw `$$"""` literals). GraphQL `errors` become `OctopusApiException`; `SpinAsync`
  converts that into a failed `SpinResult` rather than throwing. Octopus returns 403 without a `User-Agent`, which is set
  on the typed client in `Program.cs`. `CheckConnectivityAsync` is an unauthenticated `{ __typename }` probe only.
- **`ApiServer`** is a `BackgroundService` built on raw `HttpListener` (not ASP.NET Core). `/health` must always return
  200 while the process runs (the Docker `HEALTHCHECK` curls it, and an Octopus outage must not cause restarts); the
  Octopus reachability it reports comes from a background probe cached for 30s so the request never waits on Octopus.
- **`SpinStore`** uses `Microsoft.Data.Sqlite` directly with hand-written SQL, opening a connection per call. The
  `spin_attempts` table is created with `CREATE TABLE IF NOT EXISTS` in the constructor; there is no migration system,
  so schema changes need explicit handling for existing databases.
- **`Notifier`** posts to ntfy and swallows/logs every non-cancellation failure — notifications must never fail a run.

## Conventions

- Feature changes update `README.md` in the same commit (env var table, endpoint docs, project layout table).
- Code style: file-scoped namespaces, primary constructors, `sealed` classes/records, nullable enabled.
- CI (`.github/workflows/docker-publish.yml`) builds a multi-arch (amd64/arm64) image and pushes to GHCR on `main` and
  `v*` tags; version numbers come from GitVersion (`GitVersion.yml`, ContinuousDeployment mode) and are passed into the
  Dockerfile as `VERSION`/`INFORMATIONAL_VERSION` build args.
- The container runs as non-root (`$APP_UID`); `DB_PATH` is set to `/data/spins.db` with `/data` mounted as a volume.