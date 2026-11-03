namespace FSharpQueryStudio.Execution

open System
open System.IO
open FSharp.Compiler.Interactive.Shell

/// Isolated FSI evaluation session — see Section 9. Each worker process
/// handles exactly one execution, so mutating process-wide state here
/// (current directory, Console.Out/Error) is safe: there is no second
/// session to contend with it.
module FsiSession =

    type Session =
        { Fsi: FsiEvaluationSession
          Output: StringWriter
          Errors: StringWriter
          mutable GeneratedContent: string }

    let private resolveGeneratedFile (workingDirectory: string) (workspaceDirectory: string) : string =
        let cleanWsDir =
            if String.IsNullOrEmpty workspaceDirectory then ""
            else
                let unescaped = Uri.UnescapeDataString workspaceDirectory
                if unescaped.Length >= 3 && (unescaped.[0] = '/' || unescaped.[0] = '\\') && Char.IsLetter(unescaped.[1]) && unescaped.[2] = ':' then
                    unescaped.Substring(1)
                else
                    unescaped

        let wsGeneratedFsqs =
            if not (String.IsNullOrEmpty cleanWsDir) then
                Path.Combine(cleanWsDir, ".fsqs", "generated", "GeneratedEntities.fs")
            else
                ""
        let wsGenerated =
            if not (String.IsNullOrEmpty cleanWsDir) then
                Path.Combine(cleanWsDir, "generated", "GeneratedEntities.fs")
            else
                ""
        let localGeneratedFsqs = Path.Combine(workingDirectory, ".fsqs", "generated", "GeneratedEntities.fs")
        let localGenerated = Path.Combine(workingDirectory, "generated", "GeneratedEntities.fs")
        if not (String.IsNullOrEmpty wsGeneratedFsqs) && File.Exists wsGeneratedFsqs then
            wsGeneratedFsqs
        elif not (String.IsNullOrEmpty wsGenerated) && File.Exists wsGenerated then
            wsGenerated
        elif File.Exists localGeneratedFsqs then
            localGeneratedFsqs
        elif File.Exists localGenerated then
            localGenerated
        else
            ""

    let refreshGeneratedEntities (workingDirectory: string) (workspaceDirectory: string) (session: Session) : unit =
        let generatedFile = resolveGeneratedFile workingDirectory workspaceDirectory
        if not (String.IsNullOrEmpty generatedFile) && File.Exists generatedFile then
            let currentContent =
                try File.ReadAllText generatedFile
                with _ -> ""
            if not (String.IsNullOrWhiteSpace currentContent) && currentContent <> session.GeneratedContent then
                let generatedLoad = $"#load @\"{generatedFile}\"\nopen FSharpQueryStudio.Generated\n"
                let choice, errs = session.Fsi.EvalInteractionNonThrowing(generatedLoad)
                session.Output.GetStringBuilder().Clear() |> ignore
                session.Errors.GetStringBuilder().Clear() |> ignore
                match choice with
                | Choice2Of2 ex ->
                    let diag = errs |> Seq.map (fun (e: FSharp.Compiler.Diagnostics.FSharpDiagnostic) -> e.Message) |> String.concat "; "
                    failwithf "#load of '%s' failed: %s (Diagnostics: %s)" generatedFile ex.Message diag
                | Choice1Of2 _ ->
                    session.GeneratedContent <- currentContent

    /// Controlled working directory, redirected output/error, and no
    /// interactive console prompts (stdin reader is pre-exhausted so a
    /// prompt can never block waiting on it).
    ///
    /// The session's own outWriter/errorWriter only capture FSI's REPL
    /// echo (e.g. "val it: int = 2") — a script's own `printfn`/
    /// `Console.WriteLine` calls go through `System.Console.Out`, which is
    /// a separate global unless explicitly redirected too. Both are
    /// pointed at the same writers so captured output is complete.
    let createInWorkspace (workingDirectory: string) (workspaceDirectory: string) : Session =
        Directory.SetCurrentDirectory(workingDirectory)

        let output = new StringWriter()
        let errors = new StringWriter()
        let stdin = new StringReader("")
        Console.SetOut(output)
        Console.SetError(errors)
        Console.SetIn(stdin)

        let argv = [| "fsi.exe"; "--noninteractive"; "--nologo"; "--gui-"; "--readline-"; "--simpleresolution" |]
        let config = FsiEvaluationSession.GetDefaultConfiguration()

        let fsi =
            FsiEvaluationSession.Create(config, argv, stdin, output, errors, collectible = true)

        let protocolAssemblyPath =
            typeof<FSharpQueryStudio.Protocol.ConnectorKind>.Assembly.Location

        let abstractionsAssemblyPath =
            typeof<FSharpQueryStudio.Connectors.IConnector>.Assembly.Location

        let connectorsAssemblyPath =
            typeof<FSharpQueryStudio.Connectors.WorkspaceConnectionResolver>.Assembly.Location

        let resultsAssemblyPath =
            typeof<FSharpQueryStudio.Results.PagingCursor>.Assembly.Location

        let baseDir = Path.GetDirectoryName(connectorsAssemblyPath)
        let connectorModulePaths =
            if not (String.IsNullOrEmpty baseDir) && Directory.Exists baseDir then
                Directory.GetFiles(baseDir, "FSharpQueryStudio.Connectors.*.dll")
                |> Array.sort
                |> Array.toList
            else
                [ abstractionsAssemblyPath ]

        let allRefs =
            ([ protocolAssemblyPath; abstractionsAssemblyPath ] @ connectorModulePaths @ [ connectorsAssemblyPath; resultsAssemblyPath ])
            |> List.filter (fun p -> not (String.IsNullOrWhiteSpace p))
            |> List.distinct
            |> List.map (fun p -> $"#r @\"{p}\"")
            |> String.concat "\n"

        let basePrologue =
            $"{allRefs}\nopen System.Linq\nopen FSharpQueryStudio.Protocol\nopen FSharpQueryStudio.Connectors\n"

        fsi.EvalInteractionNonThrowing(basePrologue) |> ignore

        let session =
            { Fsi = fsi
              Output = output
              Errors = errors
              GeneratedContent = "" }

        refreshGeneratedEntities workingDirectory workspaceDirectory session

        // Clear whatever FSI's own reference/echo messages the prologue
        // produced (e.g. "--> Referenced ...") - a script's own captured
        // Output/ErrorOutput (Evaluation.evaluate) must reflect only what
        // the user's own code printed, not this host-injected setup step.
        output.GetStringBuilder().Clear() |> ignore
        errors.GetStringBuilder().Clear() |> ignore

        session

    let create (workingDirectory: string) : Session =
        createInWorkspace workingDirectory ""
