# Delta Spec: Local Workspace Project Support & Connection Scope

## ADDED Requirements (GEARS Syntax)

### FR-PROJ-01: Initialize Empty Project Command
```text
When the user triggers the command 'fs-query-studio.initEmptyProject',
the extension shall prompt for a target workspace folder if multiple or none are open,
and shall create an empty 'workspace.json' file with content '[]' and an empty interactive notebook 'notebook.fsqnb'.
```

### FR-PROJ-02: Connection Scope Selection Prompt
```text
Where at least one workspace folder or local project with 'workspace.json' is open in VS Code,
when the user initiates the 'fs-query-studio.connections.add' workflow,
the extension shall display a prompt asking the user to choose between 'Local (Current Project)' and 'Global (All Windows / Machine)'.
```

### FR-PROJ-03: Connection Registration with Target Directory
```text
Where the user selects 'Local (Current Project)' scope during connection creation,
when the connection definition is submitted,
the worker shall register the connection into the target project's 'workspace.json',
shall configure the connection resolver with the updated project connections,
and shall generate strongly-typed entities into '<project>/generated/GeneratedEntities.fs'.
```

## MODIFIED Requirements (GEARS Syntax)

### FR-CONN-REG: Worker Connection Registration
```text
When the worker receives a 'connection/register' request,
the worker shall inspect the optional 'workspaceDir' / 'targetDir' parameter;
where 'workspaceDir' is provided and valid,
the worker shall persist the connection definition into '<workspaceDir>/workspace.json';
otherwise,
the worker shall persist the connection definition into the global machine workspace '%LOCALAPPDATA%\FSharpQueryStudio\workspace.json'.
```
