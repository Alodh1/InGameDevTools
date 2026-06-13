# AGENTS.md

This workspace is the `ingamedevtools` Vintage Story mod. It is a C#/.NET project with an ImGui-based in-game tool suite for editing animations, transforms, models, particles, recipes, loot/drop data, worldgen data, patches, Entity AI, ConfigLib defaults, settings, and block/item JSON.

## Non-negotiable Workflow

- Preserve user work. The tree is often dirty; inspect `git status --short` before editing and do not revert unrelated changes.
- When a meaningful change is completed, add a brief, plain change note to `docs/changelog.md`. Only include changes that make sense in a changelog; skip build/deploy churn, status-only work, and trivial internal edits.
- The user writes requested work into a document named `todo`. Treat that document as an active task source when present or when the user refers to it.
- When working from task documents, mark each task done as it is completed, and mark the whole document done when every task in it is finished.
- After changes that affect built code, assets, project files, tests, packaging, or runtime behavior, build and deploy the mod to the active Vintage Story Mods folder before final handoff unless the user explicitly says not to.
- After completed changes, push the scoped finished work to the configured GitHub remote as well unless the user explicitly says not to. In dirty worktrees, stage and push only files belonging to the current task; never include unrelated user changes.
- If the work only changes docs, task notes, changelog text, agent instructions, or other files that do not affect the built mod, building and deploying are not needed.
- Use Debug builds for normal dev iteration when build/deploy is needed. This workspace's parent `Directory.Build.targets` redirects packages to `..\release`; older local packages may still exist under `.\Releases`, so deploy the newest generated dev zip from either location:

```powershell
dotnet build .\InGameDevTools.csproj -c Debug
New-Item -ItemType Directory -Force "$env:APPDATA\VintagestoryData\Mods" | Out-Null
$modsDir = Resolve-Path "$env:APPDATA\VintagestoryData\Mods"
$devZip = Get-ChildItem -Path ..\release, .\Releases -Filter "ingamedevtools_*_dev.zip" -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-ChildItem -LiteralPath $modsDir -Filter "ingamedevtools*.zip" -File | Remove-Item
Copy-Item -LiteralPath $devZip.FullName -Destination $modsDir -Force
```

- Active Mods folder on this machine: `C:\Users\caiaf\AppData\Roaming\VintagestoryData\Mods`.
- If the user identifies a different active Mods folder, deploy there instead.
- Run focused tests when changing test-covered utilities or editor serialization logic:

```powershell
dotnet test .\tests\InGameDevTools.Tests\InGameDevTools.Tests.csproj -c Debug
```

## Build Prerequisites

- Target framework is `net10.0`.
- The project references Vintage Story assemblies from `$(GameDirectory)`.
- `GameDirectory` defaults first to the `VINTAGE_STORY` environment variable, then to `%APPDATA%\Vintagestory`.
- If the default is wrong, pass it explicitly:

```powershell
dotnet build .\InGameDevTools.csproj -c Debug -p:GameDirectory="C:\Path\To\Vintagestory"
```

- Python is required because `scripts/package_zip.py` creates the dev/release zip after builds.
- Warnings are treated as errors and nullable reference types are enabled.

## Project Map

- `InGameDevTools.csproj`: mod metadata, build output layout, Vintage Story references, asset copying, and zip packaging.
- `source/InGameDevToolsModSystem.cs`: mod lifecycle entry point. Registers standalone classes, Harmony patches, config, managers, hotkeys, font extraction, and cleanup.
- `source/InGameDevTools/Animations/DebugWindowManager*.cs`: main VSImGui window and most editor tab orchestration. It is a large partial class; keep changes local to the relevant partial file when possible.
- `source/InGameDevTools/Animations/*Editor*.cs`: individual editors for recipes, block/item JSON, loot/drop data, worldgen, Entity AI, ConfigLib, transforms, models, particles, and vanilla animations.
- `source/InGameDevTools/Integration`: Harmony patches and transpilers that connect the tools to live Vintage Story runtime behavior.
- `source/InGameDevTools/Utils`: reusable helpers. Prefer putting pure logic here when it can be unit tested.
- `source/InGameDevTools/StandaloneStubs.cs`: local compatibility stubs for classes normally supplied by companion mods/libraries.
- `Resources/assets`: mod assets copied to the built mod package.
- `tests/InGameDevTools.Tests`: xUnit tests for parsing, serialization, math, timing, asset indexing, text helpers, batching, and other utility-heavy behavior.
- `docs/editor-missing-feature-audit.md`: living editor coverage audit. Update it when work changes first-class editor coverage or closes/opens a feature gap.

## Runtime And Editor Conventions

- The UI is immediate-mode ImGui through VSImGui. Keep labels stable and use `##id` suffixes where visible labels can repeat.
- `Ctrl+Shift+L` toggles the external devtools UI. The command palette is opened with the in-window button or `Ctrl+P`.
- Avoid throwing out of draw/update paths. Prefer guarded runtime reads, clear status strings, and `LoggerUtil` for diagnostics.
- Source-save features usually build a previewable `SourceSaveRequest`/`SourceSaveResult` and then commit through the existing queue/status flow. Reuse that pattern instead of writing files directly from random UI code.
- Authored files saved by the tools live under `%APPDATA%\VintagestoryData\InGameDevTools\<asset-type>\assets\...` and are indexed beside loaded game assets where relevant.
- Many runtime integrations use reflection or Harmony against Vintage Story internals. Keep patch changes narrow, null-safe, and disposable/unpatchable.
- Preserve raw/unknown JSON fields unless a feature explicitly owns them. Many editors combine first-class controls with round-tripping of extra metadata.

## Testing Guidance

- Add or update xUnit tests for pure helpers, JSON transforms, serialization, matching/indexing, timing, and math.
- UI-only ImGui work is often hard to unit test; still test extracted parsing/formatting/state logic where practical.
- If tests cannot run because the local Vintage Story assemblies or .NET SDK are missing, report the exact blocker and the command attempted.

## Packaging Notes

- Debug build output is staged under `bin\Debug\Mods\ingamedevtools\`.
- Release build output is staged under `bin\Release\Mods\ingamedevtools\`.
- Debug builds package `..\release\ingamedevtools_<version>_dev.zip` because of the parent `Directory.Build.targets` override.
- Release builds package `..\release\ingamedevtools_<version>.zip` because of the parent `Directory.Build.targets` override.
- Older packages under `.\Releases` are historical local artifacts.
- Deployment for normal iteration means copying the newest Debug `_dev.zip` into the active Vintage Story data Mods folder and removing older active `ingamedevtools*.zip` files so Vintage Story does not load duplicate versions.
