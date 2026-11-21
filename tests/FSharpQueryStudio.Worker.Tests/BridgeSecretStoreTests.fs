module BridgeSecretStoreTests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security
open FSharpQueryStudio.Worker
open TestHelpers

let private replyWith (result: obj option) (error: WorkerErrorInfo option) (channel: HostChannel) (id: string) =
    channel.TryComplete
        { Id = id
          Result = result |> Option.map (fun r -> JsonSerializer.SerializeToElement(r, Serialization.options))
          Error = error }
    |> ignore

[<Fact>]
let ``TryRead returns the host's secret`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read =
            task {
                do! Task.Yield()
                return store.TryRead "fqs-conn-1"
            }
        // let the Request frame be written, then answer it
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = "hunter2" |})) None channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(Some "hunter2", result)
    }

[<Fact>]
let ``TryRead returns None on an error frame`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read =
            task {
                do! Task.Yield()
                return store.TryRead "x"
            }
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith None (Some { Code = "host/unknownMethod"; Message = "no" }) channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryRead returns None on a null secret`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read =
            task {
                do! Task.Yield()
                return store.TryRead "x"
            }
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = (null: string) |})) None channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryRead returns None when the host never replies (timeout)`` () =
    let channel = HostChannel(fun _ -> Task.CompletedTask)
    let store = BridgeSecretStore(channel, TimeSpan.FromMilliseconds 150.0) :> ISecretStore
    Assert.Equal(None, store.TryRead "x")

[<Fact>]
let ``Save and Delete are not supported`` () =
    let store = BridgeSecretStore(HostChannel(fun _ -> Task.CompletedTask)) :> ISecretStore
    Assert.Throws<NotSupportedException>(fun () -> store.Save "r" "s") |> ignore
    Assert.Throws<NotSupportedException>(fun () -> store.Delete "r") |> ignore

[<Fact>]
let ``TryRead returns None when the HostChannel write throws`` () =
    let channel = HostChannel(fun _ -> failwith "simulated write failure")
    let store = BridgeSecretStore(channel) :> ISecretStore
    Assert.Equal(None, store.TryRead "ref")

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 1: Successful read)
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryReadAsync returns the host's secret`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read = store.TryReadAsync("fqs-conn-1", CancellationToken.None)
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = "hunter2" |})) None channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(Some "hunter2", result)
    }

[<Fact>]
let ``TryReadAsync returns secret when reference contains special characters`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read = store.TryReadAsync("vault://org/app-1:password#special!", CancellationToken.None)
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = "p@$$w0rd#123" |})) None channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(Some "p@$$w0rd#123", result)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 2: Cancellation and Timeout)
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryReadAsync returns None when pre-cancelled CancellationToken is provided`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore
        use cts = new CancellationTokenSource()
        cts.Cancel()

        let! result = store.TryReadAsync("any-ref", cts.Token)
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryReadAsync returns None when CancellationToken is cancelled during wait`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let store = BridgeSecretStore(channel, TimeSpan.FromSeconds 10.0) :> ISecretStore
        use cts = new CancellationTokenSource()

        let readTask = store.TryReadAsync("slow-ref", cts.Token)
        cts.CancelAfter(50)

        let! result = readTask |> withTimeout 45.0
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryReadAsync returns None when the host never replies within timeout`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let store = BridgeSecretStore(channel, TimeSpan.FromMilliseconds 100.0) :> ISecretStore

        let! result = store.TryReadAsync("unanswered", CancellationToken.None) |> withTimeout 45.0
        Assert.Equal(None, result)
    }

// ---------------------------------------------------------------------------
// TryReadAsync tests (Scenario 3: Error frame or null / non-string payload)
// ---------------------------------------------------------------------------

[<Fact>]
let ``TryReadAsync returns None on an error frame`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read = store.TryReadAsync("error-ref", CancellationToken.None)
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith None (Some { Code = "host/notFound"; Message = "Secret not found" }) channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(None, result)
    }

[<Fact>]
let ``TryReadAsync returns None when secret property is null or non-string`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let store = BridgeSecretStore(channel) :> ISecretStore

        let read = store.TryReadAsync("null-prop", CancellationToken.None)
        SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = (null: string) |})) None channel sent.[0].Id

        let! result = read |> withTimeout 45.0
        Assert.Equal(None, result)

        let sent2 = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel2 = HostChannel(fun f -> sent2.Add f; Task.CompletedTask)
        let store2 = BridgeSecretStore(channel2) :> ISecretStore

        let read2 = store2.TryReadAsync("number-prop", CancellationToken.None)
        SpinWait.SpinUntil((fun () -> sent2.Count = 1), 1000) |> ignore
        replyWith (Some(box {| secret = 12345 |})) None channel2 sent2.[0].Id

        let! result2 = read2 |> withTimeout 45.0
        Assert.Equal(None, result2)
    }

[<Fact>]
let ``TryReadAsync returns None when the HostChannel write throws synchronously or asynchronously`` () =
    task {
        let channel = HostChannel(fun _ -> failwith "simulated channel failure")
        let store = BridgeSecretStore(channel) :> ISecretStore

        let! result = store.TryReadAsync("failed-ref", CancellationToken.None)
        Assert.Equal(None, result)
    }
