module HandlersTests

open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

let private emptyParamsRequest (id: string) (method_: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse("{}").RootElement }

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params = JsonSerializer.SerializeToElement<EvaluateParams>({ Source = source }, Serialization.options) }

let private analysisCheckRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/check"
      Params =
        JsonSerializer.SerializeToElement<AnalysisCheckParams>(
            { DocumentUri = "file:///workspace/sales.fsx"
              Version = 1
              Source = source },
            Serialization.options
        ) }

let private analysisHoverRequest (id: string) (source: string) (line: int) (column: int) : WorkerRequestFrame =
    { Id = id
      Method = "analysis/hover"
      Params =
        JsonSerializer.SerializeToElement<AnalysisHoverParams>(
            { DocumentUri = "file:///workspace/sales.fsx"
              Version = 1
              Source = source
              Line = line
              Column = column },
            Serialization.options
        ) }

[<Fact>]
let ``dispatch on worker/health returns a Result with no Error`` () =
    task {
        let request = emptyParamsRequest "h1" "worker/health"

        let! response = Handlers.dispatch request

        Assert.Equal("h1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
    }

[<Fact>]
let ``dispatch on worker/health returns a non-empty version`` () =
    task {
        let request = emptyParamsRequest "h2" "worker/health"

        let! response = Handlers.dispatch request
        let health = JsonSerializer.Deserialize<HealthInfo>(response.Result.Value, Serialization.options)

        Assert.False(System.String.IsNullOrWhiteSpace(health.Version))
    }

[<Fact>]
let ``dispatch on an unknown method returns a structured error and no Result`` () =
    task {
        let request = emptyParamsRequest "u1" "not/a-real-method"

        let! response = Handlers.dispatch request

        Assert.Equal("u1", response.Id)
        Assert.True(response.Result.IsNone)

        match response.Error with
        | Some err ->
            Assert.Equal("method/unknown", err.Code)
            Assert.Contains("not/a-real-method", err.Message)
        | None -> failwith "expected an Error"
    }

[<Fact>]
let ``dispatch on execution/cancel acknowledges even when no evaluation is running`` () =
    task {
        let! response = Handlers.dispatch (emptyParamsRequest "c1" "execution/cancel")

        Assert.Equal("c1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
    }

[<Fact>]
let ``handleEvaluate captures printed stdout into the envelope's Output lines`` () =
    let response = Handlers.handleEvaluate (evaluateRequest "p1" "printfn \"hello from fsi\"")

    Assert.True(response.Result.IsSome)
    let envelope = JsonSerializer.Deserialize<ResultEnvelope>(response.Result.Value, Serialization.options)

    Assert.Contains(envelope.Output, (fun (line: OutputLine) -> line.Text.Contains("hello from fsi")))

[<Fact>]
let ``handleEvaluate rejects a second concurrent evaluate with a structured busy error`` () =
    task {
        // A plain file check, not a call back into a host assembly - see
        // Execution.Tests/EvaluationTests.fs for why the earlier
        // static-field-plus-self-reference design was unreliable.
        let longRunningScript =
            "let mutable i = 0 in (while i < 2_000_000_000 && not (System.IO.File.Exists(\".cancel-requested\")) do i <- i + 1); i"

        // Reserve the slot synchronously (mirrors Program.fs's own loop)
        // rather than backgrounding the whole handleEvaluate call and
        // hoping a Task.Delay outlasts the threadpool picking it up - a
        // freshly started process's threadpool can take a while to inject
        // worker threads under load, which made this test flaky before.
        match Handlers.beginEvaluate (evaluateRequest "e1" longRunningScript) with
        | Choice2Of2 _ -> failwith "expected to reserve the evaluation slot"
        | Choice1Of2 workDir ->
            let firstTask =
                Task.Run(fun () -> Handlers.runEvaluate (evaluateRequest "e1" longRunningScript) workDir)

            let! secondResponse = Handlers.dispatch (evaluateRequest "e2" "1")

            Assert.True(secondResponse.Result.IsNone)

            match secondResponse.Error with
            | Some err -> Assert.Equal("execution/busy", err.Code)
            | None -> failwith "expected a busy error"

            let! _ = Handlers.dispatch (emptyParamsRequest "c2" "execution/cancel")
            ()

            let! _ = firstTask
            ()
    }

[<Fact>]
let ``dispatch on analysis/check returns at least one diagnostic for a script with a type error`` () =
    task {
        let! response = Handlers.handleAnalysisCheck (analysisCheckRequest "a1" "let x : int = \"not an int\"")

        Assert.True(response.Result.IsSome)
        let result = JsonSerializer.Deserialize<AnalysisCheckResult>(response.Result.Value, Serialization.options)

        Assert.NotEmpty(result.Diagnostics)
    }

[<Fact>]
let ``dispatch on analysis/hover returns the display name and type for a local binding`` () =
    task {
        // "total" ends at column 9 (0-based) on line 1: "let total = 42".
        let! response = Handlers.handleAnalysisHover (analysisHoverRequest "h1" "let total = 42" 1 9)

        Assert.True(response.Result.IsSome)
        let result = JsonSerializer.Deserialize<AnalysisHoverResult>(response.Result.Value, Serialization.options)

        match result.Hover with
        | None -> failwith "expected hover info"
        | Some hover ->
            Assert.Equal("total", hover.DisplayName)
            Assert.Equal("int", hover.Type)
    }
