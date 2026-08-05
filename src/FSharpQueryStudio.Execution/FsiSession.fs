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

        { Fsi = fsi
          Output = output
          Errors = errors }
