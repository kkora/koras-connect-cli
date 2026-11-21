# Koras Connect CLI: features and plan

Tool command: `koras-connect` · Target: .NET 10 · SDK: `Koras.Connect.*` `0.1.0-preview.2` at the commit in `SDK-COMMIT`.

All verification below is **local, against synthetic fixtures** (xUnit tests with a fake HTTP vendor), plus a clean
local-feed install smoke test. No live vendor tenant has been exercised. GitHub Actions results are tracked separately in
the delivery report, not here. Run everything with the commands in [README.md](README.md).

Status legend: **implemented** (code + tests pass locally) · **partial** · **blocked** (exact missing SDK dependency named).

## Milestone 1: installable ADP export

### CLI-001: Package the tool, `--help`, `--version`
- **Status:** implemented
- **Example:** `koras-connect --version` → `koras-connect 0.1.0-preview.1`; `koras-connect --help`
- **Acceptance:** packed as a .NET tool (`PackAsTool`, command `koras-connect`); `--version` prints tool name and version on stdout; `--help` lists every shipped command and the exit codes; no arguments → usage on stderr, exit 2; a clean local install can invoke the command.
- **Verification:** `VersionAndHelpTests` (4) pass. `eng/smoke-install.sh` packs, installs into an empty `--tool-path` from local feeds only, runs `--version`, `--help`, profile init/list/validate, a secret-leak check and a usage-error exit code → `SMOKE OK (0.1.0-preview.1)`.

### CLI-002: `profile init | list | validate`
- **Status:** implemented
- **Example:** `koras-connect profile init adp-prod --connector adp --set apiRoot=https://api.adp.com ... --credential clientSecret=env:ADP_CLIENT_SECRET`
- **Acceptance:** named profiles store endpoints and credential *references* (`env:`/`file:`), never values; literals are rejected without being echoed; values resolve at run time; validation is local (no network) and lists every missing/invalid setting and unresolved reference by name; profile names cannot traverse paths.
- **Verification:** `ProfileTests` (10) and `RedactionTests` (3) pass: round-trip, file contains no secret values, literal rejected, all missing settings reported, unresolved env var named but not value, http URLs rejected, `--force` needed to replace, unsafe names rejected.

### CLI-003: `doctor --profile NAME`
- **Status:** implemented
- **Example:** `koras-connect doctor --profile adp-prod`
- **Acceptance:** validates the profile first (stops before any request on failure), then runs one minimal SDK-backed read per connector (ADP workers page, SAP PerPerson, Qualtrics survey list, Dataverse WhoAmI, Salesforce resource map, Graph users `$top=1`); distinguishes authentication (4), authorization (5), connectivity (6); all diagnostics on stderr, stdout empty.
- **Verification:** `DoctorTests` (7) plus per-connector doctor tests: token-endpoint 401 → 4, API 401 → 4, 403 → 5, network exception → 6, 503 → 6, missing credential → 3 with zero requests, secrets absent from output.

### CLI-004: `adp workers export`
- **Status:** implemented
- **Example:** `koras-connect adp workers export --profile adp-prod --fields AssociateOid,GivenName,JobTitle --output workers.csv`
- **Acceptance:** uses the SDK's OAuth client-credentials token source, mTLS client certificate and `$skip/$top` pagination; `--fields` is required and limited to the SDK's documented worker columns (government identifiers and compensation are not selectable); unknown fields fail before any request.
- **Verification:** `AdpExportTests`: 5 synthetic workers over pages of 2/2/1 (+ terminating empty page) produce the right rows; only selected columns present; unknown/sensitive field and missing `--fields` → exit 2 with zero requests; wrong-connector profile → exit 2.

