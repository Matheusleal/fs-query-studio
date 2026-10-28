# Delta Spec: Project Auto-Upgrader & .fsqs Workspace Schema v2

## ADDED Requirements (GEARS Syntax)

### FR-WS-SCHEMA-V2: Workspace Configuration Schema v2 Structure
```text
When serializing workspace settings for a project or global storage,
the storage module shall format 'workspace.json' as a JSON object containing 'schemaVersion' (integer value 2), 'extensionVersion' (string semver), and 'connections' (array of ConnectionDefinition).
```

### FR-WS-DESERIALIZE-COMPAT: Backward-Compatible Workspace Deserialization
```text
When reading 'workspace.json',
the storage module shall check whether the root JSON token is an array or an object;
where the root token is an array,
the storage module shall parse it as legacy Schema v1 connections;
where the root token is an object,
the storage module shall read 'schemaVersion', 'extensionVersion', and the 'connections' property.
```

### FR-UPGRADE-STRUCTURAL: Automatic Structural Relocation to .fsqs
```text
Where a project root directory contains a legacy 'workspace.json' or 'generated/' directory outside the '.fsqs' folder,
when project workspace access or upgrade is triggered,
the project upgrader shall ensure the '.fsqs' directory exists,
shall relocate 'workspace.json' to '.fsqs/workspace.json',
shall relocate 'generated/' contents to '.fsqs/generated/',
and shall remove the legacy files and folders from the project root.
```

### FR-UPGRADE-SCHEMA: Automatic Schema Migration to Version 2
```text
Where a workspace contains a 'workspace.json' with Schema version 1 or a bare JSON array,
when project workspace access or upgrade is triggered,
the project upgrader shall migrate the existing connection definitions into the Schema v2 format,
shall set 'schemaVersion' to 2,
shall record the current 'extensionVersion',
and shall overwrite '.fsqs/workspace.json' with the upgraded contents.
```

### FR-UPGRADE-EXTENSIBLE-PIPELINE: Sequential Version Migration Engine
```text
When the project upgrader evaluates a project workspace,
the project upgrader shall execute registered migration steps sequentially in ascending order of version until the project reaches the current schema version.
```

### FR-UPGRADE-WORKER-RPC: Workspace Upgrade Worker Method
```text
When the worker receives a 'workspace/upgrade' request with a target project directory,
the worker shall execute the project upgrader for that directory
and shall return an upgrade report containing 'upgraded' (boolean), 'previousVersion' (integer), 'currentVersion' (integer), and 'actions' (list of executed migration steps).
```

## MODIFIED Requirements (GEARS Syntax)

### FR-STORAGE-FSQS: Canonical Storage Directory Resolution
```text
When resolving the internal storage path for any project directory,
the storage module shall resolve the path to '<projectDir>/.fsqs',
except where the target directory is already named '.fsqs' or is the dedicated application data directory 'FSharpQueryStudio'.
```

### FR-PROJ-INIT-FSQS: Initialize Empty Project with Schema v2 Template
```text
When the worker receives a 'workspace/initEmptyProject' request,
the worker shall create the '<projectDir>/.fsqs' folder,
shall create '<projectDir>/.fsqs/generated',
and shall create '<projectDir>/.fsqs/workspace.json' containing an empty Schema v2 structure with 'schemaVersion: 2', 'extensionVersion', and 'connections: []'.
```
