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
                        { Id = definition.Id },
                        Serialization.options
                    ) }

            let! response = client.Send(request)

            match response.Result with
            | Some resultJson -> return JsonSerializer.Deserialize<ConnectionDefinition list>(resultJson, Serialization.options)
            | None -> return []
        }
