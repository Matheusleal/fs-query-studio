namespace FSharpQueryStudio.Connectors

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module RestConnectorExtensions =
    type ConnectorKind with
        static member RestJson = ConnectorKind("RestJson")

    type ConnectorCategory with
        static member Rest = ConnectorCategory("rest", "Json", "FSharpQueryStudio.Connectors.Connections.RestCategory")

/// URL/method/header/query/body configuration — Section 10's REST/JSON
/// connector capability shape.
type RestRequestOptions =
    { Url: string
      Method: string
      Headers: (string * string) list
      QueryParams: (string * string) list
      Body: string option }

    static member Get(url: string) : RestRequestOptions =
        { Url = url
          Method = "GET"
          Headers = []
          QueryParams = []
          Body = None }

module private RestRequests =
    let buildUri (options: RestRequestOptions) : Uri =
        let builder = UriBuilder(options.Url)

        if not options.QueryParams.IsEmpty then
            let existing =
                if String.IsNullOrEmpty(builder.Query) then
                    []
                else
                    builder.Query.TrimStart('?').Split('&') |> Array.filter (fun s -> s <> "") |> Array.toList

            let extra =
                options.QueryParams
                |> List.map (fun (k, v) -> $"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}")

            builder.Query <- String.Join("&", existing @ extra)

        builder.Uri

    let buildMessage (options: RestRequestOptions) : HttpRequestMessage =
        let request = new HttpRequestMessage(HttpMethod(options.Method), buildUri options)

        for name, value in options.Headers do
            request.Headers.TryAddWithoutValidation(name, value) |> ignore

        match options.Body with
        | Some body -> request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        | None -> ()

        request

module private RestConnectorDefaults =
    let sharedHttpClient = new HttpClient()

/// REST/JSON connector — Section 10. `baseOptions` is the endpoint used
/// for `IConnector.TestConnectionAsync`; every other call configures its
/// own request explicitly.
type RestConnector(baseOptions: RestRequestOptions, ?httpClient: HttpClient) =
    let httpClient = defaultArg httpClient RestConnectorDefaults.sharedHttpClient
    member _.BaseOptions = baseOptions

    /// Sends one configured request and parses the response body as
    /// JSON — Section 10's configuration plus JSON-parsing requirement.
    member _.SendAsync (options: RestRequestOptions) (ct: CancellationToken) : Task<JsonElement> =
        task {
            use request = RestRequests.buildMessage options
            use! response = httpClient.SendAsync(request, ct)
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync(ct)
            use doc = JsonDocument.Parse(body)
            return doc.RootElement.Clone()
        }

    /// Follows `extractNextUrl` across responses starting from `options`,
    /// collecting every page's parsed body until it returns `None` —
    /// Section 10's "pagination helpers where configured". What counts
    /// as "next" (a `next` link, a cursor field, ...) isn't standardized
    /// across REST APIs, so the caller supplies that reading.
    member this.PaginateAsync
        (options: RestRequestOptions)
        (extractNextUrl: JsonElement -> string option)
        (ct: CancellationToken)
        : Task<JsonElement list> =
        task {
            let pages = ResizeArray<JsonElement>()
            let mutable current = Some options

            while current.IsSome do
                let opts = current.Value
                let! page = this.SendAsync opts ct
                pages.Add(page)

                current <-
                    extractNextUrl page
                    |> Option.map (fun nextUrl -> { opts with Url = nextUrl; QueryParams = [] })

            return pages |> List.ofSeq
        }

    interface IConnector with
        member _.Kind = ConnectorKind.RestJson

        member _.TestConnectionAsync(ct: CancellationToken) : Task<bool> =
            task {
                try
                    use request = RestRequests.buildMessage baseOptions
                    use! response = httpClient.SendAsync(request, ct)
                    return response.IsSuccessStatusCode
                with _ ->
                    return false
            }

        /// A generic REST/JSON endpoint has no discoverable relational
        /// schema — unlike the database/file connectors, there is no
        /// universal way to enumerate "tables" from an arbitrary API.
        member _.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> = Task.FromResult []
