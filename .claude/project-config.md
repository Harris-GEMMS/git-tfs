# Project Config

Configured on: 2026-09-01
Configured with: init-project v2.2.0

Single source of truth for project-specific values. To reuse this orchestration in another project,
edit ONLY the Value column here; the plugins resolve from it and carry no hardcoded project
identifiers. Values constant across the business unit (labels vocabulary, estimation rubric,
process conventions) live in the plugins, not here.

## Identity & Atlassian
| Placeholder | Description | Example (C#) | Value |
|---|---|---|---|
| `{{PROJECT_NAME}}` | Human-readable Jira project name | `GEMMS Fax`, `DigiChart` | GEMMS |
| `{{PROJECT_KEY}}` | Jira project key | `GF`, `DGI` | GEM |
| `{{CLOUD_ID}}` | Atlassian cloud ID (find via Jira MCP `getAccessibleAtlassianResources`) | `00000000-0000-0000-0000-000000000000` | 282911cc-81bd-4f3b-96b7-2f3c78e3c4a7 |
| `{{STORY_POINTS_FIELD}}` | Jira story-points custom field | `customfield_10016` | customfield_10031 |
| `{{AGENT_PLAN_FIELD}}` | Jira agent-plan custom field | `customfield_10322` | customfield_10388 |
| `{{CONFLUENCE_SPACE_KEY}}` | Confluence space key for this project's docs (often matches `PROJECT_KEY`, need not) | `GF`, `GEMMSDOC` | G |
| `{{CONFLUENCE_DOCS_FOLDER_ID}}` | Optional default parent folder id for new Confluence pages; blank / `none` = space root | `123456798` | none |

## Workflow status names
Names only. Resolve numeric transition IDs at runtime with `getTransitionsForJiraIssue` (they drift).
| Role | Status name |
|---|---|
| Initial / backlog | Backlog |
| Selected for work | Selected for Development |
| Start work | In Progress |
| Under review | IN TESTING |
| Done | Done |

## Sizing
| Placeholder | Description | Value |
|---|---|---|
| `{{OVERSIZE_THRESHOLD}}` | work-ticket refuses to execute ABOVE this; `none` = never | none |

## Project profile / build
| Placeholder | Description | Example (C#) | Value |
|---|---|---|---|
| `{{TECH_STACK}}` | Short tech stack | `ASP.NET Core 8 Web API + React SPA`; `.NET Framework 4.8 WinForms + DevExpress` | .NET Framework 4.8 CLI (git-tfs, TFS↔git bridge) + VS-integration plugins (VS2015/2017/2019/2022); Paket package management |
| `{{TEST_FRAMEWORK}}` | Primary test framework | `xUnit`, `NUnit`, `MSTest` | xUnit |
| `{{TEST_COMMAND}}` | Test suite command (agent-runnable CLI; `dotnet` accepts `.sln` or `.slnx`) | `dotnet test MyApp.sln` | dotnet test src/GitTfs.sln |
| `{{BUILD_CHECK_COMMAND}}` | Build / type check (compile IS the type check in C#) | `dotnet build MyApp.sln` | dotnet build src/GitTfs.sln |
| `{{E2E_FRAMEWORK}}` | E2E framework | `Playwright` (web); `FlaUI` / `WinAppDriver` (WinForms/WPF) | (none) |
| `{{VS_SOLUTION_DOCS}}` | Attach superpowers spec/plan docs to VS solution folders so they are reviewable in Visual Studio: `ask` (default; work-ticket prompts once, then records `yes`/`no`) / `yes` / `no`. Default `no` when the project has no `.sln`/`.slnx`. | `ask` | no |
