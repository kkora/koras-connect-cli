# Koras Connect CLI

`koras-connect` is a .NET 10 command-line tool that exports data from ADP Workforce Now, SAP SuccessFactors,
Qualtrics, Microsoft Dataverse, Salesforce and Microsoft Graph. It is a thin layer over the
[Koras Connect SDK](https://github.com/kkora/koras-connect-sdk): authentication, mTLS, pagination, retries and
exporters all come from the SDK. This repository contains no SDK source and no vendor customer data.

> **Status: preview.** Everything is verified against synthetic fixtures only. No live vendor tenant has been
> exercised. See [FEATURES-AND-PLAN.md](FEATURES-AND-PLAN.md) for per-feature status and evidence.

## Prerequisites

- .NET SDK 10.0.100 or later
- git (to fetch the SDK) and, for the ADP connector, a PKCS#12 client certificate for mTLS

## SDK feed setup (reproducible)

The SDK packages (`0.1.0-preview.2`) are not published, so build them into a local feed from a checkout of the SDK
at the commit pinned in [`SDK-COMMIT`](SDK-COMMIT):

```sh
git clone https://github.com/kkora/koras-connect-sdk ../koras-connect-sdk
git -C ../koras-connect-sdk checkout "$(cat SDK-COMMIT)"
eng/build-sdk-feed.sh ../koras-connect-sdk        # Windows: eng\build-sdk-feed.ps1 -Sdk ..\koras-connect-sdk
```

This writes `artifacts/sdk-feed/`, which [`nuget.config`](nuget.config) already lists. The script refuses to run if the
checkout is not at the pinned commit. Details: [docs/SDK-FEED.md](docs/SDK-FEED.md).

## Build, test, install

```sh
dotnet restore Koras.Connect.Cli.slnx
dotnet build   Koras.Connect.Cli.slnx -c Release --no-restore
dotnet test --solution Koras.Connect.Cli.slnx -c Release --no-build
eng/smoke-install.sh        # pack + install into a clean tool path + exercise the installed command
```

To install the packed tool yourself:

```sh
dotnet pack src/Koras.Connect.Cli -c Release -o artifacts/pkg
dotnet tool install -g Koras.Connect.Cli --version 0.1.0-preview.1 \
  --add-source artifacts/pkg --add-source artifacts/sdk-feed
koras-connect --version
```

## Profiles and credentials

A profile is a JSON file in `~/.koras-connect/profiles/NAME.json` (override the directory with
`KORAS_CONNECT_HOME`). It holds endpoints and **credential references**, never secret values:

| Reference | Meaning |
|---|---|
| `env:NAME` | read environment variable `NAME` when the command runs |
| `file:/path/to/secret.txt` | read the file's contents (surrounding newlines trimmed) |

A literal secret is rejected at `profile init`. Resolved values are registered with a redactor, so they cannot appear in
any message the CLI prints. `profile validate` checks syntax, URL shape, certificate file existence and that every
reference resolves, and reports problems by name only.

```sh
koras-connect profile init adp-prod --connector adp \
  --set apiRoot=https://api.adp.com \
  --set tokenEndpoint=https://accounts.adp.com/auth/oauth/v2/token \
  --set certificatePath=/secure/adp-client.pfx \
  --credential clientId=env:ADP_CLIENT_ID \
  --credential clientSecret=env:ADP_CLIENT_SECRET \
  --credential certificatePassword=file:/secure/adp-cert-password.txt

koras-connect profile list
koras-connect profile validate adp-prod
koras-connect doctor --profile adp-prod
```

Settings and credentials per connector:

| Connector | Settings | Credential references |
|---|---|---|
| `adp` | `apiRoot`, `tokenEndpoint`, `certificatePath`, `scope`?, `pageSize`? | `clientId`, `clientSecret`, `certificatePassword`? |
| `sap` | `odataRoot`, `tokenEndpoint` (ends `/oauth/token`), `companyId`, `clientId`, `pageSize`? | `assertion` (pre-generated SAML bearer assertion; signing stays outside the CLI) |
| `qualtrics` | `apiRoot` | `apiToken` |
| `dataverse` | `environmentUrl`, `tokenEndpoint`, `clientId` | `clientSecret` |
| `salesforce` | `instanceRoot`, `tokenEndpoint`, `clientId`, `apiVersion`? | `clientSecret` |
| `graph` | `tenantId`, `clientId`, `beta`? | `clientSecret` |

All URLs must be `https`, without credentials, query or fragment.

## Commands

Data goes to **stdout** (or `--output FILE`); progress and diagnostics go to **stderr**. When writing to a file the
published path is printed to stdout, so `f=$(koras-connect ... --output out.csv)` works.

```sh
# ADP workers (CLI-004): explicit field selection is required
koras-connect adp workers export --profile adp-prod --fields AssociateOid,GivenName,FamilyName,JobTitle \
  --format csv --output workers.csv
koras-connect adp workers export --profile adp-prod --fields AssociateOid,WorkerStatus --format json > workers.json

# SAP SuccessFactors persons (CLI-005)
koras-connect sap persons export --profile sap-prod --fields personIdExternal,personId \
  --as-of 2025-03-01 --format xlsx --output persons.xlsx
koras-connect sap persons export --profile sap-prod --fields personIdExternal --from 2025-01-01 --to 2025-06-30 --filter "personId gt 100"

# Qualtrics responses (CLI-006): vendor file preserved byte-for-byte; the progress id is printed to stderr
koras-connect qualtrics responses export --profile qlt --survey SV_abc123 --format csv --output responses.zip
koras-connect qualtrics responses export --profile qlt --survey SV_abc123 --progress-id ES_xyz --output responses.zip   # resume: no new export

# Dataverse, Salesforce, Microsoft Graph (CLI-008)
koras-connect dataverse rows export --profile dv --table accounts --fields name,accountid --filter "statecode eq 0" --output accounts.csv
koras-connect salesforce query export --profile sf --soql "SELECT Id, Name FROM Account" --fields Id,Name --output accounts.csv
koras-connect graph users export  --profile g --fields id,displayName,userPrincipalName --output users.json --format json
koras-connect graph groups export --profile g --fields id,displayName --output groups.csv

# Incremental exports (CLI-009)
koras-connect graph users export --profile g --fields id,displayName --incremental --output users-delta.csv
koras-connect dataverse rows export --profile dv --table accounts --fields name --incremental --output accounts-delta.csv
koras-connect salesforce changes export --profile sf --object Account --fields Id,Name --output account-changes.csv
koras-connect checkpoint list  --profile g
koras-connect checkpoint reset --profile g --all
```

### Common export options

| Option | Meaning |
|---|---|
| `--profile NAME` | profile to use (required) |
| `--fields a,b,c` | explicit field selection (required for tabular exports; there is no "all fields") |
| `--format csv\|json\|xml\|xlsx` | output format, default `csv` |
| `--output FILE` / `-` | destination, default stdout. `xlsx` requires a file. |
| `--overwrite` | allow replacing an existing file (otherwise the run is refused before any record is fetched) |

Destination and options are validated before the first record is fetched. File output is written to a hidden temporary
sibling and renamed into place only on success; on failure or cancellation the temporary file is removed and any existing
file is untouched. Stdout output is staged first, so a failed run never leaves partial data on stdout.

## Output formats

| Connector | csv | json | xml | xlsx | Notes |
|---|:-:|:-:|:-:|:-:|---|
| ADP workers | yes | yes | yes | yes | fields limited to the SDK's documented worker columns |
| SAP persons | yes | yes | yes | yes | `--fields` pushed down as `$select` |
| Dataverse rows | yes | yes | yes | yes | `--fields` pushed down as `$select` |
| Salesforce query | yes | yes | yes | yes | projected from the query's returned records |
| Graph users/groups | yes | yes | yes | yes | `--fields` pushed down as `$select` |
| Qualtrics responses | vendor | vendor | vendor | no | `--format csv\|tsv\|json\|ndjson\|xml\|spss`; the vendor download is written as-is (ZIP unless `--no-compress`) |

CSV, XML and XLSX are written by the SDK exporters (CSV neutralizes spreadsheet formula prefixes; XLSX uses inline
strings, never formulas). JSON is written by the CLI from the selected columns, because the SDK's generic
`JsonExportProvider` serializes whole records and would ignore field selection. All JSON values are strings or `null`.

## Incremental exports

| Command | Mechanism | Checkpoint |
|---|---|---|
| `graph users\|groups export --incremental` | Graph delta query | `@odata.deltaLink` |
| `dataverse rows export --incremental` | Dataverse change tracking (must already be enabled on the table) | `@odata.deltaLink` |
| `salesforce changes export` | `SystemModstamp` query (`queryAll`, includes soft-deleted rows) | latest `SystemModstamp` seen |

First run exports everything and saves a checkpoint. Later runs export only what changed. Incremental output adds a
`_change` column (`changed`/`deleted`) for Graph and Dataverse, plus `_id` for Dataverse deletions.

- **Scope.** A checkpoint is keyed by connector + profile + the effective query (fields, table/object, etc.).
  `--output`, `--format` and `--overwrite` do not change the scope.
- **Safety.** The checkpoint advances only after the output file is published. A failure or cancellation before
  publication leaves the previous checkpoint in place. A per-scope exclusive lock stops simultaneous runs from clobbering
  each other (exit code 9).
- **Replay.** Delivery is at-least-once. If the file is published but the checkpoint cannot be saved (exit 10), the old
  checkpoint stays, and the next run replays those changes, so duplicates are possible. Salesforce uses `>=`, so the
  boundary record is re-sent on every run; de-duplicate downstream by key.
- **Reset.** `--reset-checkpoint` on an export, or `checkpoint reset --profile NAME --scope ID` / `--all`, forces the next
  run to be a full export. Do this after a Graph delta token expires (HTTP 410). Checkpoints live under
  `~/.koras-connect/checkpoints/`.

Not shipped (no documented vendor continuation exposed by the SDK): incremental ADP and SAP exports, and Qualtrics
continuation tokens. See [FEATURES-AND-PLAN.md](FEATURES-AND-PLAN.md).

## Exit codes

| Code | Meaning |
|---:|---|
| 0 | success |
| 1 | unexpected error |
| 2 | usage error (bad or missing arguments, unsupported format or field) |
| 3 | configuration error (profile missing or invalid, credential reference unresolved) |
| 4 | authentication failure (credentials rejected) |
| 5 | authorization failure (authenticated but denied) |
| 6 | connectivity failure (network, TLS, timeout, 5xx) |
| 7 | destination refused (file exists without `--overwrite`, directory missing or not writable) |
| 8 | vendor error (throttled after retries, malformed response, export failed or timed out) |
| 9 | another run holds the checkpoint |
| 10 | file published but checkpoint not saved (see Replay) |
| 130 | cancelled (Ctrl+C) |

## Feature status

| ID | Feature | Status |
|---|---|---|
| CLI-001 | Installable tool, `--help`, `--version` | implemented |
| CLI-002 | `profile init/list/validate` | implemented |
| CLI-003 | `doctor` | implemented |
| CLI-004 | `adp workers export` | implemented |
| CLI-005 | `sap persons export` | implemented |
| CLI-006 | `qualtrics responses export` (progress id, resume) | implemented |
| CLI-007 | CSV, JSON, XML, XLSX; safe file publication | implemented |
| CLI-008 | Dataverse, Salesforce, Graph exports | implemented |
| CLI-009 | Incremental exports and checkpoints | implemented for Graph, Dataverse, Salesforce; ADP, SAP, Qualtrics not shipped |

Live vendor validation and NuGet publishing are out of scope for this repository. Per-feature acceptance criteria and
verification evidence are in [FEATURES-AND-PLAN.md](FEATURES-AND-PLAN.md).

## CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs restore, build, test, pack and the install smoke test on
Ubuntu, macOS and Windows. It checks out the SDK at the commit in `SDK-COMMIT` and builds the local feed first. This
needs read access to `kkora/koras-connect-sdk`,
which the default `GITHUB_TOKEN` of this repository does **not** have. **Dependency that blocks hosted CI until set:** add a
read-only (`contents: read`) token for the SDK repository as the repository secret `SDK_READ_TOKEN`. Without it the job
stops at a clear "Require SDK read access" step. First run (before the secret existed) failed at the SDK checkout with
"Repository not found" on all three OSes; no build or test step ran in CI.
