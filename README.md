# AIGuiders.RoslynMcp.Core

Библиотека **.NET 10** (`AIGuiders.RoslynMcp.Core`): общий слой Roslyn workspace для **RoslynMcp** (stdio MCP) и **Cascade IDE** (in-process Monaco / AEE).

Один источник правды для code actions, rename, diagnostics, find usages, workspace navigation — без дублирования `Microsoft.CodeAnalysis.CSharp.Features` в каждом хосте.

Лицензия: MIT. Авторство пакета: LonelySoul / AIGuiders.

## Зачем

| Потребитель | Транспорт |
|-------------|-----------|
| `RoslynMcp.exe` | stdio MCP → тонкая обёртка над Core |
| Cascade IDE | in-proc (живой буфер редактора, multi-file apply) |
| Агент в Cursor | по-прежнему через **exe** MCP, тот же Core внутри |

## Публичный API (основное)

| Область | Типы |
|---------|------|
| Code actions | `CodeActions`, `RoslynEditorCodeAction`, `RoslynEditorApplyResult` |
| Rename | `RenameSymbol` |
| Diagnostics | `GetDiagnostics` |
| Navigation | `GoToDefinition`, `FindUsages`, `GetWorkspaceNavigationContext` |
| Editor bridge | `ListForEditorAsync`, `ApplyForEditorAsync`, `RenameForEditorAsync` |

Подробности — XML-комментарии в `ServiceLayer/` и [roslyn-mcp](https://github.com/AI-Guiders/RoslynMcp) `docs/MCP-TOOLS.md`.

## Установка (NuGet)

```bash
dotnet add package AIGuiders.RoslynMcp.Core --version 0.1.0
```

## Сборка локально

```bash
dotnet build RoslynMcp.Core.csproj -c Release
dotnet pack RoslynMcp.Core.csproj -c Release -o ./nupkg
```

## Публикация на nuget.org (Trusted Publishing)

1. На nuget.org → Trusted Publishing → GitHub:
   - Owner: `AI-Guiders` (или `KarataevDmitry` — как у остальных пакетов org)
   - Repository: `roslyn-mcp-core`
   - Workflow file: `publish-nuget.yml`
   - User: `LonelySoul`

2. Тег `v0.1.0` или workflow_dispatch с версией `0.1.0`.

## Репозитории

| Репозиторий | Роль |
|-------------|------|
| [roslyn-mcp-core](https://github.com/AI-Guiders/roslyn-mcp-core) | эта библиотека |
| [RoslynMcp](https://github.com/AI-Guiders/RoslynMcp) | MCP stdio host |
