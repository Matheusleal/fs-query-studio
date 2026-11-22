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
                with _ ->
                    keepGoing <- false
        }

    member _.BaseUrl = $"http://127.0.0.1:{port}"

    interface IDisposable with
        member _.Dispose() =
            try
                listener.Stop()
            with _ ->
                ()

            try
                listener.Close()
            with _ ->
                ()

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
                Headers = [ "X-Api-Key", "test-token" ]
            }

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
                Body = Some """{"query":"select 1"}"""
            }

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

        let reachable =
            RestConnector(RestRequestOptions.Get($"{stub.BaseUrl}/health")) :> IConnector

        let unreachable =
            RestConnector(RestRequestOptions.Get("http://127.0.0.1:1")) :> IConnector

        let! reachableResult = reachable.TestConnectionAsync(CancellationToken.None)
        let! unreachableResult = unreachable.TestConnectionAsync(CancellationToken.None)

        Assert.True(reachableResult)
        Assert.False(unreachableResult)
    }

[<Fact>]
let ``RestConnector reports its Kind as RestJson`` () =
    let connector =
        RestConnector(RestRequestOptions.Get("http://127.0.0.1/x")) :> IConnector

    Assert.Equal(ConnectorKind.RestJson, connector.Kind)

[<Fact>]
let ``RestDescriptor and RestConnectorFactory coverage`` () =
    let descriptor = RestDescriptor.Instance :> IConnectorDescriptor
    Assert.Equal("RestJson", descriptor.KindId)
    Assert.Equal("REST (JSON)", descriptor.DisplayName)
    Assert.Equal("cloud", descriptor.Icon)
    Assert.Equal(ConnectorCategory.Rest, descriptor.Category)
    Assert.Equal(1, descriptor.FormFields.Length)
    Assert.True(descriptor.EntityGenerator.IsNone)

    let validJson =
        System.Text.Json.JsonDocument.Parse("""{"url": "https://api.test.com"}""").RootElement

    Assert.True(descriptor.ValidateConfiguration(validJson).IsOk)
    let invalidJson = System.Text.Json.JsonDocument.Parse("""{}""").RootElement
    Assert.True(descriptor.ValidateConfiguration(invalidJson).IsError)

    let factory = descriptor.Factory

    let dummyStore =
        { new FSharpQueryStudio.Security.ISecretStore with
            member _.TryRead(_) = None

            member _.TryReadAsync(_, _) =
                System.Threading.Tasks.Task.FromResult None

            member _.Save _ _ = ()
            member _.Delete _ = ()
        }

    // String config
    let strDoc = System.Text.Json.JsonDocument.Parse("\"https://api.test.com/items\"")
    let conn1 = factory.Create(strDoc.RootElement, dummyStore, None) :?> RestConnector
    Assert.Equal("https://api.test.com/items", conn1.BaseOptions.Url)

    // Object config with url
    let objDoc1 =
        System.Text.Json.JsonDocument.Parse("""{"url": "https://api.test.com/v2"}""")

    let conn2 = factory.Create(objDoc1.RootElement, dummyStore, None) :?> RestConnector
    Assert.Equal("https://api.test.com/v2", conn2.BaseOptions.Url)

    // Object config without url
    let objDoc2 = System.Text.Json.JsonDocument.Parse("""{"other": 123}""")
    let conn3 = factory.Create(objDoc2.RootElement, dummyStore, None) :?> RestConnector
    Assert.NotNull(conn3)

type private ThrowingHttpMessageHandler() =
    inherit System.Net.Http.HttpMessageHandler()
    override _.SendAsync(_, _) =
        Task.FromException<System.Net.Http.HttpResponseMessage>(System.Net.Http.HttpRequestException("Simulated network failure"))

type private StatusCodeHttpMessageHandler(code: HttpStatusCode) =
    inherit System.Net.Http.HttpMessageHandler()
    override _.SendAsync(_, _) =
        let response = new System.Net.Http.HttpResponseMessage(code)
        Task.FromResult response

type private RecordingHttpMessageHandler(body: string) =
    inherit System.Net.Http.HttpMessageHandler()
    let mutable lastUri: Uri option = None
    member _.LastUri = lastUri
    override _.SendAsync(req, _) =
        lastUri <- Some req.RequestUri
        let response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK)
        response.Content <- new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json")
        Task.FromResult response

[<Fact>]
let ``RestRequests buildUri merges with existing query params in url scenario 1`` () =
    task {
        let opt = { RestRequestOptions.Get("http://example.com/api?page=1") with QueryParams = [ ("limit", "20") ] }
        let handler = new RecordingHttpMessageHandler("{}")
        use client = new System.Net.Http.HttpClient(handler)
        let conn = RestConnector(opt, client)
        let! _ = conn.SendAsync opt CancellationToken.None
        Assert.Equal("http://example.com/api?page=1&limit=20", handler.LastUri.Value.ToString())
    }

[<Fact>]
let ``RestRequests buildUri merges with existing query params in url scenario 2`` () =
    task {
        let opt = { RestRequestOptions.Get("http://example.com/api?a=1&b=2") with QueryParams = [ ("c", "3"); ("d", "4") ] }
        let handler = new RecordingHttpMessageHandler("{}")
        use client = new System.Net.Http.HttpClient(handler)
        let conn = RestConnector(opt, client)
        let! _ = conn.SendAsync opt CancellationToken.None
        Assert.Equal("http://example.com/api?a=1&b=2&c=3&d=4", handler.LastUri.Value.ToString())
    }

