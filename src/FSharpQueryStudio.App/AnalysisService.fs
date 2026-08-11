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
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisHoverParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result = JsonSerializer.Deserialize<AnalysisHoverResult>(resultJson, Serialization.options)
                return result.Hover
            | None -> return None
        }

    /// `analysis/definition` — Milestone E.6's go-to-definition. Same
    /// shape/rationale as `hover` above.
    let definition
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<DefinitionLocation option> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/definition"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisDefinitionParams>(
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisDefinitionParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result = JsonSerializer.Deserialize<AnalysisDefinitionResult>(resultJson, Serialization.options)
                return result.Location
            | None -> return None
        }

    /// `analysis/signatureHelp` — Milestone E.7. Same shape/rationale as
    /// `hover`/`definition` above.
    let signatureHelp
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<SignatureHelpInfo option> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/signatureHelp"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisSignatureHelpParams>(
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisSignatureHelpParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result =
                    JsonSerializer.Deserialize<AnalysisSignatureHelpResult>(resultJson, Serialization.options)

                return result.SignatureHelp
            | None -> return None
        }

    /// `analysis/complete` — same shape/rationale as `hover`.
    let complete
        (client: WorkerClient)
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<string list> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "analysis/complete"
                  Params =
                    JsonSerializer.SerializeToElement<AnalysisCompletionParams>(
                        ({ DocumentUri = documentUri
                           Version = version
                           Source = source
                           Line = line
                           Column = column }: AnalysisCompletionParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson ->
                let result =
                    JsonSerializer.Deserialize<AnalysisCompletionResult>(resultJson, Serialization.options)

                return result.Items
            | None -> return []
        }
