using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System;
using System.Collections.Generic;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Animator Controller editor (v4.2 Phase C, FR-119 to FR-126) ──────

    // FR-119: create_animator_controller
    static UCAFResult CmdCreateAnimatorController(UCAFCommand cmd)
    {
        string path = cmd.GetParam("path", "");
        if (string.IsNullOrEmpty(path))
            return new UCAFResult { success = false, message = "path is required (e.g. Assets/Animators/Player.controller)." };

        if (!path.EndsWith(".controller")) path += ".controller";

        bool overwrite = cmd.GetParam("if_exists", "skip") == "overwrite";
        if (File.Exists(Path.Combine(Path.GetDirectoryName(Application.dataPath), path)))
        {
            if (!overwrite)
                return new UCAFResult { success = false, message = $"Controller already exists: {path}. Use if_exists=overwrite to replace." };
            AssetDatabase.DeleteAsset(path);
        }

        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), Path.GetDirectoryName(path)));
        var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
        AssetDatabase.SaveAssets();

        return new UCAFResult { success = true, message = $"Animator Controller created: {path}" };
    }

    // FR-120: add_animator_state
    static UCAFResult CmdAddAnimatorState(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        string stateName  = cmd.GetParam("state_name", "");
        if (string.IsNullOrEmpty(stateName))
            return new UCAFResult { success = false, message = "state_name is required." };

        int layerIndex = int.TryParse(cmd.GetParam("layer", "0"), out int li) ? li : 0;
        if (layerIndex >= controller.layers.Length)
            return new UCAFResult { success = false, message = $"Layer {layerIndex} does not exist. Controller has {controller.layers.Length} layer(s)." };

        var sm = controller.layers[layerIndex].stateMachine;

        if (FindStateInMachine(sm, stateName) != null)
            return new UCAFResult { success = false, message = $"State '{stateName}' already exists in layer {layerIndex}." };

        var state = sm.AddState(stateName);

        string motionPath = cmd.GetParam("motion_path", "");
        if (!string.IsNullOrEmpty(motionPath))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(motionPath);
            if (clip == null)
                return new UCAFResult { success = false, message = $"AnimationClip not found: {motionPath}" };
            state.motion = clip;
        }

        if (cmd.GetParam("as_default", "false") == "true")
            sm.defaultState = state;

        SaveController(controller);
        return new UCAFResult {
            success = true,
            message = $"State '{stateName}' added to layer {layerIndex}" +
                      (sm.defaultState == state ? " (set as default)." : ".")
        };
    }

    // FR-121: add_animator_transition
    static UCAFResult CmdAddAnimatorTransition(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        string fromName = cmd.GetParam("from_state", "");
        string toName   = cmd.GetParam("to_state", "");
        if (string.IsNullOrEmpty(fromName) || string.IsNullOrEmpty(toName))
            return new UCAFResult { success = false, message = "from_state and to_state are required." };

        int layerIndex = int.TryParse(cmd.GetParam("layer", "0"), out int li) ? li : 0;
        if (layerIndex >= controller.layers.Length)
            return new UCAFResult { success = false, message = $"Layer {layerIndex} does not exist." };

        var sm = controller.layers[layerIndex].stateMachine;

        AnimatorState toState = FindStateInMachine(sm, toName);
        if (toState == null)
            return new UCAFResult { success = false, message = $"Destination state '{toName}' not found." };

        bool   hasExitTime = cmd.GetParam("has_exit_time", "false") == "true";
        float  exitTime    = float.TryParse(cmd.GetParam("exit_time", "1.0"), out float et) ? et : 1.0f;
        float  duration    = float.TryParse(cmd.GetParam("duration", "0.25"), out float d)  ? d  : 0.25f;

        AnimatorStateTransition transition;
        bool isAnyState = fromName.Equals("AnyState", StringComparison.OrdinalIgnoreCase) ||
                          fromName.Equals("Any State", StringComparison.OrdinalIgnoreCase);

        if (isAnyState)
        {
            transition = sm.AddAnyStateTransition(toState);
        }
        else
        {
            AnimatorState fromState = FindStateInMachine(sm, fromName);
            if (fromState == null)
                return new UCAFResult { success = false, message = $"Source state '{fromName}' not found." };
            transition = fromState.AddTransition(toState);
        }

        transition.hasExitTime = hasExitTime;
        transition.exitTime    = exitTime;
        transition.duration    = duration;

        string condJson = cmd.GetParam("conditions", "");
        if (!string.IsNullOrEmpty(condJson) && condJson != "[]")
        {
            try
            {
                var wrapper = JsonUtility.FromJson<UCAFAnimatorConditionList>("{\"conditions\":" + condJson + "}");
                foreach (var cond in wrapper.conditions)
                {
                    var mode = ParseConditionMode(cond.op);
                    transition.AddCondition(mode, cond.threshold, cond.param);
                }
            }
            catch (Exception ex)
            {
                return new UCAFResult { success = false, message = $"Failed to parse conditions JSON: {ex.Message}. Expected: [{{\"param\":\"Speed\",\"op\":\"Greater\",\"threshold\":0.5}}]" };
            }
        }

        SaveController(controller);
        return new UCAFResult {
            success = true,
            message = $"Transition '{fromName}' → '{toName}' added."
        };
    }

    // FR-122: add_animator_parameter
    static UCAFResult CmdAddAnimatorParameter(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        string paramName = cmd.GetParam("param_name", "");
        if (string.IsNullOrEmpty(paramName))
            return new UCAFResult { success = false, message = "param_name is required." };

        string typeStr = cmd.GetParam("type", "Float");
        if (!Enum.TryParse<AnimatorControllerParameterType>(typeStr, true, out var paramType))
            return new UCAFResult { success = false, message = $"Unknown type '{typeStr}'. Valid: Float, Int, Bool, Trigger." };

        foreach (var p in controller.parameters)
            if (p.name == paramName)
                return new UCAFResult { success = false, message = $"Parameter '{paramName}' already exists." };

        controller.AddParameter(paramName, paramType);

        string defaultVal = cmd.GetParam("default_value", "");
        if (!string.IsNullOrEmpty(defaultVal))
        {
            var parameters = controller.parameters;
            foreach (var p in parameters)
            {
                if (p.name != paramName) continue;
                if (paramType == AnimatorControllerParameterType.Float && float.TryParse(defaultVal, out float fv))
                    p.defaultFloat = fv;
                else if (paramType == AnimatorControllerParameterType.Int && int.TryParse(defaultVal, out int iv))
                    p.defaultInt = iv;
                else if (paramType == AnimatorControllerParameterType.Bool)
                    p.defaultBool = defaultVal == "true" || defaultVal == "1";
                break;
            }
            controller.parameters = parameters;
        }

        SaveController(controller);
        return new UCAFResult { success = true, message = $"Parameter '{paramName}' ({typeStr}) added." };
    }

    // FR-123: add_blend_tree
    static UCAFResult CmdAddBlendTree(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        string stateName = cmd.GetParam("state_name", "BlendTree");
        int layerIndex   = int.TryParse(cmd.GetParam("layer", "0"), out int li) ? li : 0;
        if (layerIndex >= controller.layers.Length)
            return new UCAFResult { success = false, message = $"Layer {layerIndex} does not exist." };

        string blendTypeStr = cmd.GetParam("blend_type", "Simple1D");
        if (!Enum.TryParse<BlendTreeType>(blendTypeStr, true, out var blendType))
            return new UCAFResult { success = false, message = $"Unknown blend_type '{blendTypeStr}'. Valid: Simple1D, SimpleDirectional2D, FreeformDirectional2D, FreeformCartesian2D, Direct." };

        AnimatorState btState = controller.CreateBlendTreeInController(stateName, out BlendTree blendTree, layerIndex);
        blendTree.blendType = blendType;

        string paramX = cmd.GetParam("param_x", "");
        string paramY = cmd.GetParam("param_y", "");
        if (!string.IsNullOrEmpty(paramX)) blendTree.blendParameter  = paramX;
        if (!string.IsNullOrEmpty(paramY)) blendTree.blendParameterY = paramY;

        string motionsJson = cmd.GetParam("motions", "");
        if (!string.IsNullOrEmpty(motionsJson) && motionsJson != "[]")
        {
            try
            {
                var wrapper = JsonUtility.FromJson<UCAFBlendTreeMotionList>("{\"motions\":" + motionsJson + "}");
                foreach (var m in wrapper.motions)
                {
                    var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(m.path);
                    if (clip == null)
                        return new UCAFResult { success = false, message = $"AnimationClip not found: {m.path}" };
                    if (blendType == BlendTreeType.Simple1D)
                        blendTree.AddChild(clip, m.threshold);
                    else
                        blendTree.AddChild(clip, new Vector2(m.position_x, m.position_y));
                }
            }
            catch (Exception ex)
            {
                return new UCAFResult { success = false, message = $"Failed to parse motions JSON: {ex.Message}" };
            }
        }

        SaveController(controller);
        return new UCAFResult { success = true, message = $"Blend tree state '{stateName}' added to layer {layerIndex}." };
    }

    // FR-124: set_animator_layer
    static UCAFResult CmdSetAnimatorLayer(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        int layerIndex = int.TryParse(cmd.GetParam("layer_index", "0"), out int li) ? li : 0;
        if (layerIndex >= controller.layers.Length)
            return new UCAFResult { success = false, message = $"Layer {layerIndex} does not exist." };

        var layers = controller.layers;
        var layer  = layers[layerIndex];

        if (float.TryParse(cmd.GetParam("weight", ""), out float weight))
            layer.defaultWeight = weight;

        string blendingStr = cmd.GetParam("blending", "");
        if (!string.IsNullOrEmpty(blendingStr))
        {
            if (Enum.TryParse<AnimatorLayerBlendingMode>(blendingStr, true, out var blending))
                layer.blendingMode = blending;
            else
                return new UCAFResult { success = false, message = $"Unknown blending '{blendingStr}'. Valid: Override, Additive." };
        }

        string maskPath = cmd.GetParam("mask_path", "");
        if (!string.IsNullOrEmpty(maskPath))
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
            if (mask == null)
                return new UCAFResult { success = false, message = $"AvatarMask not found: {maskPath}" };
            layer.avatarMask = mask;
        }

        layers[layerIndex] = layer;
        controller.layers  = layers;
        SaveController(controller);
        return new UCAFResult { success = true, message = $"Layer {layerIndex} updated." };
    }

    // FR-125: list_animator_states
    static UCAFResult CmdListAnimatorStates(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        var info = new UCAFAnimatorInfo {
            controller_path = cmd.GetParam("controller_path", ""),
            layer_count     = controller.layers.Length
        };

        for (int li = 0; li < controller.layers.Length; li++)
        {
            var sm = controller.layers[li].stateMachine;
            CollectStates(sm, li, sm.defaultState, info.states);
            CollectTransitions(sm, info.transitions);
        }

        foreach (var p in controller.parameters)
        {
            string defVal = p.type == AnimatorControllerParameterType.Float  ? p.defaultFloat.ToString("F3")
                          : p.type == AnimatorControllerParameterType.Int    ? p.defaultInt.ToString()
                          : p.type == AnimatorControllerParameterType.Bool   ? p.defaultBool.ToString()
                          : "";
            info.parameters.Add(new UCAFAnimatorParamInfo {
                name          = p.name,
                type          = p.type.ToString(),
                default_value = defVal
            });
        }

        return new UCAFResult {
            success   = true,
            message   = $"{info.states.Count} state(s), {info.transitions.Count} transition(s), {info.parameters.Count} parameter(s).",
            data_json = JsonUtility.ToJson(info)
        };
    }

    // FR-126: validate_animator
    static UCAFResult CmdValidateAnimator(UCAFCommand cmd)
    {
        var (controller, err) = LoadAnimatorController(cmd);
        if (controller == null) return new UCAFResult { success = false, message = err };

        var issues = new List<UCAFAnimatorValidationIssue>();

        // States without motion
        for (int li = 0; li < controller.layers.Length; li++)
        {
            var sm = controller.layers[li].stateMachine;
            foreach (var cs in sm.states)
            {
                if (cs.state.motion == null)
                    issues.Add(new UCAFAnimatorValidationIssue {
                        state_or_param = cs.state.name,
                        issue = $"State has no motion assigned (layer {li})."
                    });
            }
        }

        // Parameters unused in any transition condition
        var usedParams = new HashSet<string>();
        for (int li = 0; li < controller.layers.Length; li++)
        {
            var sm = controller.layers[li].stateMachine;
            foreach (var cs in sm.states)
                foreach (var t in cs.state.transitions)
                    foreach (var c in t.conditions)
                        usedParams.Add(c.parameter);
            foreach (var t in sm.anyStateTransitions)
                foreach (var c in t.conditions)
                    usedParams.Add(c.parameter);
        }

        foreach (var p in controller.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Trigger) continue; // triggers are often set imperatively
            if (!usedParams.Contains(p.name))
                issues.Add(new UCAFAnimatorValidationIssue {
                    state_or_param = p.name,
                    issue = $"Parameter '{p.name}' ({p.type}) is never used in any transition condition."
                });
        }

        var result = new UCAFAnimatorValidationResult {
            valid        = issues.Count == 0,
            issues_count = issues.Count,
            issues       = issues
        };

        return new UCAFResult {
            success   = true,
            message   = issues.Count == 0 ? "Animator is valid." : $"{issues.Count} issue(s) found.",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    static (AnimatorController, string) LoadAnimatorController(UCAFCommand cmd)
    {
        string path = cmd.GetParam("controller_path", "");
        if (string.IsNullOrEmpty(path)) return (null, "controller_path is required.");
        if (!path.EndsWith(".controller")) path += ".controller";
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
        if (controller == null) return (null, $"AnimatorController not found: {path}");
        return (controller, null);
    }

    static void SaveController(AnimatorController controller)
    {
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
    }

    static AnimatorState FindStateInMachine(AnimatorStateMachine sm, string name)
    {
        foreach (var cs in sm.states)
            if (cs.state.name == name) return cs.state;
        foreach (var csm in sm.stateMachines)
        {
            var found = FindStateInMachine(csm.stateMachine, name);
            if (found != null) return found;
        }
        return null;
    }

    static void CollectStates(AnimatorStateMachine sm, int layer, AnimatorState defaultState, List<UCAFAnimatorStateInfo> list)
    {
        foreach (var cs in sm.states)
        {
            string motionPath = cs.state.motion != null ? AssetDatabase.GetAssetPath(cs.state.motion) : "";
            list.Add(new UCAFAnimatorStateInfo {
                name        = cs.state.name,
                motion_path = motionPath,
                is_default  = defaultState != null && cs.state == defaultState,
                layer       = layer,
                state_type  = cs.state.motion is BlendTree ? "blend_tree" : "normal"
            });
        }
        foreach (var csm in sm.stateMachines)
            CollectStates(csm.stateMachine, layer, defaultState, list);
    }

    static void CollectTransitions(AnimatorStateMachine sm, List<UCAFAnimatorTransitionInfo> list)
    {
        foreach (var cs in sm.states)
            foreach (var t in cs.state.transitions)
                list.Add(BuildTransitionInfo(cs.state.name, t));

        foreach (var t in sm.anyStateTransitions)
            list.Add(BuildTransitionInfo("AnyState", t));

        foreach (var csm in sm.stateMachines)
            CollectTransitions(csm.stateMachine, list);
    }

    static UCAFAnimatorTransitionInfo BuildTransitionInfo(string fromName, AnimatorStateTransition t)
    {
        var info = new UCAFAnimatorTransitionInfo {
            from_state    = fromName,
            to_state      = t.destinationState != null ? t.destinationState.name : "(exit)",
            has_exit_time = t.hasExitTime,
            exit_time     = t.exitTime,
            duration      = t.duration
        };
        foreach (var c in t.conditions)
            info.conditions.Add(new UCAFAnimatorConditionData {
                param     = c.parameter,
                op        = c.mode.ToString(),
                threshold = c.threshold
            });
        return info;
    }

    static AnimatorConditionMode ParseConditionMode(string op)
    {
        switch (op.ToLowerInvariant())
        {
            case "greater":  return AnimatorConditionMode.Greater;
            case "less":     return AnimatorConditionMode.Less;
            case "equals":   return AnimatorConditionMode.Equals;
            case "notequal":
            case "not_equal":
            case "notequals":return AnimatorConditionMode.NotEqual;
            case "if":
            case "istrue":   return AnimatorConditionMode.If;
            case "ifnot":
            case "isfalse":  return AnimatorConditionMode.IfNot;
            default:
                if (Enum.TryParse<AnimatorConditionMode>(op, true, out var mode)) return mode;
                throw new ArgumentException($"Unknown condition op '{op}'. Valid: Greater, Less, Equals, NotEqual, If, IfNot.");
        }
    }
}
