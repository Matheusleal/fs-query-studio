module ReverseChannelLoopTests

open System.IO
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

/// Feeds the loop two lines: a host-bound response frame (must be routed to
/// the channel, never dispatched) followed by EOF.
[<Fact>]
let ``loop routes a method-less frame to HostChannel.TryComplete and does not dispatch it`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let pending = channel.Request("secret/get", {| reference = "r" |})
        let hostId = sent.[0].Id

        let responseLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerResponseFrame>(
                { Id = hostId
                  Result = Some(System.Text.Json.JsonSerializer.SerializeToElement({| secret = "ok" |}, Serialization.options))
                  Error = None },
                Serialization.options)

        use stdin = new StringReader(responseLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        Assert.True(pending.Wait 1000)
        Assert.Equal("ok", pending.Result.Result.Value.GetProperty("secret").GetString())
        Assert.Equal("", stdout.ToString())   // nothing written back for a routed response
    }

[<Fact>]
let ``loop still answers a normal request frame`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let healthLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
                { Id = "h1"; Method = "worker/health"; Params = System.Text.Json.JsonDocument.Parse("{}").RootElement },
                Serialization.options)

        use stdin = new StringReader(healthLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        let written = stdout.ToString().Trim()
        Assert.Contains("\"id\":\"h1\"", written)
        Assert.Contains("version", written)
    }
