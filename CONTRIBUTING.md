# Contributing to Agent Bridge for Unity

Thanks for your interest in contributing!

## Adding a New Tool

Tools live in two layers:

1. **MCP tool** (`UnityMCP~/Tools/*.cs`) — defines the MCP schema and calls the Unity bridge HTTP endpoint
2. **Bridge route** (`Editor/UnityAgentBridge/UnityCommands.*.cs`) — implements the actual Unity Editor logic

### Steps

1. Add a `[BridgeRoute]`-annotated method in the appropriate `UnityCommands.*.cs` partial class
2. Add the corresponding MCP tool in `UnityMCP~/Tools/*.cs` with `[McpServerTool]`
3. Update `docs/agent-shared/tool-list.md` with the new tool
4. If the tool introduces a new workflow, update `docs/agent-shared/workflows.md`

### Code Style

- Use `partial class UnityCommands` to keep files focused by domain
- Route methods return `(string body, int statusCode)` tuples
- Use the existing `ParseInt`, `ParseBool`, `ParseFloat` helpers for query parameters
- Follow the error envelope contract in `docs/agent-shared/error-envelope.md`

## Pull Request Process

1. Fork the repo and create a feature branch
2. Make your changes
3. Run `dotnet build UnityMCP~/UnityMCP.csproj` to verify compilation
4. Ensure no project-specific references leaked in (grep for common patterns)
5. Open a PR with a clear description of what the tool does and why

## Reporting Issues

Open a GitHub issue with:
- Unity version
- .NET SDK version
- Steps to reproduce
- Expected vs actual behavior
