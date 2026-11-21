# SDK feed

The CLI pins the SDK packages at `0.1.0-preview.2` (`KorasConnectSdkVersion` in `Directory.Build.props`). They are
not on nuget.org. `SDK-COMMIT` records the exact `koras-connect-sdk` commit those packages are built from.

1. Clone the SDK next to this repository and check out the pinned commit.
2. Run `eng/build-sdk-feed.sh <sdk>` (or `.ps1`). It runs `dotnet pack Koras.Connect.slnx -c Release` and writes the
   eleven `Koras.Connect.*` packages to `artifacts/sdk-feed/` (git-ignored).
3. `nuget.config` maps `Koras.Connect*` to that folder only (package source mapping), and everything else to nuget.org.
   A stray `Koras.Connect` package on nuget.org can therefore never be substituted.

When the SDK publishes packages, remove the local source from `nuget.config` and keep the version pin.

Bumping the SDK: check out the new SDK commit, update `SDK-COMMIT` and `KorasConnectSdkVersion`, rebuild the feed, run
the tests.

SDK facts the CLI relies on, recorded at the pinned commit:

- `Koras.Connect.Export.JsonExportProvider` ignores the `ExportSchema` and serializes the raw record. The CLI therefore
  writes JSON itself from the selected columns.
- `ResponseExportOptions` (Qualtrics) has no `AllowContinuation` flag; `StartResponseExportAsync` only sends
  `allowContinuation` when a continuation token is supplied, so a first incremental Qualtrics export cannot obtain a token.
- `DataverseClient.GetChangesAsync` returns the same `ContinuationToken` type for `@odata.nextLink` and
  `@odata.deltaLink`; the CLI tells them apart by the `$deltatoken` parameter in the opaque value.
