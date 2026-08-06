module RestConnectorTests

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

/// A minimal in-process HTTP stub bound to loopback on an ephemeral
/// port — Section 10's "local HTTP stub", never a real external
/// endpoint. `handle` gets the raw request and writes the response
/// itself so each test can script exactly what the stub returns.
type private HttpStub(handle: HttpListenerContext -> Task) =
    let port =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let p = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        p

    let listener = new HttpListener()
    do listener.Prefixes.Add($"http://127.0.0.1:{port}/")
    do listener.Start()

    let acceptTask =
        task {
            let mutable keepGoing = true

            while keepGoing do
                try
                    let! context = listener.GetContextAsync()
                    do! handle context
                    context.Response.Close()
                with
                | :? ObjectDisposedException
                | :? HttpListenerException -> keepGoing <- false
        }

    member _.BaseUrl = $"http://127.0.0.1:{port}"

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()
            listener.Close()
            ignore acceptTask

let private writeJson (context: HttpListenerContext) (json: string) : Task =
    task {
        context.Response.StatusCode <- 200
        context.Response.ContentType <- "application/json"
        let bytes = Encoding.UTF8.GetBytes(json)
        do! context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
    }
    :> Task

[<Fact>]
let ``SendAsync sends the configured query params/headers and parses the JSON response`` () =
    task {
        use stub =
            new HttpStub(fun ctx ->
                task {
                    Assert.Equal("/items", ctx.Request.Url.AbsolutePath)
                    Assert.Equal("42", ctx.Request.QueryString.["id"])
                    Assert.Equal("test-token", ctx.Request.Headers.["X-Api-Key"])
                    do! writeJson ctx """{"name":"Sales"}"""
                })

        let options =
            { RestRequestOptions.Get($"{stub.BaseUrl}/items") with
                QueryParams = [ "id", "42" ]
                Headers = [ "X-Api-Key", "test-token" ] }

        let connector = RestConnector(options)
        let! result = connector.SendAsync options CancellationToken.None

        Assert.Equal("Sales", result.GetProperty("name").GetString())
    }

[<Fact>]
let ``SendAsync sends a POST body and the stub echoes what it received`` () =
    task {
        use stub =
            new HttpStub(fun ctx ->
                task {
                    Assert.Equal("POST", ctx.Request.HttpMethod)
                    use reader = new IO.StreamReader(ctx.Request.InputStream)
                    let! body = reader.ReadToEndAsync()
                    Assert.Equal("""{"query":"select 1"}""", body)
                    do! writeJson ctx """{"received":true}"""
                })

        let options =
            { RestRequestOptions.Get($"{stub.BaseUrl}/run") with
                Method = "POST"
                Body = Some """{"query":"select 1"}""" }

        let connector = RestConnector(options)
        let! result = connector.SendAsync options CancellationToken.None

        Assert.True(result.GetProperty("received").GetBoolean())
    }

[<Fact>]
let ``PaginateAsync follows next-page links until extractNextUrl returns None`` () =
    task {
        use stub =
            new HttpStub(fun ctx ->
                match ctx.Request.Url.AbsolutePath with
                | "/page1" -> writeJson ctx """{"items":["a"],"next":"/page2"}"""
                | "/page2" -> writeJson ctx """{"items":["b"],"next":null}"""
                | other -> failwith $"unexpected path {other}")

        let options = RestRequestOptions.Get($"{stub.BaseUrl}/page1")
        let connector = RestConnector(options)

        let extractNextUrl (page: Text.Json.JsonElement) =
            let next = page.GetProperty("next")

            if next.ValueKind = Text.Json.JsonValueKind.Null then
                None
            else
                Some($"{stub.BaseUrl}{next.GetString()}")

        let! pages = connector.PaginateAsync options extractNextUrl CancellationToken.None

        Assert.Equal(2, pages.Length)
        Assert.Equal("a", pages.[0].GetProperty("items").[0].GetString())
        Assert.Equal("b", pages.[1].GetProperty("items").[0].GetString())
    }

[<Fact>]
let ``TestConnectionAsync returns true when the endpoint responds successfully and false when unreachable`` () =
    task {
        use stub = new HttpStub(fun ctx -> writeJson ctx "{}")
        let reachable = RestConnector(RestRequestOptions.Get($"{stub.BaseUrl}/health")) :> IConnector

        let unreachable =
            RestConnector(RestRequestOptions.Get("http://127.0.0.1:1")) :> IConnector

        let! reachableResult = reachable.TestConnectionAsync(CancellationToken.None)
        let! unreachableResult = unreachable.TestConnectionAsync(CancellationToken.None)

        Assert.True(reachableResult)
        Assert.False(unreachableResult)
    }

[<Fact>]
let ``RestConnector reports its Kind as RestJson`` () =
    let connector = RestConnector(RestRequestOptions.Get("http://127.0.0.1/x")) :> IConnector

    Assert.Equal(ConnectorKind.RestJson, connector.Kind)
