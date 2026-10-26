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

/// Critical 1 (worker side): the REAL extension reply shape is
/// `{"id":…,"result":…}` with NO `error` key. FSharp.SystemTextJson does not
/// default a missing `option` field to `None`, so a naive
/// `Deserialize<WorkerResponseFrame>` on this line throws and (unguarded)
/// exits the worker. The loop must both survive it and still complete the
/// pending `HostChannel.Request`.
[<Fact>]
let ``loop routes a reply that has no error key and completes the pending request`` () =
    task {
        let sent = System.Collections.Generic.List<WorkerRequestFrame>()
        let channel = HostChannel(fun f -> sent.Add f; Task.CompletedTask)
        let pending = channel.Request("secret/get", {| reference = "r" |})
        let hostId = sent.[0].Id

        // Hand-built: no "error" key at all, exactly what client.ts emits.
        let responseLine = sprintf """{"id":"%s","result":{"secret":"s"}}""" hostId

        use stdin = new StringReader(responseLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        Assert.True(pending.Wait 1000)
        Assert.Equal("s", pending.Result.Result.Value.GetProperty("secret").GetString())
        Assert.Equal("", stdout.ToString())
    }

/// Critical 1 (worker side): non-JSON garbage on stdin must not terminate
/// the loop — it is logged and skipped, and a following valid request is
/// still answered.
[<Fact>]
let ``loop survives a non-JSON garbage line and still answers a following request`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let healthLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
                { Id = "h9"; Method = "worker/health"; Params = System.Text.Json.JsonDocument.Parse("{}").RootElement },
                Serialization.options)

        use stdin = new StringReader("not json at all\n" + healthLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        let written = stdout.ToString().Trim()
        Assert.Contains("\"id\":\"h9\"", written)
        Assert.Contains("version", written)
    }
