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
    let create (workingDirectory: string) : Session =
        Directory.SetCurrentDirectory(workingDirectory)

        let output = new StringWriter()
        let errors = new StringWriter()
        Console.SetOut(output)
        Console.SetError(errors)

        let argv = [| "fsi.exe"; "--noninteractive"; "--nologo"; "--gui-" |]
        let config = FsiEvaluationSession.GetDefaultConfiguration()
        let stdin = new StringReader("")

        let fsi =
            FsiEvaluationSession.Create(config, argv, stdin, output, errors, collectible = true)

        // Every script reaches the controlled `Connections.sql`/`csv`/
        // `excel`/`rest` API (Section 10/11) with no
        // #r/open of its own - the architecture spec's own example script
        // calls `Connections.sql "Sales" ...` directly. Run as a separate
        // interaction, before any user source, so it never shifts the
        // user script's own line numbers - Diagnostic mapping elsewhere
        // in this project is 1-based on FCS's own line numbers and must
        // stay aligned with exactly what the user typed.
        let connectorsAssemblyPath =
            typeof<FSharpQueryStudio.Connectors.IConnector>.Assembly.Location

        let resultsAssemblyPath =
            typeof<FSharpQueryStudio.Results.PagingCursor>.Assembly.Location

        let prologue =
            $"#r @\"{connectorsAssemblyPath}\"\n#r @\"{resultsAssemblyPath}\"\nopen FSharpQueryStudio.Connectors"

        fsi.EvalInteractionNonThrowing(prologue) |> ignore

        // Clear whatever FSI's own reference/echo messages the prologue
        // produced (e.g. "--> Referenced ...") - a script's own captured
        // Output/ErrorOutput (Evaluation.evaluate) must reflect only what
        // the user's own code printed, not this host-injected setup step.
        output.GetStringBuilder().Clear() |> ignore
        errors.GetStringBuilder().Clear() |> ignore

        { Fsi = fsi
          Output = output
          Errors = errors }
