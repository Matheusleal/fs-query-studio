namespace FSharpQueryStudio.Fsac

open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Protocol

type LspError = { Code: int; Message: string }

type LspResult =
    | LspOk of JsonElement
    | LspFail of LspError

/// A minimal JSON-RPC 2.0 client over `LspTransport`'s framing — the
/// Worker<->fsautocomplete leg. Owns request/response correlation by
/// integer id and dispatches server-initiated notifications
/// (`textDocument/publishDiagnostics`, etc.) to whatever handler is
/// registered for that method *at the moment the notification arrives* —
/// same "no buffering for a not-yet-registered listener" tradeoff
/// `WorkerClient`'s own `pending` dictionary accepts for responses that
/// arrive before anyone is waiting.
type LspClient(input: Stream, output: Stream) =
    let mutable nextId = 0
    let pending = ConcurrentDictionary<int, TaskCompletionSource<LspResult>>()
    let notificationHandlers = ConcurrentDictionary<string, JsonElement -> unit>()
    let writeLock = new SemaphoreSlim(1, 1)
    let nullElement = JsonDocument.Parse("null").RootElement

    let handleIncoming (json: string) =
        let root = JsonDocument.Parse(json).RootElement

        let tryGetInt (name: string) =
            match root.TryGetProperty(name) with
            | true, el when el.ValueKind = JsonValueKind.Number -> Some(el.GetInt32())
            | _ -> None

        let tryGetString (name: string) =
            match root.TryGetProperty(name) with
            | true, el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
            | _ -> None

        let tryGetElement (name: string) =
            match root.TryGetProperty(name) with
            | true, el -> Some el
            | false, _ -> None

        match tryGetInt "id", tryGetString "method" with
        | Some id, _ ->
            match pending.TryRemove(id) with
            | false, _ -> () // no one is waiting on this id anymore
            | true, tcs ->
                match tryGetElement "error" with
                | Some errEl ->
                    let err = JsonSerializer.Deserialize<LspError>(errEl.GetRawText(), Serialization.options)
                    tcs.TrySetResult(LspFail err) |> ignore
                | None ->
                    let result = tryGetElement "result" |> Option.defaultValue nullElement
                    tcs.TrySetResult(LspOk result) |> ignore
        | None, Some method_ ->
            match notificationHandlers.TryGetValue(method_) with
            | true, handler ->
                try
                    handler (tryGetElement "params" |> Option.defaultValue nullElement)
                with ex ->
                    // A misbehaving handler must not fault the reader loop's background
                    // Task — that would silently stop all future reads, hanging every
                    // pending and future SendRequest forever with no signal.
                    Log.Warning(ex, "Notification handler for {Method} threw; continuing", method_)
            | false, _ -> () // nobody registered for this notification
        | None, None -> () // not a valid LSP message - ignored rather than crashing the reader loop

    let startReaderLoop () =
        Task.Run(fun () ->
            (task {
                let mutable keepGoing = true

                while keepGoing do
                    let! message = LspTransport.readMessage input

                    match message with
                    | None ->
                        keepGoing <- false

                        for id in pending.Keys |> Seq.toList do
                            match pending.TryRemove(id) with
                            | true, tcs ->
                                tcs.TrySetResult(LspFail { Code = -1; Message = "the LSP server process exited" })
                                |> ignore
                            | false, _ -> ()
                    | Some json -> handleIncoming json
            })
            :> Task)
        |> ignore

    do startReaderLoop ()

    let send (envelope: obj) : Task =
        task {
            let json = JsonSerializer.Serialize(envelope, Serialization.options)
            do! writeLock.WaitAsync()

            try
                do! LspTransport.writeMessage output json
            finally
                writeLock.Release() |> ignore
        }

    /// Registers `handler` for every future `method_`-named notification.
    /// Must be called before the notification of interest can arrive.
    member _.OnNotification(method_: string, handler: JsonElement -> unit) : unit =
        notificationHandlers.[method_] <- handler

    member _.SendRequest<'TParams>(method_: string, requestParams: 'TParams) : Task<LspResult> =
        task {
            let id = Interlocked.Increment(&nextId)
            let tcs = TaskCompletionSource<LspResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            pending.[id] <- tcs

            do!
                send (
                    {| jsonrpc = "2.0"
                       id = id
                       method = method_
                       ``params`` = requestParams |}
                )

            return! tcs.Task
        }

    member _.SendNotification<'TParams>(method_: string, notificationParams: 'TParams) : Task =
        send (
            {| jsonrpc = "2.0"
               method = method_
               ``params`` = notificationParams |}
        )
