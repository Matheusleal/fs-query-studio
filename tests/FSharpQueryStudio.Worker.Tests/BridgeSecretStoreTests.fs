module BridgeSecretStoreTests

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security
open FSharpQueryStudio.Worker

let private replyWith (result: obj option) (error: WorkerErrorInfo option) (channel: HostChannel) (id: string) =
    channel.TryComplete
        { Id = id
          Result = result |> Option.map (fun r -> JsonSerializer.SerializeToElement(r, Serialization.options))
          Error = error }
    |> ignore

[<Fact>]
let ``TryRead returns the host's secret`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = async { return store.TryRead "fqs-conn-1" } |> Async.StartAsTask
    // let the Request frame be written, then answer it
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith (Some(box {| secret = "hunter2" |})) None channel sent.[0].Id

    Assert.Equal(Some "hunter2", read.Result)

[<Fact>]
let ``TryRead returns None on an error frame`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = async { return store.TryRead "x" } |> Async.StartAsTask
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith None (Some { Code = "host/unknownMethod"; Message = "no" }) channel sent.[0].Id

    Assert.Equal(None, read.Result)

[<Fact>]
let ``TryRead returns None on a null secret`` () =
    let sent = System.Collections.Generic.List<WorkerRequestFrame>()
    let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
    let store = BridgeSecretStore(channel) :> ISecretStore

    let read = async { return store.TryRead "x" } |> Async.StartAsTask
    SpinWait.SpinUntil((fun () -> sent.Count = 1), 1000) |> ignore
    replyWith (Some(box {| secret = (null: string) |})) None channel sent.[0].Id

    Assert.Equal(None, read.Result)

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
