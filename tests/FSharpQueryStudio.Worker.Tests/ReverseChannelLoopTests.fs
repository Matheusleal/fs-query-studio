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

        let channel =
            HostChannel(fun f ->
                sent.Add f
                Task.CompletedTask)

        let pending = channel.Request("secret/get", {| reference = "r" |})
        let hostId = sent.[0].Id

        let responseLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerResponseFrame>(
                {
                    Id = hostId
                    Result =
                        Some(
                            System.Text.Json.JsonSerializer.SerializeToElement(
                                {| secret = "ok" |},
                                Serialization.options
                            )
                        )
                    Error = None
                },
                Serialization.options
            )

        use stdin = new StringReader(responseLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        Assert.True(pending.Wait 1000)
        Assert.Equal("ok", pending.Result.Result.Value.GetProperty("secret").GetString())
        Assert.Equal("", stdout.ToString()) // nothing written back for a routed response
    }

[<Fact>]
let ``loop still answers a normal request frame`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)

        let healthLine =
            System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
                {
                    Id = "h1"
                    Method = "worker/health"
                    Params = System.Text.Json.JsonDocument.Parse("{}").RootElement
                },
                Serialization.options
            )

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

        let channel =
            HostChannel(fun f ->
                sent.Add f
                Task.CompletedTask)

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
                {
                    Id = "h9"
                    Method = "worker/health"
                    Params = System.Text.Json.JsonDocument.Parse("{}").RootElement
                },
                Serialization.options
            )

        use stdin = new StringReader("not json at all\n" + healthLine + "\n")
        use stdout = new StringWriter()

        do! Program.loop stdin stdout channel

        let written = stdout.ToString().Trim()
        Assert.Contains("\"id\":\"h9\"", written)
        Assert.Contains("version", written)
    }

let private healthLine (id: string) =
    System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
        {
            Id = id
            Method = "worker/health"
            Params = System.Text.Json.JsonDocument.Parse("{}").RootElement
        },
        Serialization.options
    )

let private waitForOutput (stdout: StringWriter) (needle: string) =
    task {
        let mutable attempts = 0

        while not (stdout.ToString().Contains needle) && attempts < 200 do
            do! Task.Delay 50
            attempts <- attempts + 1

        return stdout.ToString()
    }

[<Fact>]
let ``loop skips non-object lines and over-long garbage, then answers a valid request`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let longGarbage = String.replicate 600 "x"

        use stdin =
            new StringReader("[1,2,3]\n" + longGarbage + "\n" + healthLine "h-skip" + "\n")

        use stdout = new StringWriter()
        do! Program.loop stdin stdout channel
        Assert.Contains("\"id\":\"h-skip\"", stdout.ToString())
    }

[<Fact>]
let ``loop tolerates reverse-channel replies with odd id and error shapes`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)

        let lines =
            [
                """{"id":123,"error":{"code":"err","message":"msg"}}"""
                """{"id":"unknown-id","error":"malformed-not-an-object"}"""
                """{"id":"unknown-id-2","result":{"a":1},"error":null}"""
                healthLine "h-odd"
            ]

        use stdin = new StringReader(String.concat "\n" lines + "\n")
        use stdout = new StringWriter()
        do! Program.loop stdin stdout channel
        Assert.Contains("\"id\":\"h-odd\"", stdout.ToString())
    }

[<Fact>]
let ``loop skips a request frame that has a method but is malformed`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let bad = """{"method":"worker/health","id":12345,"params":"bad"}"""
        use stdin = new StringReader(bad + "\n" + healthLine "h-after-bad" + "\n")
        use stdout = new StringWriter()
        do! Program.loop stdin stdout channel
        Assert.Contains("\"id\":\"h-after-bad\"", stdout.ToString())
    }

[<Fact>]
let ``loop dispatches results/exportCsv on a background task and replies`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)

        let line =
            """{"id":"bg-1","method":"results/exportCsv","params":{"documentUri":"file:///none-bg.fsx","filePath":"out.csv"}}"""

        use stdin = new StringReader(line + "\n")
        use stdout = new StringWriter()
        do! Program.loop stdin stdout channel
        let! output = waitForOutput stdout "bg-1"
        Assert.Contains("\"id\":\"bg-1\"", output)
        Assert.Contains("results/nothingToExport", output)
    }

[<Fact>]
let ``loop turns an exception in a background dispatch into a worker/requestFailed response`` () =
    task {
        let channel = HostChannel(fun _ -> Task.CompletedTask)
        let line = """{"id":"bg-err","method":"results/exportCsv","params":123}"""
        use stdin = new StringReader(line + "\n")
        use stdout = new StringWriter()
        do! Program.loop stdin stdout channel
        let! output = waitForOutput stdout "bg-err"
        Assert.Contains("worker/requestFailed", output)
    }

[<Fact>]
let ``loop answers execution/evaluate with a busy error while the slot is reserved`` () =
    task {
        let reserveReq: WorkerRequestFrame =
            {
                Id = "reserve"
                Method = "execution/evaluate"
                Params = System.Text.Json.JsonDocument.Parse("123").RootElement
            }

        match Handlers.beginEvaluate reserveReq with
        | Choice2Of2 _ -> failwith "expected to reserve the evaluation slot"
        | Choice1Of2 workDir ->
            try
                let channel = HostChannel(fun _ -> Task.CompletedTask)

                let line =
                    System.Text.Json.JsonSerializer.Serialize<WorkerRequestFrame>(
                        { reserveReq with Id = "eval-busy" },
                        Serialization.options
                    )

                use stdin = new StringReader(line + "\n")
                use stdout = new StringWriter()
                do! Program.loop stdin stdout channel
                Assert.Contains("execution/busy", stdout.ToString())
            finally
                try
                    Handlers.runEvaluate reserveReq workDir |> ignore
                with _ ->
                    ()
    }
