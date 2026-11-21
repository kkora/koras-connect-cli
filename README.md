# Koras Connect CLI

`koras-connect` is a .NET 10 command-line tool that exports data from enterprise systems using the
[Koras Connect SDK](https://github.com/kkora/koras-connect-sdk). This repository holds no SDK source and no customer data.

Milestone 1 provides an installable tool with `--help`/`--version`, named profiles with credential references,
`doctor`, and `adp workers export` to CSV/JSON/XML/XLSX with safe file publication. Build the pinned SDK feed first:
`eng/build-sdk-feed.sh ../koras-connect-sdk` (see `docs/SDK-FEED.md`). The full documentation arrives with the final
milestone commit; later milestones add SAP SuccessFactors, Qualtrics, Dataverse, Salesforce, Microsoft Graph and
incremental exports.
