# .NET refactoring

`.editorconfig` is the source of truth for C# naming. Roslynator is pinned as a
repository-local .NET tool in `dotnet-tools.json`.

## Workflow

1. Run `dotnet tool restore` and confirm the working tree before editing.
2. Establish a baseline with `dotnet build Heartbeat.slnx --no-restore`.
3. Inventory naming diagnostics without changing files:
   `dotnet format style Heartbeat.slnx --diagnostics IDE1006 --verify-no-changes --no-restore`.
4. For a solution-wide rename, use `dotnet roslynator rename-symbol --help`, then
   run the chosen command with `--dry-run` before applying it. Review every
   affected project and reference.
5. Apply cohesive, reviewable batches. Treat public APIs, serialized DTO members,
   database mappings, native interop declarations, and externally consumed names
   as compatibility boundaries rather than cosmetic identifiers.
6. Complete the batch only after the naming inventory is understood, the solution
   builds, and affected tests pass. Run the full test suite when the rename crosses
   project boundaries.

The repository intentionally does not enforce an `Async` suffix because existing
production and test naming conventions differ. Native ABI declarations listed in
`.editorconfig` retain their platform names.
