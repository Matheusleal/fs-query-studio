module HandlersTests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Worker

let private emptyParamsRequest (id: string) (method_: string) : WorkerRequestFrame =
    { Id = id
      Method = method_
      Params = JsonDocument.Parse("{}").RootElement }

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
