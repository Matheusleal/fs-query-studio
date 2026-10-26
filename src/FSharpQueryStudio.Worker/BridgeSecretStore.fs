namespace FSharpQueryStudio.Worker

open System
open System.Text.Json
open FSharpQueryStudio.Security

/// `ISecretStore` backed by the extension host over `HostChannel` (F5).
/// The connection resolver reads `{password}` synchronously mid-evaluation
/// (`ConnectionStringSecrets.resolve`), so `TryRead` blocks on the
/// host-bound `secret/get` — safe because `runEvaluate` /
/// `handleConnectionSchema` already run on a background task, leaving the
/// read loop free to deliver the reply. Any failure (timeout, error frame,
/// null/non-string result, exception) degrades to `None`, which surfaces
/// downstream as an ordinary connection failure — the same contract
/// `ConnectionStringSecrets.resolve` already documents for a missing
/// secret. Read-only: the extension owns every credential write.
type BridgeSecretStore(host: HostChannel, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout (TimeSpan.FromSeconds 5.0)

    let notSupported () : 'a =
        raise (NotSupportedException "BridgeSecretStore is read-only; the extension host owns secret writes.")

    interface ISecretStore with
        member _.TryRead(reference: string) : string option =
            try
                let request = host.Request("secret/get", {| reference = reference |})

                if request.Wait timeout then
                    let frame = request.Result

                    match frame.Error, frame.Result with
                    | Some _, _
                    | None, None -> None
                    | None, Some element ->
                        match element.TryGetProperty "secret" with
                        | true, secret when secret.ValueKind = JsonValueKind.String -> Some(secret.GetString())
                        | _ -> None
                else
                    None
            with _ ->
                None

        member _.Save (_: string) (_: string) : unit = notSupported ()
        member _.Delete(_: string) : unit = notSupported ()
