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
          Errors: StringWriter }

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

        let connectorsAssemblyPath =
            typeof<FSharpQueryStudio.Connectors.IConnector>.Assembly.Location

        let resultsAssemblyPath =
            typeof<FSharpQueryStudio.Results.PagingCursor>.Assembly.Location

        let sqlProviderCommonAssemblyPath =
            typeof<FSharp.Data.Sql.Common.DatabaseProviderTypes>.Assembly.Location

        let sqlProviderAssemblyPath =
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(sqlProviderCommonAssemblyPath),
                "FSharp.Data.SqlProvider.dll"
            )

        let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        let wsGenerated =
            if not (String.IsNullOrEmpty workspaceDirectory) then
                Path.Combine(workspaceDirectory, "generated", "GeneratedEntities.fs")
            else
                ""
        let localGenerated = Path.Combine(workingDirectory, "generated", "GeneratedEntities.fs")
        let globalGenerated = Path.Combine(appData, "FSharpQueryStudio", "generated", "GeneratedEntities.fs")
        let generatedFile =
            if not (String.IsNullOrEmpty workspaceDirectory) then
                if File.Exists wsGenerated then wsGenerated else ""
            elif File.Exists localGenerated then
                localGenerated
            elif File.Exists globalGenerated then
                globalGenerated
            else
                ""

        let basePrologue =
            $"#r @\"{connectorsAssemblyPath}\"\n#r @\"{resultsAssemblyPath}\"\nopen System.Linq\nopen FSharpQueryStudio.Connectors\n"

        fsi.EvalInteractionNonThrowing(basePrologue) |> ignore

        if not (String.IsNullOrEmpty generatedFile) && File.Exists generatedFile then
            let generatedLoad = $"#load @\"{generatedFile}\"\nopen FSharpQueryStudio.Generated\n"
            fsi.EvalInteractionNonThrowing(generatedLoad) |> ignore

        // Clear whatever FSI's own reference/echo messages the prologue
        // produced (e.g. "--> Referenced ...") - a script's own captured
        // Output/ErrorOutput (Evaluation.evaluate) must reflect only what
        // the user's own code printed, not this host-injected setup step.
        output.GetStringBuilder().Clear() |> ignore
        errors.GetStringBuilder().Clear() |> ignore

        { Fsi = fsi
          Output = output
          Errors = errors }

    let create (workingDirectory: string) : Session =
        createInWorkspace workingDirectory ""
