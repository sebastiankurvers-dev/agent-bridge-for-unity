using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Profiling;
using Unity.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Audio;
using TMPro;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        #region Tag and Layer Operations

        public static string CreateTag(string tagName)
        {
            if (string.IsNullOrEmpty(tagName))
            {
                return JsonError("Tag name is required");
            }

            // Check if tag already exists
            var existingTags = UnityEditorInternal.InternalEditorUtility.tags;
            if (existingTags.Contains(tagName))
            {
                return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", $"Tag '{tagName}' already exists" }, { "existed", true } });
            }

            try
            {
                var tagManager = new SerializedObject(
                    AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                var tagsProp = tagManager.FindProperty("tags");

                // Find first empty slot or add new
                int insertIndex = tagsProp.arraySize;
                for (int i = 0; i < tagsProp.arraySize; i++)
                {
                    if (string.IsNullOrEmpty(tagsProp.GetArrayElementAtIndex(i).stringValue))
                    {
                        insertIndex = i;
                        break;
                    }
                }

                if (insertIndex == tagsProp.arraySize)
                {
                    tagsProp.InsertArrayElementAtIndex(insertIndex);
                }

                tagsProp.GetArrayElementAtIndex(insertIndex).stringValue = tagName;
                tagManager.ApplyModifiedProperties();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "tag", tagName }, { "message", $"Created tag '{tagName}'" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        public static string CreateLayer(string layerName, int layerIndex = -1)
        {
            if (string.IsNullOrEmpty(layerName))
            {
                return JsonError("Layer name is required");
            }

            try
            {
                var tagManager = new SerializedObject(
                    AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
                var layersProp = tagManager.FindProperty("layers");

                // Check if layer already exists
                for (int i = 0; i < layersProp.arraySize; i++)
                {
                    if (layersProp.GetArrayElementAtIndex(i).stringValue == layerName)
                    {
                        return JsonResult(new Dictionary<string, object> { { "success", true }, { "message", $"Layer '{layerName}' already exists at index {i}" }, { "existed", true }, { "index", i } });
                    }
                }

                // Find empty user layer (8-31)
                int targetIndex = layerIndex >= 8 && layerIndex <= 31 ? layerIndex : -1;

                if (targetIndex == -1)
                {
                    for (int i = 8; i < 32; i++)
                    {
                        if (string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(i).stringValue))
                        {
                            targetIndex = i;
                            break;
                        }
                    }
                }

                if (targetIndex == -1)
                {
                    return JsonError("No available layer slots (layers 8-31 are all used)");
                }

                if (!string.IsNullOrEmpty(layersProp.GetArrayElementAtIndex(targetIndex).stringValue))
                {
                    return JsonError($"Layer index {targetIndex} is already in use");
                }

                layersProp.GetArrayElementAtIndex(targetIndex).stringValue = layerName;
                tagManager.ApplyModifiedProperties();

                return JsonResult(new Dictionary<string, object> { { "success", true }, { "layer", layerName }, { "index", targetIndex }, { "message", $"Created layer '{layerName}' at index {targetIndex}" } });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/tag", Category = "project", Description = "Get all tags", ReadOnly = true)]
        public static string GetTags()
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return JsonResult(new Dictionary<string, object> { { "tags", tags.ToList() } });
        }

        [BridgeRoute("GET", "/layer", Category = "project", Description = "Get all layers", ReadOnly = true)]
        public static string GetLayers()
        {
            var layers = new List<LayerData>();
            for (int i = 0; i < 32; i++)
            {
                var layerName = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(layerName))
                {
                    layers.Add(new LayerData
                    {
                        index = i,
                        name = layerName,
                        isBuiltIn = i < 8
                    });
                }
            }
            var jsonParts = layers.Select(l => JsonUtility.ToJson(l));
            return "{\"layers\":[" + string.Join(",", jsonParts) + "]}";
        }

        #endregion
    }
}
