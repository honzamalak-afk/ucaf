using System;
using System.Collections.Generic;

[Serializable]
public class UCAFCommand
{
    public string id;
    public string type;
    public string timestamp;
    public List<UCAFParam> params_list = new List<UCAFParam>();

    public string GetParam(string key, string defaultValue = "")
    {
        if (params_list == null) return defaultValue;
        foreach (var p in params_list)
            if (p.key == key) return p.value;
        return defaultValue;
    }

    public bool HasParam(string key)
    {
        if (params_list == null) return false;
        foreach (var p in params_list)
            if (p.key == key) return true;
        return false;
    }
}

[Serializable]
public class UCAFParam
{
    public string key;
    public string value;
}

[Serializable]
public class UCAFResult
{
    public bool   success;
    public string message;
    public string screenshot_path;
    public string data_json;
}

[Serializable]
public class UCAFSceneNode
{
    public string path;
    public string name;
    public bool active;
    public string tag;
    public int layer;
    public List<string> components = new List<string>();
    public List<UCAFSceneNode> children = new List<UCAFSceneNode>();
}

[Serializable]
public class UCAFSceneTree
{
    public string scene_name;
    public List<UCAFSceneNode> roots = new List<UCAFSceneNode>();
}

[Serializable]
public class UCAFLogEntry
{
    public string timestamp;
    public string type;
    public string message;
    public string stack_trace;
}

[Serializable]
public class UCAFLogQueryResult
{
    public List<UCAFLogEntry> entries = new List<UCAFLogEntry>();
    public int total_in_buffer;
}

[Serializable]
public class UCAFFieldInfo
{
    public string name;
    public string display_name;
    public string type;
}

[Serializable]
public class UCAFFieldList
{
    public List<UCAFFieldInfo> fields = new List<UCAFFieldInfo>();
}

[Serializable]
public class UCAFFieldValue
{
    public string field_name;
    public string type;
    public string value;
}

[Serializable]
public class UCAFStringList
{
    public List<string> items = new List<string>();
}

[Serializable]
public class UCAFCompileResult
{
    public bool has_errors;
    public int error_count;
    public int warning_count;
    public List<UCAFLogEntry> errors = new List<UCAFLogEntry>();
    public List<UCAFLogEntry> warnings = new List<UCAFLogEntry>();
    public int elapsed_ms;
}

// ── v3.0 types ──────────────────────────────────────────────────────────

[Serializable]
public class UCAFObjectRef
{
    public string path;
    public string name;
}

[Serializable]
public class UCAFObjectRefList
{
    public List<UCAFObjectRef> items = new List<UCAFObjectRef>();
}

[Serializable]
public class UCAFObjectInfo
{
    public string path;
    public string name;
    public bool active;
    public string tag;
    public int layer;
    public string position;
    public string rotation;
    public string scale;
    public List<string> components = new List<string>();
    public bool is_prefab_instance;
    public string prefab_asset_path;
}

[Serializable]
public class UCAFFileContent
{
    public string asset_path;
    public string content;
    public int line_count;
}

[Serializable]
public class UCAFBatchSubResult
{
    public int index;
    public string type;
    public bool success;
    public string message;
    public string data_json;
    public string screenshot_path;
}

[Serializable]
public class UCAFBatchResult
{
    public int total;
    public int succeeded;
    public int failed;
    public bool stopped_early;
    public List<UCAFBatchSubResult> results = new List<UCAFBatchSubResult>();
}

[Serializable]
public class UCAFArrayAppendResult
{
    public int new_index;
    public int new_size;
}

[Serializable]
public class UCAFBatchPayload
{
    public bool stop_on_error;
    public List<UCAFCommand> commands = new List<UCAFCommand>();
}

// ── v4.2 Phase A types ──────────────────────────────────────────────────

[Serializable]
public class UCAFPackageInfo
{
    public string name;
    public string displayName;
    public string version;
    public string source;
    public string description;
    public string category;
}

[Serializable]
public class UCAFPackageList
{
    public int total;
    public List<UCAFPackageInfo> packages = new List<UCAFPackageInfo>();
}

