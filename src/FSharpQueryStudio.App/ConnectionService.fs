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
