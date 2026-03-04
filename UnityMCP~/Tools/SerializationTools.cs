using System.ComponentModel;
using ModelContextProtocol.Server;

namespace UnityMCP.Tools;

[McpServerToolType]
public static class SerializationTools
{
    [McpServerTool(Name = "unity_set_managed_reference")]
    [Description("Set a SerializeReference field on a component to a new typed instance. SerializeReference fields support polymorphic serialization of managed objects.")]
    public static async Task<string> SetManagedReference(
        UnityClient client,
        [Description("The instance ID of the GameObject containing the component.")] int instanceId,
        [Description("The property path of the SerializeReference field (e.g., 'myAbility' or 'abilities.Array.data[0]').")] string propertyPath,
        [Description("The full type name of the class to instantiate (e.g., 'MyNamespace.FireballAbility' or just 'FireballAbility').")] string typeName,
        [Description("Optional JSON data to populate the instance fields (e.g., '{\"damage\":50,\"range\":10}').")] string? jsonData = null)
    {
        if (instanceId == 0)
        {
            return ToolErrors.ValidationError("Instance ID is required");
        }

        if (string.IsNullOrWhiteSpace(propertyPath))
        {
            return ToolErrors.ValidationError("Property path is required");
        }

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return ToolErrors.ValidationError("Type name is required");
        }

        return await client.SetManagedReferenceAsync(instanceId, propertyPath, typeName, jsonData);
    }

    [McpServerTool(Name = "unity_get_derived_types")]
    [Description("Find all types that derive from or implement a base type/interface. Useful for discovering what types can be assigned to a SerializeReference field.")]
    public static async Task<string> GetDerivedTypes(
        UnityClient client,
        [Description("The base type or interface name to find implementations of (e.g., 'IAbility', 'BaseWeapon', or 'MyNamespace.IEffect').")] string baseTypeName)
    {
        if (string.IsNullOrWhiteSpace(baseTypeName))
        {
            return ToolErrors.ValidationError("Base type name is required");
        }

        return await client.GetDerivedTypesAsync(baseTypeName);
    }
}
