namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// The "host service" for `results/exportCsv`/`results/discard` — same
/// shape as `ConnectionService`. See
/// docs/superpowers/specs/2026-08-12-results-export-design.md.
module ExportService =

    /// Surfaces the worker's own structured error message (e.g.
    /// `results/nothingToExport`, `results/exportFailed`) as `Error`
    /// rather than swallowing it — same convention `ConnectionService.schema`
    /// already established for a comparable "real I/O can fail" case.
    let exportCsv (client: WorkerClient) (documentUri: string) (filePath: string) : Task<Result<unit, string>> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "results/exportCsv"
                  Params =
                    JsonSerializer.SerializeToElement<ExportCsvParams>(
                        ({ DocumentUri = documentUri; FilePath = filePath }: ExportCsvParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result, response.Error with
            | Some _, _ -> return Ok()
            | None, Some workerError -> return Result.Error workerError.Message
            | None, None -> return Result.Error "The worker returned no response."
        }

    /// `results/discard` never errors (Task 4's handler always succeeds),
    /// so this is a fire-and-forget `Task<unit>`, not a `Result` — matching
    /// how `ShellViewModel.CloseDocument` calls it (see `DiscardResultsAsync`).
    let discardResults (client: WorkerClient) (documentUri: string) : Task<unit> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "results/discard"
                  Params =
                    JsonSerializer.SerializeToElement<ResultsDiscardParams>(
                        ({ DocumentUri = documentUri }: ResultsDiscardParams),
                        Serialization.options
                    ) }

            let! _response = client.Send(request)
            ()
        }
