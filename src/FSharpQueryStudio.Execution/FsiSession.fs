namespace FSharpQueryStudio.Execution

open System.IO
open FSharp.Compiler.Interactive.Shell

/// Isolated FSI evaluation session — see Section 9. Each worker process
/// handles exactly one execution, so mutating the process-wide current
/// directory here is safe: there is no second session to contend with it.
module FsiSession =

    type Session =
        { Fsi: FsiEvaluationSession
          Output: StringWriter
          Errors: StringWriter }

    /// Controlled working directory, redirected output/error, and no
    /// interactive console prompts (stdin reader is pre-exhausted so a
    /// prompt can never block waiting on it).
    let create (workingDirectory: string) : Session =
        Directory.SetCurrentDirectory(workingDirectory)

        let output = new StringWriter()
        let errors = new StringWriter()
        let argv = [| "fsi.exe"; "--noninteractive"; "--nologo"; "--gui-" |]
        let config = FsiEvaluationSession.GetDefaultConfiguration()
        let stdin = new StringReader("")

        let fsi =
            FsiEvaluationSession.Create(config, argv, stdin, output, errors, collectible = true)

        { Fsi = fsi
          Output = output
          Errors = errors }
