using UnityEngine;
using UnityEditor;
using UnityEngine.Timeline;
using UnityEngine.Playables;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── Timeline + Cinemachine stubs (v4.2 Phase D, FR-154–159) ─────────

    // FR-154: create_timeline
    static UCAFResult CmdCreateTimeline(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required (e.g. Assets/Timelines/Intro.playable)." };

        string dir = Path.GetDirectoryName(assetPath);
        if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
        {
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Application.dataPath), dir));
            AssetDatabase.Refresh();
        }

        var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
        AssetDatabase.CreateAsset(timeline, assetPath);
        AssetDatabase.SaveAssets();

        string msg = $"Timeline created: {assetPath}";

        string dirObjPath = cmd.GetParam("director_obj", "");
        if (!string.IsNullOrEmpty(dirObjPath))
        {
            var go = GameObject.Find(dirObjPath);
            if (go == null)
                return new UCAFResult { success = false, message = $"director_obj not found: {dirObjPath}" };

            var pd = go.GetComponent<PlayableDirector>() ?? Undo.AddComponent<PlayableDirector>(go);
            pd.playableAsset   = timeline;
            pd.timeUpdateMode  = DirectorUpdateMode.GameTime;
            EditorUtility.SetDirty(go);
            msg += $"; PlayableDirector attached to '{go.name}'";
        }

        var info = new UCAFTimelineInfo {
            asset_path  = assetPath,
            duration    = (float)timeline.duration,
            track_count = timeline.outputTrackCount
        };

        return new UCAFResult { success = true, message = msg, data_json = JsonUtility.ToJson(info) };
    }

    // FR-155: add_timeline_track
    static UCAFResult CmdAddTimelineTrack(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required." };

        var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
        if (timeline == null)
            return new UCAFResult { success = false, message = $"TimelineAsset not found: {assetPath}" };

        string trackType = cmd.GetParam("track_type", "animation").ToLowerInvariant();
        string trackName = cmd.GetParam("track_name", trackType + "_track");

        TrackAsset track;
        switch (trackType)
        {
            case "animation":  track = timeline.CreateTrack<AnimationTrack>(trackName);  break;
            case "audio":      track = timeline.CreateTrack<AudioTrack>(trackName);      break;
            case "activation": track = timeline.CreateTrack<ActivationTrack>(trackName); break;
            case "signal":     track = timeline.CreateTrack<SignalTrack>(trackName);     break;
            default:
                return new UCAFResult { success = false, message = $"Unknown track_type '{trackType}'. Use: animation, audio, activation, signal." };
        }

        // Optionally bind to a scene object via a PlayableDirector on the same object
        string bindingObj = cmd.GetParam("binding_obj", "");
        if (!string.IsNullOrEmpty(bindingObj))
        {
            var dirGo = FindDirectorForTimeline(timeline);
            if (dirGo != null)
            {
                var bindTarget = GameObject.Find(bindingObj);
                if (bindTarget != null)
                {
                    var pd = dirGo.GetComponent<PlayableDirector>();
                    if (pd != null)
                    {
                        UnityEngine.Object binding = null;
                        if      (trackType == "animation") binding = (UnityEngine.Object)bindTarget.GetComponent<Animator>() ?? bindTarget.GetComponent<Animation>();
                        else if (trackType == "audio")     binding = bindTarget.GetComponent<AudioSource>();
                        else                               binding = bindTarget;
                        if (binding != null) pd.SetGenericBinding(track, binding);
                        EditorUtility.SetDirty(dirGo);
                    }
                }
            }
        }

        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();

        return new UCAFResult { success = true, message = $"Track '{track.name}' ({trackType}) added to '{assetPath}'." };
    }

    // FR-156: add_timeline_clip
    static UCAFResult CmdAddTimelineClip(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        string trackName = cmd.GetParam("track_name", "");
        if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(trackName))
            return new UCAFResult { success = false, message = "asset_path and track_name required." };

        var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(assetPath);
        if (timeline == null)
            return new UCAFResult { success = false, message = $"TimelineAsset not found: {assetPath}" };

        TrackAsset target = null;
        foreach (var t in timeline.GetOutputTracks())
            if (t.name == trackName) { target = t; break; }
        if (target == null)
            return new UCAFResult { success = false, message = $"Track '{trackName}' not found." };

        float start    = float.TryParse(cmd.GetParam("start",    "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ? s : 0f;
        float duration = float.TryParse(cmd.GetParam("duration", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 1f;
        string clipName = cmd.GetParam("clip_name", "Clip");
        string clipAsset = cmd.GetParam("clip_asset", "");

        TimelineClip clip;

        if (!string.IsNullOrEmpty(clipAsset) && target is AnimationTrack animTrack)
        {
            var animClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipAsset);
            if (animClip == null) return new UCAFResult { success = false, message = $"AnimationClip not found: {clipAsset}" };
            clip = animTrack.CreateClip(animClip);
        }
        else if (!string.IsNullOrEmpty(clipAsset) && target is AudioTrack audioTrack)
        {
            var audioClip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipAsset);
            if (audioClip == null) return new UCAFResult { success = false, message = $"AudioClip not found: {clipAsset}" };
            clip = audioTrack.CreateClip(audioClip);
        }
        else
        {
            clip = target.CreateDefaultClip();
        }

        clip.displayName = clipName;
        clip.start       = start;
        clip.duration    = duration;

        EditorUtility.SetDirty(timeline);
        AssetDatabase.SaveAssets();

        return new UCAFResult {
            success   = true,
            message   = $"Clip '{clipName}' added to track '{trackName}' at t={start:F2}s.",
            data_json = JsonUtility.ToJson(new UCAFTimelineClipInfo {
                name = clipName, start = start, duration = duration, track_name = trackName
            })
        };
    }

    // FR-157–159: Cinemachine stubs — package not installed
    static UCAFResult CmdCreateCinemachineCamera(UCAFCommand cmd) =>
        new UCAFResult { success = false, message = "Cinemachine not installed. Run: add_package name=com.unity.cinemachine" };
    static UCAFResult CmdSetVCamProperty(UCAFCommand cmd) =>
        new UCAFResult { success = false, message = "Cinemachine not installed. Run: add_package name=com.unity.cinemachine" };
    static UCAFResult CmdCinemachineDollyPath(UCAFCommand cmd) =>
        new UCAFResult { success = false, message = "Cinemachine not installed. Run: add_package name=com.unity.cinemachine" };

    static GameObject FindDirectorForTimeline(TimelineAsset timeline)
    {
        var directors = UnityEngine.Object.FindObjectsByType<PlayableDirector>(FindObjectsInactive.Include);
        foreach (var pd in directors)
            if (pd.playableAsset == timeline) return pd.gameObject;
        return null;
    }
}
