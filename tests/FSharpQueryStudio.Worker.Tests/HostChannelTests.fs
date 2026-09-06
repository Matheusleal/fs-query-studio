module HostChannelTests

open System.Collections.Generic
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

[<Fact>]
let ``Request writes one frame with a host- id and its method`` () =
    let sent = List<WorkerRequestFrame>()
    let channel = HostChannel(fun frame -> sent.Add frame; Task.CompletedTask)

    channel.Request("secret/get", {| reference = "fqs-conn-abc" |}) |> ignore

    Assert.Single sent |> ignore
    Assert.StartsWith("host-", sent.[0].Id)
    Assert.Equal("secret/get", sent.[0].Method)
    Assert.Equal("fqs-conn-abc", sent.[0].Params.GetProperty("reference").GetString())

[<Fact>]
let ``TryComplete resolves the matching pending Request and returns true`` () =
    let sent = List<WorkerRequestFrame>()
    let channel = HostChannel(fun frame -> sent.Add frame; Task.CompletedTask)

    let pending = channel.Request("secret/get", {| reference = "r" |})
    let reply: WorkerResponseFrame =
        { Id = sent.[0].Id
          Result = Some(System.Text.Json.JsonSerializer.SerializeToElement({| secret = "hunter2" |}, Serialization.options))
          Error = None }

    Assert.True(channel.TryComplete reply)
    Assert.True(pending.Wait 1000)
    Assert.Equal("hunter2", pending.Result.Result.Value.GetProperty("secret").GetString())

[<Fact>]
let ``TryComplete returns false for an unknown id`` () =
    let channel = HostChannel(fun _ -> Task.CompletedTask)
    let reply: WorkerResponseFrame = { Id = "host-nope"; Result = None; Error = None }
    Assert.False(channel.TryComplete reply)