[Serializable]
public class UCAFImportCheck
{
    public string key;
    public string value;
}

[Serializable]
public class UCAFImportRule
{
    public string glob;
    public List<UCAFImportCheck> checks = new List<UCAFImportCheck>();
}

[Serializable]
public class UCAFImportRules
{
    public List<UCAFImportRule> rules = new List<UCAFImportRule>();
}

[Serializable]
public class UCAFImportIssue
{
    public string asset_path;
    public string rule;
    public string expected;
    public string actual;
}

[Serializable]
public class UCAFImportValidationResult
{
    public int total_checked;
    public int issues_found;
    public List<UCAFImportIssue> issues = new List<UCAFImportIssue>();
}

[Serializable]
public class UCAFGitStatusResult
{
    public List<string> modified  = new List<string>();
    public List<string> added     = new List<string>();
    public List<string> deleted   = new List<string>();
    public List<string> untracked = new List<string>();
    public int total;
}

[Serializable]
public class UCAFGitDiffResult
{
    public string patch;
    public int files_changed;
}

// ── v4.2 Phase B types ──────────────────────────────────────────────────

[Serializable]
public class UCAFPlayModeResult
{
    public string playmode_session_id;
    public int frames;
    public float elapsed_seconds;
}

[Serializable]
public class UCAFPlayModeRunResult
{
    public string playmode_session_id;
    public int frames;
    public float elapsed_seconds;
    public bool condition_met;
    public string wait_for;
    public List<string> signals_emitted = new List<string>();
    public string console_stream_path;      // FR-182: auto-subscribed stream
    public string recording_session_id;     // FR-191: record_video=true
    public List<string> recording_frames = new List<string>(); // FR-191
}

[Serializable]
public class UCAFTestInfo
{
    public string test_name;
    public string full_name;
    public string mode;
    public string assembly;
}

[Serializable]
public class UCAFTestList
{
    public int total;
    public List<UCAFTestInfo> tests = new List<UCAFTestInfo>();
}

[Serializable]
public class UCAFTestResultEntry
{
    public string test_name;
    public string full_name;
    public string result_type;
    public float  duration_s;
    public string message;
    public string stack_trace;
}

[Serializable]
public class UCAFTestRunResult
{
    public int   total;
    public int   passed;
    public int   failed;
    public int   skipped;
    public float duration_s;
    public List<UCAFTestResultEntry> tests = new List<UCAFTestResultEntry>();
}

[Serializable]
public class UCAFRuntimeFieldValue
{
    public string obj_path;
    public string component;
    public string field;
    public string value;
    public string type_name;
}

[Serializable]
public class UCAFSignalRecord
{
    public string name;
    public string timestamp;
    public int    frame;
}

[Serializable]
public class UCAFSignalList
{
    public int total;
    public List<UCAFSignalRecord> signals = new List<UCAFSignalRecord>();
}

[Serializable]
public class UCAFRuntimeCallResult
{
    public bool   success;
    public string return_value;
    public string exception;
}

// ── v4.2 Phase C types ──────────────────────────────────────────────────

[Serializable]
public class UCAFAnimatorStateInfo
{
    public string name;
    public string motion_path;
    public bool   is_default;
    public int    layer;
    public string state_type; // "normal" | "blend_tree"
}

[Serializable]
public class UCAFAnimatorConditionData
{
    public string param;
    public string op;        // Greater | Less | Equals | NotEqual | If | IfNot
    public float  threshold;
}

[Serializable]
public class UCAFAnimatorConditionList
{
    public List<UCAFAnimatorConditionData> conditions = new List<UCAFAnimatorConditionData>();
}

[Serializable]
public class UCAFAnimatorTransitionInfo
{
    public string from_state;
    public string to_state;
    public bool   has_exit_time;
    public float  exit_time;
    public float  duration;
    public List<UCAFAnimatorConditionData> conditions = new List<UCAFAnimatorConditionData>();
}

[Serializable]
public class UCAFAnimatorParamInfo
{
    public string name;
    public string type;          // Float | Int | Bool | Trigger
    public string default_value;
}

