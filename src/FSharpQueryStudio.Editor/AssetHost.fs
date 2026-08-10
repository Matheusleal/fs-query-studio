namespace FSharpQueryStudio.Editor

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Serilog

/// Serves Monaco's local assets over loopback-only HTTP — see Section 6.
/// Binds `127.0.0.1` on an OS-assigned ephemeral port ("127.0.0.1" is the
/// only host literal used anywhere in this module — there is no code path
/// that could bind all interfaces), serves only files under `assetsRoot`,
/// rejects path traversal, 404s unknown assets, and applies a restrictive
/// Content-Security-Policy to every response.
module AssetHost =

    let private findEphemeralPort () =
        let listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    let private contentType (path: string) =
        match Path.GetExtension(path).ToLowerInvariant() with
        | ".html" -> "text/html; charset=utf-8"
        | ".js" -> "text/javascript; charset=utf-8"
        | ".css" -> "text/css; charset=utf-8"
        | ".json" -> "application/json; charset=utf-8"
        | ".map" -> "application/json; charset=utf-8"
        | ".woff" -> "font/woff"
        | ".woff2" -> "font/woff2"
        | ".ttf" -> "font/ttf"
        | ".svg" -> "image/svg+xml"
        | ".wasm" -> "application/wasm"
        | _ -> "application/octet-stream"

    /// `None` if the decoded request path resolves outside `assetsRoot`
    /// (rejects path traversal, including encoded "..") or the file
    /// doesn't exist.
    let private resolveAsset (assetsRoot: string) (requestPath: string) : string option =
        let rootFull = Path.GetFullPath(assetsRoot)
        let relative = requestPath.TrimStart('/')
        let candidateFull = Path.GetFullPath(Path.Combine(rootFull, relative))

        let withinRoot =
            candidateFull.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
            || candidateFull.StartsWith(
                rootFull + string Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )

        if withinRoot && File.Exists(candidateFull) then
            Some candidateFull
        else
            None

    let private handleRequest (assetsRoot: string) (context: HttpListenerContext) : Task =
        task {
            context.Response.Headers.Add(
                "Content-Security-Policy",
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'"
            )

            match resolveAsset assetsRoot context.Request.Url.AbsolutePath with
            | None -> context.Response.StatusCode <- int HttpStatusCode.NotFound
            | Some fullPath ->
                context.Response.StatusCode <- int HttpStatusCode.OK
                context.Response.ContentType <- contentType fullPath
                use fileStream = File.OpenRead(fullPath)
                do! fileStream.CopyToAsync(context.Response.OutputStream)

            context.Response.Close()
        }
        :> Task

    /// `GetContextAsync` throwing anything other than these two is
    /// unexpected — both are the normal "the listener was stopped/
    /// disposed" shutdown signal, everything else is a real failure that
    /// silently kills asset serving (and with it, Monaco) for the rest of
    /// the app's lifetime unless it's logged here. There was previously no
    /// way to tell "the listener died" from "someone tested the wrong
    /// port" after the fact.
    let private acceptLoop (listener: HttpListener) (assetsRoot: string) : Task =
        task {
            let mutable keepGoing = true

            while keepGoing do
                try
                    let! context = listener.GetContextAsync()
                    Task.Run(fun () -> handleRequest assetsRoot context) |> ignore
                with
                | :? ObjectDisposedException
                | :? HttpListenerException -> keepGoing <- false
                | ex ->
                    Log.Warning(
                        ex,
                        "Asset host's accept loop stopped unexpectedly; Monaco/editor assets are no longer reachable"
                    )

                    keepGoing <- false
        }
        :> Task

    type Host(listener: HttpListener, assetsRoot: string, baseUri: Uri) =
        let acceptTask = acceptLoop listener assetsRoot

        member _.BaseUri = baseUri

        interface IDisposable with
            member _.Dispose() =
                listener.Stop()
                listener.Close()
                ignore acceptTask

    /// Logs the real `BaseUri` on every start — previously nothing did,
    /// so there was no way to confirm which port a running instance
    /// actually bound to (it's a fresh OS-assigned ephemeral port every
    /// launch) versus a stale one from an earlier run or a different
    /// process.
    let start (assetsRoot: string) : Host =
        let port = findEphemeralPort ()
        let baseUri = Uri($"http://127.0.0.1:{port}/")
        let listener = new HttpListener()
        listener.Prefixes.Add(baseUri.ToString())
        listener.Start()
        Log.Information("Asset host listening at {BaseUri}", baseUri)
        new Host(listener, assetsRoot, baseUri)
