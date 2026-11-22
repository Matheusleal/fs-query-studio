namespace FSharpQueryStudio.Worker

open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

/// `ISecretStore` backed by the extension host over `HostChannel` (F5).
/// The connection resolver reads `{password}` via `TryReadAsync` (or synchronously
/// via `TryRead` for legacy callers). Any failure (timeout, error frame,
/// null/non-string result, exception, cancellation) degrades to `None`,
/// which surfaces downstream as an ordinary connection failure — the same contract
/// `ConnectionStringSecrets.resolve` already documents for a missing secret.
/// Read-only: the extension owns every credential write.
type BridgeSecretStore(host: HostChannel, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout (TimeSpan.FromSeconds 5.0)

    let notSupported () : 'a =
        raise (NotSupportedException "BridgeSecretStore is read-only; the extension host owns secret writes.")

    let extractSecret (frame: WorkerResponseFrame) : string option =
        match frame.Error, frame.Result with
        | Some _, _
        | None, None -> None
        | None, Some element ->
            match element.TryGetProperty "secret" with
            | true, secret when secret.ValueKind = JsonValueKind.String -> Some(secret.GetString())
            | _ -> None

    member this.TryReadAsync(reference: string, ct: CancellationToken) : Task<string option> =
        task {
            try
                use cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(timeout)
                let request = host.Request("secret/get", {| reference = reference |})
                let! completed = Task.WhenAny(request, Task.Delay(-1, cts.Token))

                if completed = (request :> Task) then
                    let! frame = request
                    return extractSecret frame
                else
                    return None
            with _ ->
                return None
        }

    interface ISecretStore with
        member this.TryReadAsync(reference: string, ct: CancellationToken) : Task<string option> =
            this.TryReadAsync(reference, ct)

        member this.TryRead(reference: string) : string option =
            (this :> ISecretStore).TryReadAsync(reference, CancellationToken.None).GetAwaiter().GetResult()

        member _.Save (_: string) (_: string) : unit = notSupported ()
        member _.Delete(_: string) : unit = notSupported ()
