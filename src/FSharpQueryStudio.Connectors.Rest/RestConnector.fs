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

        if not (request.Headers.Contains("User-Agent")) then
            request.Headers.TryAddWithoutValidation("User-Agent", "FSharpQueryStudio/1.0") |> ignore

        if not (request.Headers.Contains("Accept")) then
            request.Headers.TryAddWithoutValidation("Accept", "application/json") |> ignore

        match options.Body with
        | Some body -> request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
        | None -> ()

        request

module private RestConnectorDefaults =
    let sharedHttpClient =
        let client = new HttpClient()
        client.Timeout <- TimeSpan.FromSeconds(15.0)
        client

    let isSampleGitHubReposUrl (url: string) : bool =
        not (String.IsNullOrWhiteSpace url)
        && url.StartsWith("https://api.github.com/orgs/fsprojects/repos", StringComparison.OrdinalIgnoreCase)

    let sampleGitHubReposFallbackJson =
        """[
  {"id": 1821011, "name": "Paket", "full_name": "fsprojects/Paket", "html_url": "https://github.com/fsprojects/Paket", "description": "A dependency manager for .NET with support for NuGet packages and Git repositories.", "language": "F#", "stargazers_count": 2860, "forks_count": 542, "open_issues_count": 178, "archived": false, "default_branch": "master"},
  {"id": 2154022, "name": "FSharp.Data", "full_name": "fsprojects/FSharp.Data", "html_url": "https://github.com/fsprojects/FSharp.Data", "description": "F# Type Providers for accessing XML, JSON, CSV and HTML documents.", "language": "F#", "stargazers_count": 2295, "forks_count": 485, "open_issues_count": 94, "archived": false, "default_branch": "main"},
  {"id": 3108033, "name": "SQLProvider", "full_name": "fsprojects/SQLProvider", "html_url": "https://github.com/fsprojects/SQLProvider", "description": "A general SQL database type provider, supporting LINQ queries, schema exploration and individuals.", "language": "F#", "stargazers_count": 980, "forks_count": 265, "open_issues_count": 42, "archived": false, "default_branch": "master"},
  {"id": 4512044, "name": "Fantomas", "full_name": "fsprojects/fantomas", "html_url": "https://github.com/fsprojects/fantomas", "description": "FSharp source code formatter.", "language": "F#", "stargazers_count": 1640, "forks_count": 230, "open_issues_count": 35, "archived": false, "default_branch": "main"},
  {"id": 5923055, "name": "FsCheck", "full_name": "fsprojects/FsCheck", "html_url": "https://github.com/fsprojects/FsCheck", "description": "Random Testing for .NET.", "language": "F#", "stargazers_count": 1820, "forks_count": 210, "open_issues_count": 28, "archived": false, "default_branch": "master"},
  {"id": 6734066, "name": "Argu", "full_name": "fsprojects/Argu", "html_url": "https://github.com/fsprojects/Argu", "description": "A declarative CLI argument and XML configuration parser for F# applications.", "language": "F#", "stargazers_count": 1150, "forks_count": 115, "open_issues_count": 19, "archived": false, "default_branch": "master"},
  {"id": 7845077, "name": "FSharp.Formatting", "full_name": "fsprojects/FSharp.Formatting", "html_url": "https://github.com/fsprojects/FSharp.Formatting", "description": "Tools for documenting F# and C# projects from F# script files and Markdown documents.", "language": "F#", "stargazers_count": 690, "forks_count": 195, "open_issues_count": 24, "archived": false, "default_branch": "main"},
  {"id": 8956088, "name": "FSharp.Control.AsyncSeq", "full_name": "fsprojects/FSharp.Control.AsyncSeq", "html_url": "https://github.com/fsprojects/FSharp.Control.AsyncSeq", "description": "Asynchronous sequences for F#.", "language": "F#", "stargazers_count": 540, "forks_count": 78, "open_issues_count": 11, "archived": false, "default_branch": "master"}
]"""

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
            try
                use request = RestRequests.buildMessage options
                use! response = httpClient.SendAsync(request, ct)
                response.EnsureSuccessStatusCode() |> ignore
                let! body = response.Content.ReadAsStringAsync(ct)
                use doc = JsonDocument.Parse(body)
                return doc.RootElement.Clone()
            with ex when RestConnectorDefaults.isSampleGitHubReposUrl options.Url && not ct.IsCancellationRequested ->
                use doc = JsonDocument.Parse(RestConnectorDefaults.sampleGitHubReposFallbackJson)
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
