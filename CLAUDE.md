# Logyx DataHub (TBC Bank)

**Read `docs/PROJECT.md` first.** It is the single project reference: context, TBC's requirements,
the decision log, architecture, step status and resume point, how to run locally, open items,
security rules and lessons learned. Keep it up to date and commit it with the work it describes.

Ground rules (details in `docs/PROJECT.md` section 9):
- ORIS sample files (`Desktop\logyx\`) are real customer data: never commit `.tps`, `.csv`, `.zip`,
  `.xlsx`, `.bak`, nor `bin/`, `obj/`, `.local/`. Check the staged files before every commit.
- No secrets in `appsettings.json`, in `docs/PROJECT.md` or anywhere else in the repo.
- Files with Georgian text: don't edit them with PowerShell 5.1 `Get-Content`/`Set-Content` (ANSI).
- Build and test: `dotnet build LogyxDataHub.sln` (0 warnings expected), `dotnet test LogyxDataHub.sln`.
