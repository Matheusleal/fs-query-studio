module Tests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Editor

let private withTimeout (seconds: float) (work: Task<'T>) : Task<'T> =
    task {
        let! winner = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds seconds))

        if winner = (work :> Task) then
            return! work
        else
            return failwith $"timed out after {seconds}s"
    }

/// Returns (assetsRoot, outsideDir) — outsideDir holds a "secret.txt" that
/// must never be reachable through assetsRoot, no matter what a request
/// path says.
let private setupAssets () =
    let root =
        Path.Combine(Path.GetTempPath(), "fqs-assets-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    File.WriteAllText(Path.Combine(root, "editor-host.html"), "<html>hi</html>")

    let outsideDir =
        Path.Combine(Path.GetTempPath(), "fqs-outside-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(outsideDir) |> ignore
    File.WriteAllText(Path.Combine(outsideDir, "secret.txt"), "top secret")

    root, outsideDir

[<Fact>]
let ``serves a known asset with 200 and its exact content`` () =
    task {
        let assetsRoot, _ = setupAssets ()
        use host = AssetHost.start assetsRoot
        use client = new HttpClient()

        let! response = client.GetAsync(Uri(host.BaseUri, "editor-host.html")) |> withTimeout 10.0

        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! body = response.Content.ReadAsStringAsync()
        Assert.Equal("<html>hi</html>", body)
    }

[<Fact>]
let ``an unknown asset 404s`` () =
    task {
        let assetsRoot, _ = setupAssets ()
        use host = AssetHost.start assetsRoot
        use client = new HttpClient()

        let! response = client.GetAsync(Uri(host.BaseUri, "does-not-exist.js")) |> withTimeout 10.0

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
    }

[<Fact>]
let ``a path-traversal request never reaches the file outside the assets root`` () =
    task {
        let assetsRoot, outsideDir = setupAssets ()
        use host = AssetHost.start assetsRoot
        use client = new HttpClient()

        // %2e%2e is an encoded ".." — .NET's Uri combiner collapses a
        // *literal* ".." itself before the request ever leaves the client,
        // which would make this test pass for the wrong reason (never
        // actually exercising the server's own defense). Encoding it
        // forces the traversal attempt to survive to the server, which
        // must reject it after decoding.
        let outsideName = Path.GetFileName(outsideDir)
        let traversal = Uri($"{host.BaseUri}%%2e%%2e/%%2e%%2e/{outsideName}/secret.txt")

        let! response = client.GetAsync(traversal) |> withTimeout 10.0

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode)
    }

[<Fact>]
let ``binds only to loopback`` () =
    let assetsRoot, _ = setupAssets ()
    use host = AssetHost.start assetsRoot
    Assert.Equal("127.0.0.1", host.BaseUri.Host)
