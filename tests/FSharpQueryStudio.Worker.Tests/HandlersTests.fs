module HandlersTests

open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Execution
open FSharpQueryStudio.Worker

let private emptyParamsRequest (id: string) (method_: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse("{}").RootElement }

let private evaluateRequest (id: string) (source: string) : WorkerRequestFrame =
    { Id = id
      Method = "execution/evaluate"
      Params = JsonSerializer.SerializeToElement<EvaluateParams>({ Source = source }, Serialization.options) }

[<Fact>]
let ``dispatch on worker/health returns a Result with no Error`` () =
    let request = emptyParamsRequest "h1" "worker/health"

    let response = Handlers.dispatch request

    Assert.Equal("h1", response.Id)
    Assert.True(response.Result.IsSome)
    Assert.True(response.Error.IsNone)

[<Fact>]
let ``dispatch on worker/health returns a non-empty version`` () =
    let request = emptyParamsRequest "h2" "worker/health"

    let response = Handlers.dispatch request
    let health = JsonSerializer.Deserialize<HealthInfo>(response.Result.Value, Serialization.options)

    Assert.False(System.String.IsNullOrWhiteSpace(health.Version))

[<Fact>]
let ``dispatch on an unknown method returns a structured error and no Result`` () =
    let request = emptyParamsRequest "u1" "not/a-real-method"

    let response = Handlers.dispatch request

    Assert.Equal("u1", response.Id)
    Assert.True(response.Result.IsNone)

    match response.Error with
    | Some err ->
        Assert.Equal("method/unknown", err.Code)
        Assert.Contains("not/a-real-method", err.Message)
    | None -> failwith "expected an Error"

[<Fact>]
let ``dispatch on execution/cancel requests cancellation and acknowledges`` () =
    CancellationSignal.reset ()

    try
        let response = Handlers.dispatch (emptyParamsRequest "c1" "execution/cancel")

        Assert.Equal("c1", response.Id)
        Assert.True(response.Result.IsSome)
        Assert.True(response.Error.IsNone)
        Assert.True(CancellationSignal.isCancelled ())
    finally
        CancellationSignal.reset ()

[<Fact>]
let ``handleEvaluate rejects a second concurrent evaluate with a structured busy error`` () =
    task {
        CancellationSignal.reset ()

        try
            let longRunningScript =
                "let mutable i = 0 in (while i < 2_000_000_000 && not (FSharpQueryStudio.Execution.CancellationSignal.isCancelled ()) do i <- i + 1); i"

            let firstTask =
                Task.Run(fun () -> Handlers.dispatch (evaluateRequest "e1" longRunningScript))

            do! Task.Delay(200)

            let secondResponse = Handlers.dispatch (evaluateRequest "e2" "1")

            Assert.True(secondResponse.Result.IsNone)

            match secondResponse.Error with
            | Some err -> Assert.Equal("execution/busy", err.Code)
            | None -> failwith "expected a busy error"

            CancellationSignal.requestCancel ()
            let! _ = firstTask
            ()
        finally
            CancellationSignal.reset ()
    }
