# AIGuiders.RoslynMcp.Core

Shared **.NET 10** library for C# semantic tooling: Roslyn workspace (MSBuild), code actions, rename, diagnostics, find usages, workspace navigation.

Used by [RoslynMcp](https://github.com/AI-Guiders/RoslynMcp) (stdio MCP for agents) and IDE hosts such as Cascade IDE (in-process editor refactorings).

**License:** MIT

## Install

```bash
dotnet add package AIGuiders.RoslynMcp.Core
```

Requires **.NET 10** and a solution or project path (`.sln` / `.csproj`) for workspace operations.

## Main API (`RoslynMcp.ServiceLayer`)

| Area | Entry points |
|------|----------------|
| Code actions | `CodeActions.GetCodeActionsAsync`, `ApplyCodeActionAsync`; editor: `ListForEditorAsync`, `ApplyForEditorAsync` |
| Rename | `RenameSymbol.RenameAsync`; editor: `RenameForEditorAsync` |
| Diagnostics | `GetDiagnostics` |
| Navigation | `GoToDefinition`, `FindUsages`, `GetWorkspaceNavigationContext` |

Tool names and MCP arguments are documented in the [RoslynMcp](https://github.com/AI-Guiders/RoslynMcp) repository.

## Links

- Source: [github.com/AI-Guiders/roslyn-mcp-core](https://github.com/AI-Guiders/roslyn-mcp-core)
- MCP host: [github.com/AI-Guiders/RoslynMcp](https://github.com/AI-Guiders/RoslynMcp)