[<Fact>]
let ``RestConnector SendAsync falls back to sample github json on network error scenario 1`` () =
    task {
        let opt = RestRequestOptions.Get("https://api.github.com/orgs/fsprojects/repos")
        use client = new System.Net.Http.HttpClient(new ThrowingHttpMessageHandler())
        let conn = RestConnector(opt, client)
        let! json = conn.SendAsync opt CancellationToken.None
        Assert.True(json.ValueKind = System.Text.Json.JsonValueKind.Array)
        Assert.True(json.GetArrayLength() > 0)
    }

[<Fact>]
let ``RestConnector SendAsync falls back to sample github json on 500 error scenario 2`` () =
    task {
        let opt = RestRequestOptions.Get("https://api.github.com/orgs/fsprojects/repos?page=1")
        use client = new System.Net.Http.HttpClient(new StatusCodeHttpMessageHandler(HttpStatusCode.InternalServerError))
        let conn = RestConnector(opt, client)
        let! json = conn.SendAsync opt CancellationToken.None
        Assert.True(json.ValueKind = System.Text.Json.JsonValueKind.Array)
        Assert.True(json.GetArrayLength() > 0)
    }

[<Fact>]
let ``RestConnector DiscoverSchemaAsync returns empty list scenario 1`` () =
    task {
        let opt = RestRequestOptions.Get("http://example.com/api")
        let conn = RestConnector(opt) :> IConnector
        let! tables = conn.DiscoverSchemaAsync(CancellationToken.None)
        Assert.Empty(tables)
    }

[<Fact>]
let ``RestConnector DiscoverSchemaAsync returns empty list scenario 2`` () =
    task {
        let opt = RestRequestOptions.Get("http://example.com/v2/items")
        let conn = RestConnector(opt) :> IConnector
        use cts = new CancellationTokenSource()
        let! tables = conn.DiscoverSchemaAsync(cts.Token)
        Assert.Empty(tables)
    }

[<Fact>]
let ``RestEntityGenerator emits code with JSON properties scenario 1`` () =
    let json = """[ { "id": 1, "title": "First Item", "price": 49.90, "inStock": true } ]"""
    let handler = new RecordingHttpMessageHandler(json)
    use client = new System.Net.Http.HttpClient(handler)
    let opt = RestRequestOptions.Get("http://example.com/items")
    let conn = RestConnector(opt, client)

    let gen = RestEntityGenerator() :> IConnectorEntityGenerator
    Assert.Equal(ConnectorCategory.Rest, gen.Category)

    let code = gen.EmitModuleCode("ItemsApi", conn, "ItemsApiModule")
    Assert.Contains("module ItemsApiModule =", code)
    Assert.Contains("type Item =", code)
    Assert.Contains("id: int", code)
    Assert.Contains("title: string", code)
    Assert.Contains("price: decimal", code)
    Assert.Contains("inStock: bool", code)
    Assert.Contains("type Data(conn: FSharpQueryStudio.Connectors.Connections.RestConnection) =", code)
    Assert.Contains("let items () : System.Linq.IQueryable<Item> = (Data()).Items()", code)
    Assert.Contains("let rows () : System.Linq.IQueryable<Item> = (Data()).Rows", code)

    let extCode = gen.EmitExtensionCode("ItemsApi", "ItemsApiModule", "items_api")
    Assert.Contains("member _.items_api : ItemsApiModule.Data = ItemsApiModule.Data(\"ItemsApi\")", extCode)

[<Fact>]
let ``RestEntityGenerator emits code with fallback Value property when empty array scenario 2`` () =
    let handler = new RecordingHttpMessageHandler("[]")
    use client = new System.Net.Http.HttpClient(handler)
    let opt = RestRequestOptions.Get("http://example.com/empty")
    let conn = RestConnector(opt, client)

    let gen = RestEntityGenerator() :> IConnectorEntityGenerator
    let code = gen.EmitModuleCode("EmptyApi", conn, "EmptyApiModule")
    Assert.Contains("module EmptyApiModule =", code)
    Assert.Contains("Value: string", code)

    let dummyConn = SqliteConnector("Data Source=:memory:") :> IConnector
    let nonMatchingCode = gen.EmitModuleCode("SqliteConn", dummyConn, "SqliteModule")
    Assert.Equal("", nonMatchingCode)

[<Fact>]
let ``CustomDescriptorWithGenerator forwards all members scenario 1`` () =
    let baseDesc = RestDescriptor.Instance :> IConnectorDescriptor
    let gen = RestEntityGenerator() :> IConnectorEntityGenerator
    let custom = CustomDescriptorWithGenerator(baseDesc, gen) :> IConnectorDescriptor

    Assert.Equal(baseDesc.KindId, custom.KindId)
    Assert.Equal(baseDesc.DisplayName, custom.DisplayName)
    Assert.Equal(baseDesc.Category, custom.Category)
    Assert.Equal(baseDesc.Icon, custom.Icon)
    Assert.Equal<ConnectorFormField list>(baseDesc.FormFields, custom.FormFields)
    Assert.True(custom.EntityGenerator.IsSome)

[<Fact>]
let ``CustomDescriptorWithGenerator validates configuration and creates factory scenario 2`` () =
    let baseDesc = RestDescriptor.Instance :> IConnectorDescriptor
    let gen = RestEntityGenerator() :> IConnectorEntityGenerator
    let custom = CustomDescriptorWithGenerator(baseDesc, gen) :> IConnectorDescriptor

    use doc = System.Text.Json.JsonDocument.Parse("""{"url": "http://example.com/api"}""")
    let valRes = custom.ValidateConfiguration doc.RootElement
    Assert.True(Result.isOk valRes)
    Assert.NotNull(custom.Factory)
