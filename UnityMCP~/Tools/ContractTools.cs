using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class ContractTools
{
    [McpServerTool(Name = "unity_register_contracts")]
    [Description("Register runtime contracts (invariants) that are automatically checked every other frame during play mode. " +
        "Contracts define declarative assertions on component fields/properties. Max 20 contracts. " +
        "On violation, a 'contract_violation' event is pushed to the event stream. " +
        "Contracts are auto-cleared when play mode stops. " +
        "Example: unity_register_contracts(contracts=[{name:\"player_on_track\",instanceId:1234,componentType:\"Transform\",field:\"position.y\",op:\">=\",expected:\"-1\",severity:\"error\"}])")]
    public static async Task<string> RegisterContracts(
        UnityClient client,
        [Description("JSON array of contract definitions. Each: {name, instanceId, componentType, field, op, expected, severity}. " +
            "Operators: >=, <=, ==, !=, >, <, in_range (expected: \"[min,max]\"), not_null. " +
            "Field supports dotted paths: position.y, localScale.x. " +
            "Severity: info, warning, error.")] string contracts)
    {
        if (string.IsNullOrWhiteSpace(contracts))
        {
            return ToolErrors.ValidationError("contracts JSON array is required");
        }

        return await client.RegisterContractsAsync(new { contracts = System.Text.Json.JsonSerializer.Deserialize<object[]>(contracts) });
    }

    [McpServerTool(Name = "unity_query_contracts")]
    [Description("Query the current state of all registered runtime contracts. Returns per-contract pass/fail/error counts and last actual value. " +
        "Use after registering contracts and running play mode to verify invariants held.")]
    public static async Task<string> QueryContracts(UnityClient client)
    {
        return await client.QueryContractsAsync();
    }

    [McpServerTool(Name = "unity_clear_contracts")]
    [Description("Clear all registered runtime contracts. Contracts are also auto-cleared when play mode stops.")]
    public static async Task<string> ClearContracts(UnityClient client)
    {
        return await client.ClearContractsAsync();
    }
}
