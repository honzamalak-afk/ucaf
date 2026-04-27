# UCAF — Unity Claude Assisted Framework

File-based IPC protocol that enables Claude (via Claude Code) to control the Unity Editor. Claude writes JSON command files, Unity executes them and writes results back.

## Features
- 100+ commands: scenes, objects, components, packages, play mode, tests, builds, profiler, timeline, NavMesh, lightmaps, recording, and more
- Domain reload resilient (survives compile cycles)
- NDJSON console streaming
- Integrated bug ledger
- Works with Unity 6+

## Setup for a new project

```powershell
.\New-UnityUCAF.ps1 -ProjectPath "C:\Projects\MyGame" -UnityVersion "6000.4.3f1" -RenderPipeline "URP"
```

This will:
1. Copy workspace template (`ucaf_workspace/`) into your project
2. Patch `ucaf_config.json` with correct paths
3. Add `com.ucaf` to `Packages/manifest.json`
4. Generate a `CLAUDE.md` for Claude Code context

Open the project in Unity — UCAF activates automatically via `[InitializeOnLoad]`.

## Install via UPM (manual)

Add to `Packages/manifest.json`:
```json
"com.ucaf": "https://github.com/honzamalak-afk/ucaf.git"
```

## Command format

**Write to** `ucaf_workspace/commands/pending/{id}.json`:
```json
{
  "id": "my-command-1",
  "type": "get_console",
  "timestamp": "2026-04-27T10:00:00Z",
  "params_list": [
    { "key": "since", "value": "2026-04-27T09:00:00Z" }
  ]
}
```

**Read from** `ucaf_workspace/commands/done/{id}.json`.

## Unity version
Requires Unity 6000.0 or later.
