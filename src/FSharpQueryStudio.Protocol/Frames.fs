namespace FSharpQueryStudio.Protocol

open System.Text.Json


/// One NDJSON line sent to the worker over stdin — see Section 7.
type WorkerRequestFrame =
    { Id: string
      Method: string
      Params: JsonElement }

type WorkerErrorInfo = { Code: string; Message: string }

/// One NDJSON line sent back from the worker over stdout — see Section 7.
type WorkerResponseFrame =
    { Id: string
      Result: JsonElement option
      Error: WorkerErrorInfo option }
