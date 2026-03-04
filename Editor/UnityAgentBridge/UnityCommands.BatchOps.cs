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
        [BridgeRoute("POST", "/scene/patch-batch", Category = "scene", Description = "Batch scene patch with review/approval", TimeoutDefault = 30000, TimeoutMin = 500, TimeoutMax = 120000)]
        public static string ApplyScenePatchBatch(string jsonData)
        {
            try
            {
                var request = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (request == null)
                {
                    return JsonError("Failed to parse batch request");
                }

                if (!(request.TryGetValue("operations", out var operationsObj) && operationsObj is IList<object> operations))
                {
                    return JsonError("operations array is required");
                }

                bool reviewOnly = ReadBool(request, "reviewOnly", false);
                bool dryRun = ReadBool(request, "dryRun", false);
                bool effectiveDryRun = dryRun || reviewOnly;
                bool atomic = ReadBool(request, "atomic", false);
                bool rollbackOnFail = ReadBool(request, "rollbackOnFail", atomic);
                bool brief = ReadBool(request, "brief", false);
                bool diffOnly = ReadBool(request, "diffOnly", false);
                bool autoCheckpoint = ReadBool(request, "autoCheckpoint", atomic || rollbackOnFail);
                bool createCheckpoint = ReadBool(request, "createCheckpoint", autoCheckpoint);
                bool autoSaveScene = ReadBool(request, "autoSaveScene", false);
                string checkpointName = request.TryGetValue("checkpointName", out var cpNameObj) ? cpNameObj?.ToString() : null;
                bool requireApproval = ReadBool(request, "requireApproval", false);
                string approvedReviewHash = ReadString(request, "approvedReviewHash");

                var parsedOperations = ParseBatchOperations(operations);
                string reviewHash = BuildBatchApprovalHash(parsedOperations, atomic, rollbackOnFail, createCheckpoint);
                var risk = EvaluateBatchRisk(parsedOperations, atomic, rollbackOnFail, createCheckpoint, effectiveDryRun);

                if (!effectiveDryRun && requireApproval && string.IsNullOrWhiteSpace(approvedReviewHash))
                {
                    return JsonResult(new Dictionary<string, object>
                    {
                        { "success", false },
                        { "error", "requireApproval=true but approvedReviewHash is missing" },
                        { "reviewHash", reviewHash },
                        { "riskScore", risk.score },
                        { "riskLevel", risk.level },
                        { "riskReasons", risk.reasons },
                        { "riskSignals", risk.signals }
                    });
                }

                if (!effectiveDryRun && !string.IsNullOrWhiteSpace(approvedReviewHash))
                {
                    if (!string.Equals(approvedReviewHash.Trim(), reviewHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonResult(new Dictionary<string, object>
                        {
                            { "success", false },
                            { "error", "approvedReviewHash does not match the current batch payload" },
                            { "reviewHash", reviewHash },
                            { "riskScore", risk.score },
                            { "riskLevel", risk.level },
                            { "riskReasons", risk.reasons },
                            { "riskSignals", risk.signals }
                        });
                    }
                }

                string checkpointId = null;
                bool checkpointCreated = false;
                if (!effectiveDryRun && createCheckpoint)
                {
                    var cpName = string.IsNullOrWhiteSpace(checkpointName)
                        ? $"scene-patch-{DateTime.Now:yyyyMMdd-HHmmss}"
                        : checkpointName;
                    var cpJson = CheckpointManager.CreateCheckpoint(cpName);
                    checkpointId = ExtractCheckpointId(cpJson);
                    checkpointCreated = !string.IsNullOrEmpty(checkpointId);

                    if (atomic && !checkpointCreated)
                    {
                        return JsonError("Atomic batch requires a checkpoint but checkpoint creation failed");
                    }
                }

                int applied = 0;
                int failed = 0;
                bool rolledBack = false;
                var operationResults = new List<object>();
                var compactResults = new List<object>();
                var changedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < parsedOperations.Count; i++)
                {
                    var parsedOperation = parsedOperations[i];
                    if (!parsedOperation.isObject)
                    {
                        failed++;
                        var malformed = new Dictionary<string, object>
                        {
                            { "index", i },
                            { "operation", "(invalid)" },
                            { "success", false },
                            { "error", "Operation must be an object" }
                        };
                        operationResults.Add(malformed);
                        compactResults.Add(malformed);
                        if (atomic) break;
                        continue;
                    }

                    var result = ExecuteBatchOperation(parsedOperation.operation, parsedOperation.payload, effectiveDryRun);

                    bool success = ReadBool(result, "success", false);
                    var entry = new Dictionary<string, object>(result)
                    {
                        { "index", i },
                        { "operation", parsedOperation.operation }
                    };
                    operationResults.Add(entry);

                    var compactEntry = new Dictionary<string, object>
                    {
                        { "index", i },
                        { "operation", parsedOperation.operation },
                        { "success", success }
                    };
                    if (!success && result.TryGetValue("error", out var compactError))
                    {
                        compactEntry["error"] = compactError;
                    }
                    compactResults.Add(compactEntry);

                    if (success)
                    {
                        applied++;
                        CollectChangedTargets(result, changedTargets);
                    }
                    else
                    {
                        failed++;
                        if (atomic)
                        {
                            break;
                        }
                    }
                }

                if (!effectiveDryRun && failed > 0 && rollbackOnFail && !string.IsNullOrEmpty(checkpointId))
                {
                    var restoreJson = CheckpointManager.RestoreCheckpoint(checkpointId);
                    var restoreResult = MiniJSON.Json.Deserialize(restoreJson) as Dictionary<string, object>;
                    rolledBack = restoreResult != null && ReadBool(restoreResult, "success", false);
                }

                bool sceneSaved = false;
                string sceneSaveError = null;
                if (!effectiveDryRun && autoSaveScene && failed == 0 && !rolledBack)
                {
                    sceneSaved = TrySaveActiveScene(out sceneSaveError);
                }

                var response = new Dictionary<string, object>
                {
                    { "success", failed == 0 },
                    { "dryRun", dryRun },
                    { "reviewOnly", reviewOnly },
                    { "effectiveDryRun", effectiveDryRun },
                    { "atomic", atomic },
                    { "rollbackOnFail", rollbackOnFail },
                    { "autoSaveScene", autoSaveScene },
                    { "autoCheckpoint", createCheckpoint },
                    { "rolledBack", rolledBack },
                    { "sceneSaved", sceneSaved },
                    { "checkpointCreated", checkpointCreated },
                    { "checkpointId", checkpointId },
                    { "operationCount", operations.Count },
                    { "appliedCount", applied },
                    { "failedCount", failed },
                    { "requireApproval", requireApproval },
                    { "approvalVerified", !effectiveDryRun && !string.IsNullOrWhiteSpace(approvedReviewHash) },
                    { "reviewHash", reviewHash },
                    { "riskScore", risk.score },
                    { "riskLevel", risk.level },
                    { "riskReasons", risk.reasons },
                    { "riskSignals", risk.signals }
                };

                if (!string.IsNullOrWhiteSpace(sceneSaveError))
                {
                    response["sceneSaveError"] = sceneSaveError;
                }

                if (diffOnly)
                {
                    response["changedTargets"] = changedTargets.Cast<object>().ToList();
                }

                if (brief)
                {
                    response["results"] = compactResults;
                }
                else
                {
                    response["results"] = operationResults;
                }

                if (reviewOnly)
                {
                    response["applyHints"] = new Dictionary<string, object>
                    {
                        { "approvedReviewHash", reviewHash },
                        { "requireApproval", true },
                        { "atomic", atomic },
                        { "rollbackOnFail", rollbackOnFail },
                        { "autoCheckpoint", createCheckpoint }
                    };
                }

                return JsonResult(response);
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private class BatchOperationRequest
        {
            public int index;
            public bool isObject;
            public string operation;
            public Dictionary<string, object> payload;
        }

        private class BatchRiskReport
        {
            public int score;
            public string level;
            public List<object> reasons;
            public Dictionary<string, object> signals;
        }

        private static List<BatchOperationRequest> ParseBatchOperations(IList<object> operations)
        {
            var parsed = new List<BatchOperationRequest>();
            if (operations == null) return parsed;

            for (int i = 0; i < operations.Count; i++)
            {
                var operationObj = operations[i] as Dictionary<string, object>;
                if (operationObj == null)
                {
                    parsed.Add(new BatchOperationRequest
                    {
                        index = i,
                        isObject = false,
                        operation = "(invalid)",
                        payload = new Dictionary<string, object>()
                    });
                    continue;
                }

                string rawOperation = ReadString(operationObj, "op")
                                     ?? ReadString(operationObj, "operation")
                                     ?? ReadString(operationObj, "type");
                string normalizedOperation = NormalizeBatchOperation(rawOperation);

                parsed.Add(new BatchOperationRequest
                {
                    index = i,
                    isObject = true,
                    operation = string.IsNullOrWhiteSpace(normalizedOperation) ? "(missing-op)" : normalizedOperation,
                    payload = ExtractOperationPayload(operationObj) ?? new Dictionary<string, object>()
                });
            }

            return parsed;
        }

        private static BatchRiskReport EvaluateBatchRisk(
            IList<BatchOperationRequest> operations,
            bool atomic,
            bool rollbackOnFail,
            bool createCheckpoint,
            bool effectiveDryRun)
        {
            int operationCount = operations?.Count ?? 0;
            int invalidCount = 0;
            int globalOperationCount = 0;
            int estimatedObjectTouches = 0;
            int estimatedComponentTouches = 0;
            int estimatedAssetWrites = 0;
            double baseRiskSum = 0d;

            if (operations != null)
            {
                foreach (var operation in operations)
                {
                    if (operation == null || !operation.isObject)
                    {
                        invalidCount++;
                        continue;
                    }

                    bool globalOperation;
                    int objectTouches;
                    int componentTouches;
                    int assetWrites;

                    EstimateBatchOperationImpact(
                        operation.operation,
                        operation.payload,
                        out globalOperation,
                        out objectTouches,
                        out componentTouches,
                        out assetWrites);

                    baseRiskSum += GetBatchOperationBaseRisk(operation.operation);
                    if (globalOperation) globalOperationCount++;
                    estimatedObjectTouches += objectTouches;
                    estimatedComponentTouches += componentTouches;
                    estimatedAssetWrites += assetWrites;
                }
            }

            double typeRisk = operationCount > 0 ? baseRiskSum / Math.Max(1, operationCount) : 0d;
            double blastRisk = Clamp01(
                (estimatedObjectTouches / 24d * 0.6d)
                + (estimatedComponentTouches / 30d * 0.3d)
                + (estimatedAssetWrites / 8d * 0.1d));
            double globalRisk = Clamp01(globalOperationCount / 2d);

            double runtimePenalty = 0d;
            if (EditorApplication.isCompiling) runtimePenalty += 0.18d;
            if (EditorApplication.isPlayingOrWillChangePlaymode) runtimePenalty += 0.12d;

            double reversibilityPenalty = 0d;
            double reversibilityBonus = 0d;
            if (!effectiveDryRun)
            {
                if (!createCheckpoint) reversibilityPenalty += 0.15d;
                if (!rollbackOnFail) reversibilityPenalty += 0.12d;
                if (createCheckpoint && rollbackOnFail) reversibilityBonus = 0.10d;
            }

            double invalidPenalty = invalidCount > 0 ? Math.Min(0.2d, invalidCount * 0.05d) : 0d;

            double combined = Clamp01(
                typeRisk * 0.45d
                + blastRisk * 0.25d
                + globalRisk * 0.20d
                + runtimePenalty
                + reversibilityPenalty
                + invalidPenalty
                - reversibilityBonus);

            int score = (int)Math.Round(combined * 100d, MidpointRounding.AwayFromZero);
            string level = score >= 85 ? "critical" : score >= 65 ? "high" : score >= 35 ? "medium" : "low";

            var reasons = new List<object>();
            if (globalOperationCount > 0)
            {
                reasons.Add($"Includes {globalOperationCount} global operation(s) (render/camera/volume) that can affect scene-wide visuals.");
            }
            if (estimatedObjectTouches >= 15)
            {
                reasons.Add($"High estimated object blast radius ({estimatedObjectTouches} object touches).");
            }
            if (estimatedComponentTouches >= 18)
            {
                reasons.Add($"High estimated component/property blast radius ({estimatedComponentTouches} touches).");
            }
            if (estimatedAssetWrites > 0)
            {
                reasons.Add($"Potential asset write scope detected ({estimatedAssetWrites} write-like operation units).");
            }
            if (invalidCount > 0)
            {
                reasons.Add($"{invalidCount} malformed operation(s) in batch payload.");
            }
            if (EditorApplication.isCompiling)
            {
                reasons.Add("Unity is compiling; mutation timing is less predictable.");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                reasons.Add("Play mode is active or transitioning; runtime/editor state may diverge.");
            }
            if (!effectiveDryRun && !createCheckpoint)
            {
                reasons.Add("No checkpoint is planned before mutation, reducing reversibility.");
            }
            if (!effectiveDryRun && !rollbackOnFail)
            {
                reasons.Add("rollbackOnFail is disabled; failed batches may leave partial scene state.");
            }
            if (reasons.Count == 0)
            {
                reasons.Add("Operation set is limited in scope and has strong reversibility safeguards.");
            }

            return new BatchRiskReport
            {
                score = score,
                level = level,
                reasons = reasons,
                signals = new Dictionary<string, object>
                {
                    { "operationCount", operationCount },
                    { "invalidOperationCount", invalidCount },
                    { "globalOperationCount", globalOperationCount },
                    { "estimatedObjectTouches", estimatedObjectTouches },
                    { "estimatedComponentTouches", estimatedComponentTouches },
                    { "estimatedAssetWrites", estimatedAssetWrites },
                    { "baseRiskAverage", Math.Round(typeRisk, 4) },
                    { "blastRisk", Math.Round(blastRisk, 4) },
                    { "globalRisk", Math.Round(globalRisk, 4) },
                    { "isCompiling", EditorApplication.isCompiling },
                    { "playModeActive", EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode },
                    { "atomic", atomic },
                    { "rollbackOnFail", rollbackOnFail },
                    { "checkpointPlanned", createCheckpoint },
                    { "effectiveDryRun", effectiveDryRun }
                }
            };
        }

        private static Dictionary<string, object> ExecuteBatchOperation(string normalizedOperation, Dictionary<string, object> payload, bool dryRun)
        {
            payload ??= new Dictionary<string, object>();

            if (dryRun)
            {
                var validationError = ValidateBatchOperationPayload(normalizedOperation, payload);
                if (!string.IsNullOrEmpty(validationError))
                {
                    return new Dictionary<string, object>
                    {
                        { "success", false },
                        { "dryRun", true },
                        { "error", validationError }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "dryRun", true },
                    { "message", "Validation passed" }
                };
            }

            try
            {
                string opResultJson;
                switch (normalizedOperation)
                {
                    case "spawn":
                        opResultJson = Spawn(MiniJSON.Json.Serialize(payload));
                        break;

                    case "delete_gameobject":
                        if (!TryReadInt(payload, "instanceId", out int deleteId))
                            return ErrorResult("instanceId is required");
                        opResultJson = DeleteGameObject(deleteId);
                        break;

                    case "modify_gameobject":
                        if (!TryReadInt(payload, "instanceId", out int modifyId))
                            return ErrorResult("instanceId is required");
                        opResultJson = ModifyGameObject(modifyId, MiniJSON.Json.Serialize(payload));
                        break;

                    case "add_component":
                        opResultJson = AddComponent(MiniJSON.Json.Serialize(payload));
                        break;

                    case "remove_component":
                        if (!TryReadInt(payload, "instanceId", out int removeId))
                            return ErrorResult("instanceId is required");
                        if (!payload.TryGetValue("componentType", out var removeTypeObj) || string.IsNullOrWhiteSpace(removeTypeObj?.ToString()))
                            return ErrorResult("componentType is required");
                        opResultJson = RemoveComponent(removeId, removeTypeObj.ToString());
                        break;

                    case "modify_component":
                        opResultJson = ModifyComponent(MiniJSON.Json.Serialize(payload));
                        break;

                    case "patch_serialized_properties":
                        opResultJson = PatchSerializedProperties(MiniJSON.Json.Serialize(payload));
                        break;

                    case "set_renderer_materials":
                        opResultJson = SetRendererMaterials(MiniJSON.Json.Serialize(payload));
                        break;

                    case "reparent":
                        opResultJson = ReparentGameObject(MiniJSON.Json.Serialize(payload));
                        break;

                    case "set_render_settings":
                        opResultJson = SetRenderSettings(MiniJSON.Json.Serialize(payload));
                        break;

                    case "set_volume_profile_overrides":
                        opResultJson = SetVolumeProfileOverrides(MiniJSON.Json.Serialize(payload));
                        break;

                    case "set_camera_rendering":
                        opResultJson = SetCameraRendering(MiniJSON.Json.Serialize(payload));
                        break;

                    case "create_light":
                        opResultJson = CreateLight(MiniJSON.Json.Serialize(payload));
                        break;

                    case "modify_light":
                        opResultJson = ModifyLight(MiniJSON.Json.Serialize(payload));
                        break;

                    case "configure_rigidbody":
                        opResultJson = ConfigureRigidbody(MiniJSON.Json.Serialize(payload));
                        break;

                    case "configure_collider":
                        opResultJson = ConfigureCollider(MiniJSON.Json.Serialize(payload));
                        break;

                    case "execute_csharp":
                        opResultJson = ExecuteCSharp(MiniJSON.Json.Serialize(payload));
                        break;

                    default:
                        return ErrorResult($"Unsupported operation: {normalizedOperation}");
                }

                return ParseOperationResult(opResultJson, normalizedOperation);
            }
            catch (Exception ex)
            {
                return ErrorResult(ex.Message);
            }
        }

        private static string ValidateBatchOperationPayload(string normalizedOperation, Dictionary<string, object> payload)
        {
            switch (normalizedOperation)
            {
                case "spawn":
                    if (!(payload.ContainsKey("prefabPath")
                          || payload.ContainsKey("primitiveType")
                          || payload.ContainsKey("name")))
                    {
                        return "spawn requires prefabPath, primitiveType, or name";
                    }
                    break;

                case "delete_gameobject":
                case "modify_gameobject":
                    if (!payload.ContainsKey("instanceId"))
                        return $"{normalizedOperation} requires instanceId";
                    break;

                case "remove_component":
                    if (!payload.ContainsKey("instanceId") || !payload.ContainsKey("componentType"))
                        return "remove_component requires instanceId and componentType";
                    break;

                case "set_renderer_materials":
                    if (!payload.ContainsKey("instanceId") || !payload.ContainsKey("materialPaths"))
                        return "set_renderer_materials requires instanceId and materialPaths";
                    break;

                case "set_volume_profile_overrides":
                    if (!(payload.ContainsKey("profilePath")
                          || payload.ContainsKey("volumeInstanceId")))
                    {
                        return "set_volume_profile_overrides requires profilePath or volumeInstanceId";
                    }
                    break;

                case "set_camera_rendering":
                    if (!(payload.ContainsKey("instanceId")
                          || payload.ContainsKey("cameraName")))
                    {
                        return "set_camera_rendering requires instanceId or cameraName";
                    }
                    break;
            }

            return null;
        }

        private static string NormalizeBatchOperation(string rawOperation)
        {
            if (string.IsNullOrWhiteSpace(rawOperation)) return string.Empty;

            var op = rawOperation.Trim().ToLowerInvariant();
            op = op.Replace("-", "_");

            switch (op)
            {
                case "spawn_prefab":
                case "create_gameobject":
                    return "spawn";
                case "modifygameobject":
                    return "modify_gameobject";
                case "addcomponent":
                    return "add_component";
                case "removecomponent":
                    return "remove_component";
                case "modifycomponent":
                    return "modify_component";
                case "patch_component":
                    return "patch_serialized_properties";
                case "set_materials":
                    return "set_renderer_materials";
                case "setvolumeprofileoverrides":
                    return "set_volume_profile_overrides";
                case "setcamerarendering":
                    return "set_camera_rendering";
                default:
                    return op;
            }
        }

        private static Dictionary<string, object> ExtractOperationPayload(Dictionary<string, object> operation)
        {
            if (operation.TryGetValue("payload", out var payloadObj) && payloadObj is Dictionary<string, object> payloadDict)
                return payloadDict;

            if (operation.TryGetValue("data", out var dataObj) && dataObj is Dictionary<string, object> dataDict)
                return dataDict;

            if (operation.TryGetValue("request", out var requestObj) && requestObj is Dictionary<string, object> requestDict)
                return requestDict;

            var fallback = new Dictionary<string, object>();
            foreach (var kv in operation)
            {
                var key = kv.Key;
                if (string.Equals(key, "op", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "operation", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                fallback[key] = kv.Value;
            }
            return fallback;
        }

        private static Dictionary<string, object> ParseOperationResult(string resultJson, string operationName)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
            {
                return ErrorResult($"Operation '{operationName}' returned an empty response");
            }

            var parsed = MiniJSON.Json.Deserialize(resultJson) as Dictionary<string, object>;
            if (parsed == null)
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", $"Operation '{operationName}' returned invalid JSON" },
                    { "raw", resultJson }
                };
            }

            if (!parsed.ContainsKey("success"))
            {
                parsed["success"] = !parsed.ContainsKey("error");
            }

            return parsed;
        }

        private static Dictionary<string, object> ErrorResult(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", message }
            };
        }

        private static void CollectChangedTargets(Dictionary<string, object> result, HashSet<string> changedTargets)
        {
            if (result == null || changedTargets == null) return;

            if (TryReadInt(result, "instanceId", out int instanceId))
            {
                changedTargets.Add($"instance:{instanceId}");
            }

            if (result.TryGetValue("instanceIds", out var idsObj) && idsObj is IList<object> ids)
            {
                foreach (var item in ids)
                {
                    changedTargets.Add($"instance:{Convert.ToInt32(item)}");
                }
            }

            if (result.TryGetValue("path", out var pathObj) && !string.IsNullOrWhiteSpace(pathObj?.ToString()))
            {
                changedTargets.Add(pathObj.ToString());
            }
        }

        private static string ExtractCheckpointId(string checkpointJson)
        {
            var parsed = MiniJSON.Json.Deserialize(checkpointJson) as Dictionary<string, object>;
            if (parsed == null) return null;

            if (parsed.TryGetValue("checkpoint", out var cpObj) && cpObj is Dictionary<string, object> cpDict)
            {
                if (cpDict.TryGetValue("id", out var idObj))
                {
                    return idObj?.ToString();
                }
            }

            if (parsed.TryGetValue("checkpointId", out var checkpointIdObj))
            {
                return checkpointIdObj?.ToString();
            }

            return null;
        }

        private static double GetBatchOperationBaseRisk(string operation)
        {
            switch (operation)
            {
                case "execute_csharp":
                    return 0.95d;
                case "set_volume_profile_overrides":
                    return 0.85d;
                case "set_render_settings":
                    return 0.82d;
                case "delete_gameobject":
                    return 0.78d;
                case "set_camera_rendering":
                    return 0.72d;
                case "modify_light":
                    return 0.62d;
                case "remove_component":
                    return 0.58d;
                case "patch_serialized_properties":
                case "modify_component":
                    return 0.55d;
                case "configure_collider":
                case "configure_rigidbody":
                case "set_renderer_materials":
                    return 0.5d;
                case "add_component":
                case "reparent":
                    return 0.45d;
                case "spawn":
                case "modify_gameobject":
                case "create_light":
                    return 0.4d;
                default:
                    return 0.52d;
            }
        }

        private static void EstimateBatchOperationImpact(
            string operation,
            Dictionary<string, object> payload,
            out bool globalOperation,
            out int objectTouches,
            out int componentTouches,
            out int assetWrites)
        {
            payload = payload ?? new Dictionary<string, object>();
            globalOperation = false;
            objectTouches = 0;
            componentTouches = 0;
            assetWrites = 0;

            int targetCount = 0;
            if (payload.ContainsKey("instanceId")) targetCount += 1;
            targetCount += CountListLike(payload, "instanceIds", "targets", "targetInstanceIds");
            if (targetCount == 0 && operation == "spawn") targetCount = 1;

            switch (operation)
            {
                case "spawn":
                    objectTouches = Math.Max(1, targetCount + CountListLike(payload, "objects", "prefabPaths"));
                    break;
                case "delete_gameobject":
                case "modify_gameobject":
                case "reparent":
                case "create_light":
                case "modify_light":
                case "configure_rigidbody":
                case "configure_collider":
                    objectTouches = Math.Max(1, targetCount);
                    break;
                case "add_component":
                case "remove_component":
                case "modify_component":
                    objectTouches = Math.Max(1, targetCount);
                    componentTouches = Math.Max(1, objectTouches);
                    break;
                case "patch_serialized_properties":
                    objectTouches = Math.Max(1, targetCount);
                    componentTouches = Math.Max(1, CountListLike(payload, "patches", "properties", "propertyPaths"));
                    break;
                case "set_renderer_materials":
                    objectTouches = Math.Max(1, targetCount);
                    componentTouches = Math.Max(1, CountListLike(payload, "materialPaths", "materials", "slots"));
                    break;
                case "set_render_settings":
                case "set_volume_profile_overrides":
                case "set_camera_rendering":
                    globalOperation = true;
                    objectTouches = Math.Max(1, targetCount);
                    componentTouches = Math.Max(1, CountListLike(payload, "overrides", "patch", "settings"));
                    assetWrites = 1;
                    break;
                case "execute_csharp":
                    globalOperation = true;
                    objectTouches = Math.Max(5, targetCount);
                    componentTouches = Math.Max(3, CountListLike(payload, "targets"));
                    assetWrites = 1;
                    break;
                default:
                    objectTouches = Math.Max(1, targetCount);
                    break;
            }
        }

        private static int CountListLike(Dictionary<string, object> payload, params string[] keys)
        {
            if (payload == null || keys == null || keys.Length == 0) return 0;

            int count = 0;
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key) || !payload.TryGetValue(key, out var value) || value == null)
                {
                    continue;
                }

                if (value is IList<object> objectList)
                {
                    count += objectList.Count;
                }
                else if (value is System.Collections.IList list)
                {
                    count += list.Count;
                }
                else
                {
                    count += 1;
                }
            }

            return count;
        }

        private static string BuildBatchApprovalHash(
            IList<BatchOperationRequest> operations,
            bool atomic,
            bool rollbackOnFail,
            bool createCheckpoint)
        {
            var normalizedOperations = new List<object>();
            if (operations != null)
            {
                foreach (var operation in operations)
                {
                    if (operation == null)
                    {
                        normalizedOperations.Add(new Dictionary<string, object> { { "operation", "(null)" } });
                        continue;
                    }

                    normalizedOperations.Add(new Dictionary<string, object>
                    {
                        { "index", operation.index },
                        { "isObject", operation.isObject },
                        { "operation", operation.operation ?? string.Empty },
                        { "payload", operation.payload ?? new Dictionary<string, object>() }
                    });
                }
            }

            var hashSource = new Dictionary<string, object>
            {
                { "version", 1 },
                { "atomic", atomic },
                { "rollbackOnFail", rollbackOnFail },
                { "createCheckpoint", createCheckpoint },
                { "operations", normalizedOperations }
            };

            var canonicalJson = BuildCanonicalJson(hashSource);
            using (var sha256 = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(canonicalJson);
                var hash = sha256.ComputeHash(bytes);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static string BuildCanonicalJson(object value)
        {
            var builder = new StringBuilder();
            AppendCanonicalJson(builder, value);
            return builder.ToString();
        }

        private static void AppendCanonicalJson(StringBuilder builder, object value)
        {
            if (builder == null) return;

            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (value is Dictionary<string, object> dict)
            {
                builder.Append("{");
                bool first = true;
                foreach (var key in dict.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first) builder.Append(",");
                    first = false;
                    builder.Append(MiniJSON.Json.Serialize(key));
                    builder.Append(":");
                    AppendCanonicalJson(builder, dict[key]);
                }
                builder.Append("}");
                return;
            }

            if (value is IList<object> objectList)
            {
                builder.Append("[");
                for (int i = 0; i < objectList.Count; i++)
                {
                    if (i > 0) builder.Append(",");
                    AppendCanonicalJson(builder, objectList[i]);
                }
                builder.Append("]");
                return;
            }

            if (value is System.Collections.IList list)
            {
                builder.Append("[");
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) builder.Append(",");
                    AppendCanonicalJson(builder, list[i]);
                }
                builder.Append("]");
                return;
            }

            if (value is bool boolValue)
            {
                builder.Append(boolValue ? "true" : "false");
                return;
            }

            if (value is string stringValue)
            {
                builder.Append(MiniJSON.Json.Serialize(stringValue));
                return;
            }

            if (value is float floatValue)
            {
                builder.Append(floatValue.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is double doubleValue)
            {
                builder.Append(doubleValue.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is decimal decimalValue)
            {
                builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is IFormattable formattable)
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            builder.Append(MiniJSON.Json.Serialize(value));
        }

        private static double Clamp01(double value)
        {
            if (value < 0d) return 0d;
            if (value > 1d) return 1d;
            return value;
        }

    }
}