### CLI-007 (first part): CSV and JSON, safe publication, stdout, exit codes
- **Status:** implemented
- **Example:** `koras-connect adp workers export ... --format json > workers.json`
- **Acceptance:** destination/options validated before fetching; file output via hidden temporary sibling then rename; refuse overwrite unless `--overwrite`; temp removed on failure/cancel; existing file untouched on failure; stdout carries data only (staged, so no partial data on failure); exit codes documented in README.
- **Verification:** `AdpExportTests`: existing file refused with zero requests; `--overwrite` replaces and leaves no temp; missing dir / directory-as-output refused; mid-export HTTP 500 leaves no partial file and keeps an existing file; failed run writes nothing to stdout; cancellation → 130 and no leftovers; CSV formula prefixes neutralized; JSON contains only selected fields.

## Milestone 2: SAP SuccessFactors and Qualtrics

### CLI-005: `sap persons export`
- **Status:** implemented
- **Example:** `koras-connect sap persons export --profile sap-prod --fields personIdExternal,personId --as-of 2025-03-01 --output persons.csv`
- **Acceptance:** SDK `SuccessFactorsClient` with `SamlBearerTokenSource` (the SDK's only SAP token source; assertion supplied by credential reference); `__next` pagination; `--fields` → `$select`; `--as-of`, `--from/--to`, `--filter` forwarded; `--as-of` with `--from/--to` rejected before any request.
- **Verification:** `SapExportTests` (4): two pages merged, `$select`/`asOfDate`/`fromDate`/`toDate`/`$filter` present in requests, one token exchange, token 401 → exit 4.

### CLI-006: `qualtrics responses export`
- **Status:** implemented
- **Example:** `koras-connect qualtrics responses export --profile qlt --survey SV_abc123 --output responses.zip` / `... --progress-id ES_xyz`
- **Acceptance:** starts the export, prints `progress-id: ...` to stderr, polls, downloads the vendor result byte-for-byte; `--progress-id` resumes without a start request; a poll timeout prints the id to resume with and leaves no file; formats limited to Qualtrics' own (`csv tsv json ndjson xml spss`).
- **Verification:** `QualtricsExportTests` (9): start+poll+download preserves binary ZIP bytes; resume sends zero POSTs; format/compress sent in the start body; poll-limit → exit 8 with resume hint and no file; failed export → 8; `xlsx`/`excel`/`yaml` rejected before any request; existing output protected; bad ids rejected; 401 → 4.

### CLI-007 (completion): XML and Excel
- **Status:** implemented
- **Formats per connector:** csv/json/xml/xlsx for ADP, SAP, Dataverse, Salesforce, Graph; Qualtrics uses vendor formats only (see README matrix).
- **Acceptance:** XML/XLSX written by the SDK `TabularExport` writers; same publication and failure handling as CSV/JSON; XLSX to stdout refused before any request.
- **Verification:** `Xml_and_xlsx_formats_are_written_by_the_sdk_exporters` (reads back the XLSX zip and XML field); `Xlsx_to_stdout_is_refused_before_any_request`.
- **Note:** the SDK's `JsonExportProvider` ignores the field schema (serializes the raw record), so JSON is written by the CLI from the selected columns to honour field selection.

## Milestone 3: additional connectors

SDK inspection at the pinned commit found the required public APIs for all three, so none is blocked:
`DataverseClient.EnumerateAsync`/`GetChangesAsync`, `SalesforceClient.EnumerateQueryAsync`/`EnumerateChangesAsync`, `GraphClient.EnumerateAsync`/`GetDeltaAsync`, with client-credentials token sources for each.

### CLI-008a: `dataverse rows export`
- **Status:** implemented · **Example:** `koras-connect dataverse rows export --profile dv --table accounts --fields name,accountid --filter "statecode eq 0"`
- **Acceptance:** Entra client-credentials via SDK; `@odata.nextLink` paging; `--fields` → `$select`; `--filter` → `$filter`; table name validated.
- **Verification:** `DataverseExportTests`: two pages merged, `$select`/`$filter` in the first request, 403 → exit 5, doctor uses WhoAmI, bad table name rejected.

### CLI-008b: `salesforce query export`
- **Status:** implemented · **Example:** `koras-connect salesforce query export --profile sf --soql "SELECT Id, Name FROM Account" --fields Id,Name`
- **Acceptance:** client-credentials via SDK; `nextRecordsUrl` paging; `--soql` must be a `SELECT`; `--fields` required and projected from returned records; `--all` uses `queryAll`.
- **Verification:** `SalesforceExportTests`: two pages merged and SOQL sent verbatim; non-SELECT and missing `--fields` rejected with zero requests; doctor reads the resource map; token 400 → exit 4.

### CLI-008c: `graph users export`, `graph groups export`
- **Status:** implemented · **Example:** `koras-connect graph users export --profile g --fields id,displayName --page-size 500`
- **Acceptance:** Entra client-credentials via SDK; `@odata.nextLink` paging; `$select`, `$filter`, `$top`; null values preserved in JSON.
- **Verification:** `GraphExportTests`: paging, `$select`/`$top`/`$filter` in the request, JSON null preserved, groups collection, 403 → exit 5.

## Milestone 4: incremental exports

### CLI-009: incremental export with checkpoints
- **Status:** implemented for Graph (delta), Dataverse (change tracking), Salesforce (`SystemModstamp`). **Not shipped:** ADP, SAP, Qualtrics.
- **Example:** `koras-connect graph users export --profile g --fields id,displayName --incremental --output delta.csv`
- **Acceptance:** checkpoint scoped by connector + profile + effective query; advances only after successful file publication; prior checkpoint preserved on failure or cancellation; per-scope exclusive lock (exit 9) prevents simultaneous runs overwriting each other; publication-then-save failure → exit 10 and next run replays; reset by `--reset-checkpoint` or `checkpoint reset`; expired Graph token → reset hint.
- **Verification:** `IncrementalGraphTests` (10), `IncrementalDataverseTests` (2), `IncrementalSalesforceTests` (2): first-run full + resume from saved link; failure before publication keeps checkpoint byte-identical and leaves no file; cancellation before publication keeps checkpoint; **failure between publication and checkpoint persistence** keeps the old checkpoint, file exists, next run replays the same changes; concurrent run refused while lock held and succeeds after release; scoping by profile and query (output options do not create a scope); reset flag and command; corrupt checkpoint reported (exit 3) without writing a file.
- **Replay behaviour:** at-least-once. Duplicates are possible after exit 10 and, for Salesforce (`>=`), at every boundary record. See README.
- **Not shipped, exact dependencies:**
  - **ADP:** the SDK exposes no vendor continuation/change token (only the client-side `AdpChangeDetector`/`FileWorkerFingerprintStore`). Needs a documented ADP change feed.
  - **SAP:** the SDK offers only a caller-managed modification-timestamp filter (`SapDeltaFilters`, `SapDeltaSync`); no vendor continuation token. Deferred.
  - **Qualtrics (blocked):** `Koras.Connect.Qualtrics` `ResponseExportOptions` has no `AllowContinuation` property, and `QualtricsClient.StartResponseExportAsync` sends `allowContinuation` only when a `ContinuationToken` is already supplied, so a first run cannot obtain a continuation token. Needs an SDK change adding an opt-in `allowContinuation` flag (and returning `ExportProgress.ContinuationToken` after download).

## Risks and assumptions

- Dataverse delta-link detection relies on the `$deltatoken` parameter in the opaque token (SDK returns one token type for both next and delta links). Documented in `docs/SDK-FEED.md`.
- SAP uses a caller-supplied pre-generated SAML assertion; the CLI never signs assertions. A long-running export outliving the assertion's validity is not handled.
- Hosted CI needs the repository secret `SDK_READ_TOKEN` (read access to `kkora/koras-connect-sdk`); the default token cannot read it. Until set, CI fails at SDK checkout.
- Live vendor behaviour (entitlements, real payload shapes, rate limits) is unverified.
