namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// The "host service" for `analysis/check`/`analysis/hover` — same shape
/// as `ExecutionService`, but neither call needs a tracked request id: the
/// caller's own version guard (Section 7 — `DocumentViewModel
/// .ApplyAnalysisResult`) is what protects against a stale response, not
/// anything on the request/cancel path evaluate needs.
module AnalysisService =

    let check (client: WorkerClient) (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/check"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisCheckParams>(
                        { DocumentUri = documentUri
                          Version = version
                          Source = source },
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result = JsonSerializer.Deserialize<AnalysisCheckResult>(resultJson, Serialization.options)
                return result.Diagnostics
            | None -> return [] // worker/crashed or similar - no diagnostics to show, not a crash of the host.
        }

    let hover
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<HoverInfo option> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/hover"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisHoverParams>(
                        { DocumentUri = documentUri
                          Version = version
                          Source = source
                          Line = line
                          Column = column },
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result = JsonSerializer.Deserialize<AnalysisHoverResult>(resultJson, Serialization.options)
                return result.Hover
            | None -> return None
        }
