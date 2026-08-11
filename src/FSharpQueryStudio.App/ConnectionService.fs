namespace FSharpQueryStudio.App

open System
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// The "host service" for `connection/register` — same shape as
/// `ExecutionService`/`AnalysisService`. `register` is the one place a
/// plaintext secret (e.g. a typed-in database password) exists outside
/// the OS credential store: it's saved via `secretStore.Save` *before*
/// the definition (carrying only the resulting `SecretReference`, never
/// the secret itself) is sent to the worker to persist to
/// `workspace.json`.
module ConnectionService =

    let register
        (client: WorkerClient)
        (secretStore: ISecretStore)
        (definition: ConnectionDefinition)
        (secret: string option)
        : Task<ConnectionDefinition list> =
        task {
            match definition.SecretReference, secret with
            | Some reference, Some value -> secretStore.Save reference value
            | _ -> ()

            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "connection/register"
                  Params = JsonSerializer.SerializeToElement<ConnectionDefinition>(definition, Serialization.options) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson -> return JsonSerializer.Deserialize<ConnectionDefinition list>(resultJson, Serialization.options)
            | None -> return []
        }

    /// The "host service" for `connection/remove` — deletes the
    /// connection's own credential (if it had one) from the OS
    /// credential store *before* asking the worker to drop it from
    /// `workspace.json`, the same save-before-send ordering `register`
    /// uses for the opposite direction. Deleting first means a crash or
    /// a dropped connection between the two steps leaves an orphaned
    /// credential behind rather than a `workspace.json` entry pointing
    /// at a `SecretReference` nothing will ever read again — the safer
    /// of the two possible leaks.
    let remove (client: WorkerClient) (secretStore: ISecretStore) (definition: ConnectionDefinition) : Task<ConnectionDefinition list> =
        task {
            definition.SecretReference |> Option.iter secretStore.Delete

            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "connection/remove"
                  Params =
                    JsonSerializer.SerializeToElement<ConnectionRemoveParams>(
                        ({ Id = definition.Id }: ConnectionRemoveParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson -> return JsonSerializer.Deserialize<ConnectionDefinition list>(resultJson, Serialization.options)
            | None -> return []
        }

    /// The "host service" for `connection/schema` — Section 10's schema
    /// discovery, wired to the UI for the first time. Real I/O (opening a
    /// database connection, reading a file's header, ...) stays in the
    /// worker process, same architectural boundary `register`/`remove`
    /// already draw; this is a thin request/response wrapper, nothing
    /// more. Surfaces the worker's own structured error message (e.g. an
    /// unknown `Id`, or a real connectivity failure) as `Error` rather
    /// than silently returning an empty table list, so the Workspace
    /// panel can show the user *why* nothing came back.
    let schema (client: WorkerClient) (id: Guid) : Task<Result<SchemaTable list, string>> =
        task {
            let request: WorkerRequestFrame =
                { Id = Guid.NewGuid().ToString()
                  Method = "connection/schema"
                  Params =
                    JsonSerializer.SerializeToElement<ConnectionSchemaParams>(
                        ({ Id = id }: ConnectionSchemaParams),
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result, response.Error with
            | Some resultJson, _ ->
                let result = JsonSerializer.Deserialize<ConnectionSchemaResult>(resultJson, Serialization.options)
                return Ok result.Tables
            | None, Some workerError -> return Result.Error workerError.Message
            | None, None -> return Result.Error "The worker returned no response."
        }
