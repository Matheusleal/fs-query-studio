namespace FSharpQueryStudio.Execution

open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell

/// Runs a whole script through an isolated session and captures its raw
/// outcome — see Section 9. This is intentionally not yet the
/// protocol-neutral result shape from Section 11; that projection is a
/// later task and consumes `Value` here.
module Evaluation =

    type EvaluationOutcome =
        { Value: FsiValue option
          Output: string
          ErrorOutput: string
          Diagnostics: FSharpDiagnostic[]
          Exception: exn option }

    let private trimStringBuilder (sb: System.Text.StringBuilder) =
        sb.Clear() |> ignore
        if sb.Capacity > 4096 then
            sb.Capacity <- 4096

    let evaluate (session: FsiSession.Session) (source: string) : EvaluationOutcome =
        System.Console.SetOut(session.Output)
        System.Console.SetError(session.Errors)
        System.Console.SetIn(new System.IO.StringReader(""))
        trimStringBuilder (session.Output.GetStringBuilder())
        trimStringBuilder (session.Errors.GetStringBuilder())

        let normalizedSource = if source.EndsWith("\n") then source else source + "\n"
        let result, diagnostics = session.Fsi.EvalInteractionNonThrowing(normalizedSource)

        let value, exn =
            match result with
            | Choice1Of2 v -> v, None
            | Choice2Of2 ex -> None, Some ex

        let outputStr = session.Output.ToString()
        let errorStr = session.Errors.ToString()

        trimStringBuilder (session.Output.GetStringBuilder())
        trimStringBuilder (session.Errors.GetStringBuilder())

        { Value = value
          Output = outputStr
          ErrorOutput = errorStr
          Diagnostics = diagnostics
          Exception = exn }