[Serializable]
public class UCAFAnimatorInfo
{
    public string controller_path;
    public int    layer_count;
    public List<UCAFAnimatorStateInfo>      states      = new List<UCAFAnimatorStateInfo>();
    public List<UCAFAnimatorTransitionInfo> transitions = new List<UCAFAnimatorTransitionInfo>();
    public List<UCAFAnimatorParamInfo>      parameters  = new List<UCAFAnimatorParamInfo>();
}

[Serializable]
public class UCAFAnimatorValidationIssue
{
    public string state_or_param;
    public string issue;
}

[Serializable]
public class UCAFAnimatorValidationResult
{
    public bool  valid;
    public int   issues_count;
    public List<UCAFAnimatorValidationIssue> issues = new List<UCAFAnimatorValidationIssue>();
}

[Serializable]
public class UCAFBlendTreeMotion
{
    public string path;
    public float  threshold;
    public float  position_x;
    public float  position_y;
}

[Serializable]
public class UCAFBlendTreeMotionList
{
    public List<UCAFBlendTreeMotion> motions = new List<UCAFBlendTreeMotion>();
}

[Serializable]
public class UCAFBuildPlayerResult
{
    public bool   success;
    public string output_path;
    public float  duration_s;
    public long   size_bytes;
    public int    error_count;
    public int    warning_count;
    public string summary_result;
    public List<string> errors   = new List<string>();
    public List<string> warnings = new List<string>();
}

[Serializable]
public class UCAFNavMeshPath
{
    public bool   reachable;
    public string status;       // Complete | Partial | Invalid
    public float  length;
    public int    corner_count;
    public List<string> corners = new List<string>(); // "x,y,z"
}

[Serializable]
public class UCAFNavMeshSample
{
    public bool   hit;
    public string position; // "x,y,z"
    public float  distance;
}

[Serializable]
public class UCAFLightmapResult
{
    public bool  success;
    public float duration_s;
    public int   atlas_count;
    public List<string> errors = new List<string>();
}

// ── v4.2 Phase D types ──────────────────────────────────────────────────

[Serializable]
public class UCAFProfilerMetric
{
    public string name;
    public double value;
    public string unit;
}

[Serializable]
public class UCAFProfilerSnapshot
{
    public string snapshot_id;
    public string timestamp;
    public int    frames;
    public List<UCAFProfilerMetric> metrics = new List<UCAFProfilerMetric>();
}

[Serializable]
public class UCAFProfilerDiff
{
    public string name;
    public double value_a;
    public double value_b;
    public double delta;
    public double delta_pct;
}

[Serializable]
public class UCAFProfilerCompare
{
    public string snapshot_a;
    public string snapshot_b;
    public List<UCAFProfilerDiff> diffs = new List<UCAFProfilerDiff>();
}

[Serializable]
public class UCAFTimelineTrackInfo
{
    public string name;
    public string track_type;
    public int    clip_count;
}

[Serializable]
public class UCAFTimelineInfo
{
    public string asset_path;
    public float  duration;
    public int    track_count;
    public List<UCAFTimelineTrackInfo> tracks = new List<UCAFTimelineTrackInfo>();
}

[Serializable]
public class UCAFTimelineClipInfo
{
    public string name;
    public float  start;
    public float  duration;
    public string track_name;
}

[Serializable]
public class UCAFShaderGraphProperty
{
    public string name;
    public string reference;
    public string type;
    public string default_value;
}

[Serializable]
public class UCAFShaderGraphPropertyList
{
    public string asset_path;
    public int    count;
    public List<UCAFShaderGraphProperty> properties = new List<UCAFShaderGraphProperty>();
}

[Serializable]
public class UCAFVFXProperty
{
    public string name;
    public string type;
    public string value;
}

[Serializable]
public class UCAFVFXPropertyList
{
    public string obj_path;
    public List<UCAFVFXProperty> properties = new List<UCAFVFXProperty>();
}

[Serializable]
public class UCAFTerrainInfo
{
    public string obj_path;
    public string data_asset_path;
    public float  width;
    public float  height;
    public float  length;
    public int    heightmap_resolution;
    public int    layer_count;
    public int    tree_prototype_count;
    public int    tree_instance_count;
}

