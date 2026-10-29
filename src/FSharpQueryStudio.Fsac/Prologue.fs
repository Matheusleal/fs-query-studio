namespace FSharpQueryStudio.Fsac

/// The controlled prologue every script checked through FSAC gets
/// prepended with, so `Connections.sql` resolves
/// unqualified — matching what real execution (`Execution.FsiSession
/// .create`) already does, and what `Analysis.AnalysisContext` used to do
/// via FCS `otherFlags`. Unlike `otherFlags` (an FCS-project-options-only
/// mechanism with no FSAC equivalent), this uses real `#r`/`open` lines
/// physically in the checked text — an ordinary FCS/FSI script directive
/// FSAC's own FCS-backed script handling resolves with no special
/// configuration on FSAC's side.
module Prologue =

    /// `#r "..."` is a real F# string literal, parsed by FSAC's own
    /// F#-literal escape rules — a raw Windows path's single backslashes
    /// (e.g. `\fs-query-studio`, `\net10.0`) are NOT inert path separators
    /// there, they're escape-sequence introducers (`\f` = form feed, `\n`
    /// = newline, ...) for any backslash followed by a lowercase letter
    /// F# recognizes. Confirmed against real fsautocomplete during this
    /// task: an unescaped path silently corrupted mid-string and FSAC
    /// reported FS0213 "not a valid assembly name" for every check.
    /// Doubling backslashes first makes the literal round-trip intact.
    let private escapeForStringLiteral (path: string) : string = path.Replace("\\", "\\\\")

    let mutable private workspaceDirOverride: string option = None
    let setWorkspaceDirForTests (dir: string option) : unit = workspaceDirOverride <- dir

    let resolveWorkspaceForDocument (docUriStr: string) : string option =
        try
            if not (System.String.IsNullOrWhiteSpace docUriStr) then
                let unescaped = System.Uri.UnescapeDataString docUriStr
                let rawPath =
                    if unescaped.StartsWith("vscode-notebook-cell:", System.StringComparison.OrdinalIgnoreCase) then
                        let withoutScheme = unescaped.Substring("vscode-notebook-cell:".Length)
                        let hashIdx = withoutScheme.IndexOf('#')
                        let pathPart = if hashIdx >= 0 then withoutScheme.Substring(0, hashIdx) else withoutScheme
                        if pathPart.StartsWith("///") then pathPart.Substring(2)
                        elif pathPart.StartsWith("//") && pathPart.Length >= 4 && System.Char.IsLetter(pathPart.[2]) && pathPart.[3] = ':' then pathPart.Substring(2)
                        elif pathPart.StartsWith("//") then pathPart.Substring(1)
                        else pathPart
                    elif unescaped.StartsWith("file:", System.StringComparison.OrdinalIgnoreCase) then
                        let uri = System.Uri(unescaped)
                        uri.LocalPath
                    else
                        let colonIdx = unescaped.IndexOf(':')
                        let pathPart = if colonIdx >= 0 then unescaped.Substring(colonIdx + 1) else unescaped
                        let hashIdx = pathPart.IndexOf('#')
                        if hashIdx >= 0 then pathPart.Substring(0, hashIdx) else pathPart
                let rawDir = System.IO.Path.GetDirectoryName(rawPath)
                let dir =
                    if not (System.String.IsNullOrEmpty rawDir) && rawDir.Length >= 3 && (rawDir.[0] = '/' || rawDir.[0] = '\\') && System.Char.IsLetter(rawDir.[1]) && rawDir.[2] = ':' then
                        rawDir.Substring(1)
                    else
                        rawDir
                if not (System.String.IsNullOrWhiteSpace dir) && System.IO.Directory.Exists dir then
                    let rec findWorkspace (current: System.IO.DirectoryInfo) (depth: int) =
                        if depth <= 0 || isNull current then None
                        elif System.IO.Directory.Exists(System.IO.Path.Combine(current.FullName, ".fsqs"))
                             || System.IO.File.Exists(System.IO.Path.Combine(current.FullName, ".fsqs", "workspace.json"))
                             || System.IO.File.Exists(System.IO.Path.Combine(current.FullName, ".fsqs", "generated", "GeneratedEntities.fs"))
                             || System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "workspace.json"))
                             || System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "generated", "GeneratedEntities.fs"))
                             || System.IO.File.Exists(System.IO.Path.Combine(current.FullName, ".fsquery", "connections.json")) then
                            Some current.FullName
                        else
                            findWorkspace current.Parent (depth - 1)
                    match findWorkspace (System.IO.DirectoryInfo(dir)) 5 with
                    | Some found -> Some found
                    | None -> Some dir
                else
                    None
            else
                None
        with _ ->
            None

    let buildInWorkspace (workspaceDir: string option) : string =
        let abstractionsPath = (typeof<FSharpQueryStudio.Connectors.IConnector>).Assembly.Location
        let connectorsPath = (typeof<FSharpQueryStudio.Connectors.WorkspaceConnectionResolver>).Assembly.Location
        let resultsPath = (typeof<FSharpQueryStudio.Results.PagingCursor>).Assembly.Location
        let sqlProviderCommonPath = (typeof<FSharp.Data.Sql.Common.DatabaseProviderTypes>).Assembly.Location
        let sqlProviderPath =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(sqlProviderCommonPath),
                "FSharp.Data.SqlProvider.dll"
            )

        let targetGenerated =
            let checkDir dir =
                let generatedFsqs = System.IO.Path.Combine(dir, ".fsqs", "generated", "GeneratedEntities.fs")
                let generatedFile = System.IO.Path.Combine(dir, "generated", "GeneratedEntities.fs")
                if System.IO.File.Exists generatedFsqs then Some generatedFsqs
                elif System.IO.File.Exists generatedFile then Some generatedFile
                else None

            let explicitDir = workspaceDir |> Option.orElse workspaceDirOverride
            match explicitDir with
            | Some dir ->
                match checkDir dir with
                | Some f -> f
                | None ->
                    let fallbackBase =
                        System.IO.Path.Combine(
                            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                            "FSharpQueryStudio"
                        )
                    checkDir fallbackBase |> Option.defaultValue ""
            | None ->
                let fallbackBase =
                    System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "FSharpQueryStudio"
                    )
                checkDir fallbackBase |> Option.defaultValue ""

        let generatedLoad =
            if not (System.String.IsNullOrEmpty targetGenerated) then
                $"#load \"{escapeForStringLiteral targetGenerated}\"\nopen FSharpQueryStudio.Generated\n"
            else
                ""

        $"#r \"{escapeForStringLiteral abstractionsPath}\"\n#r \"{escapeForStringLiteral connectorsPath}\"\n#r \"{escapeForStringLiteral resultsPath}\"\n#r \"{escapeForStringLiteral sqlProviderCommonPath}\"\n#r \"{escapeForStringLiteral sqlProviderPath}\"\n{generatedLoad}open System.Linq\nopen FSharpQueryStudio.Connectors\nopen FSharp.Data.Sql\n"

    let build () : string = buildInWorkspace None

    let buildForDocument (docUriStr: string) : string =
        let wsOpt = resolveWorkspaceForDocument docUriStr
        buildInWorkspace wsOpt

    /// The number of real (newline-terminated) lines `prologueText`
    /// occupies — computed from the actual text rather than a hardcoded
    /// constant, so a future edit to `build()` can't silently desync the
    /// offset math from what's really being prepended.
    let lineCount (prologueText: string) : int =
        prologueText.Split('\n').Length - 1

    let sourceForCheck (source: string) : string = build () + source

    let sourceForCheckForDocument (docUriStr: string) (source: string) : string =
        buildForDocument docUriStr + source

    /// A caller's own 1-based line (against the unprefixed source) ->
    /// the line to ask FSAC about in the prologue-prefixed document.
    let shiftUp (line: int) : int = line + lineCount (build ())

    let shiftUpForDocument (docUriStr: string) (line: int) : int =
        line + lineCount (buildForDocument docUriStr)

    /// An FSAC-reported line *inside this same checked document* -> the
    /// caller's real, unprefixed line. Never apply this to a location FSAC
    /// resolves in a different file (FSharp.Core, another script) — that
    /// has its own real, unshifted lines, same caveat `AnalysisContext
    /// .lineOffset`'s doc comment already carries.
    let shiftDown (line: int) : int = line - lineCount (build ())

    let shiftDownForDocument (docUriStr: string) (line: int) : int =
        line - lineCount (buildForDocument docUriStr)