// ── v4.1 types ──────────────────────────────────────────────────────────

[Serializable]
public class UCAFErrorContext
{
    public string error_code;
    public string field;
    public string expected_type;
    public List<string> valid_values_sample = new List<string>();
    public List<string> valid_format_examples = new List<string>();
    public string hint;
}

[Serializable]
public class UCAFEditFileResult
{
    public string asset_path;
    public string mode;
    public int replacements;
    public int line_count_before;
    public int line_count_after;
    public bool compiled;
    public bool compile_has_errors;
    public int compile_error_count;
    public int compile_warning_count;
}

[Serializable]
public class UCAFCodeHit
{
    public string path;
    public int line;
    public string match;
    public string context_before;
    public string context_after;
}

[Serializable]
public class UCAFCodeHits
{
    public int total;
    public bool truncated;
    public List<UCAFCodeHit> hits = new List<UCAFCodeHit>();
}

[Serializable]
public class UCAFBugScope
{
    public List<string> files = new List<string>();
    public List<string> scenes = new List<string>();
    public List<string> components = new List<string>();
}

[Serializable]
public class UCAFBugVerification
{
    public List<string> tests = new List<string>();
    public string manual;
}

[Serializable]
public class UCAFBugRecord
{
    public string bug_id;
    public string created_at;
    public string updated_at;
    public string status;        // open | fixed | recurring | wontfix | duplicate
    public string title;
    public string symptom;
    public UCAFBugScope scope = new UCAFBugScope();
    public string root_cause;
    public string fix;
    public List<string> fix_commits = new List<string>();
    public string fix_undo_group;
    public List<string> tags = new List<string>();
    public int occurrences = 1;
    public string introduced_by;
    public UCAFBugVerification verification = new UCAFBugVerification();
    public string lessons;
    public string duplicate_of;
}

[Serializable]
public class UCAFBugList
{
    public int total;
    public List<UCAFBugRecord> bugs = new List<UCAFBugRecord>();
}

[Serializable]
public class UCAFBugMatch
{
    public UCAFBugRecord bug;
    public float similarity_score;
}

[Serializable]
public class UCAFBugMatchList
{
    public int total;
    public List<UCAFBugMatch> matches = new List<UCAFBugMatch>();
}

[Serializable]
public class UCAFBugIndex
{
    public int next_id = 1;
    public List<UCAFBugRecord> bugs = new List<UCAFBugRecord>();
}

[Serializable]
public class UCAFCapabilitiesPayload
{
    public string protocol_version;
    public string listener_label;
}

// ── v4.2 Phase E types ──────────────────────────────────────────────────

[Serializable]
public class UCAFConsoleSubscription
{
    public string subscription_id;
    public string stream_path;
    public string level;
    public string pattern;
    public string from_assembly;
    public int    entries_written;
}

[Serializable]
public class UCAFParamDefinition
{
    public string key;
    public string type;
    public string description;
    public string default_value;
    public List<string> enum_values = new List<string>();
}

[Serializable]
public class UCAFCommandSchema
{
    public string command_type;
    public string description;
    public List<string> required_params  = new List<string>();
    public List<UCAFParamDefinition> optional_params = new List<UCAFParamDefinition>();
}

[Serializable]
public class UCAFSchemaValidationResult
{
    public bool   valid;
    public string command_type;
    public int    issues_count;
    public List<string> issues = new List<string>();
    public UCAFErrorContext error_context = new UCAFErrorContext();
}

// ── v4.2 Phase F types ──────────────────────────────────────────────────

[Serializable]
public class UCAFRecordingInfo
{
    public string session_id;
    public string started_at;
    public string format;
    public int    fps;
    public string output_path;
    public int    frame_count;
    public float  duration_s;
    public long   size_bytes;
}

[Serializable]
public class UCAFRecordingList
{
    public int total;
    public List<UCAFRecordingInfo> recordings = new List<UCAFRecordingInfo>();
}

[Serializable]
public class UCAFFrameExtractResult
{
    public string session_id;
    public int    frames_extracted;
    public List<string> frame_paths = new List<string>();
}
