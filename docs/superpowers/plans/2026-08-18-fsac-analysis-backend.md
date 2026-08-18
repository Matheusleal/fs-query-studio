# FSAC Analysis Backend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `FSharpQueryStudio.Analysis`'s hand-rolled FCS-direct implementation with `fsautocomplete` (FSAC) as the backend for all six `analysis/*` Worker methods, with zero visible change to `Protocol`, `EditorBridge`, the App host, or the Monaco JS side.

**Architecture:** A new `FSharpQueryStudio.Fsac` project owns a stdio LSP client (`LspTransport` + `LspClient`), a supervised `fsautocomplete` child process (`FsacProcess`), and a translation layer (`FsacBridge`) that drives LSP calls and maps responses back into the existing `Protocol` wire types. `Worker.Handlers` swaps its six `analysis/*` handler bodies from calling `FSharpQueryStudio.Analysis` to calling `FsacBridge`; `FSharpQueryStudio.Analysis` is deleted once the swap is complete and tested.

**Tech Stack:** F#, .NET 10, `System.Text.Json` + `FSharp.SystemTystJson` (via the shared `FSharpQueryStudio.Protocol.Serialization.options`), xUnit, the `fsautocomplete` NuGet dotnet tool.

**Spec:** `docs/superpowers/specs/2026-08-18-fsac-analysis-backend-design.md`

## Global Constraints

- Every existing `Protocol` wire type (`AnalysisCheckResult`, `AnalysisHoverResult`, `AnalysisDefinitionResult`, `AnalysisSignatureHelpResult`, `AnalysisCompletionResult`, `AnalysisRenameResult`) keeps its exact current shape — no field added, removed, or retyped.
- FSAC runs fully offline at runtime: no `dotnet tool restore`/network access when the built app actually launches; the tool is restored into the build output at build time only.
- A dead, hung, or slow-to-start FSAC process must never block or crash `execution/evaluate` (the FSI path) — analysis and execution stay independent failure domains.
- Every new async I/O path (process spawn, LSP request, notification wait) has an explicit bound: nothing waits forever.
- `TreatWarningsAsErrors` is on repo-wide (`Directory.Build.props`); any FCS-related NuGet advisory suppression stays scoped to the one project that needs it, matching `Execution`/`Analysis`'s existing `NoWarn` comments — not applicable here since `Fsac` never references `FSharp.Compiler.Service` directly.

---

## Task 1: `FSharpQueryStudio.Fsac` project scaffold + bundled `fsautocomplete`

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`
- Create: `src/FSharpQueryStudio.Fsac/AssemblyInfo.fs` (placeholder module so the project has at least one compiled file before Task 2 adds real code)
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/PackagingTests.fs`
- Create: `Directory.Build.targets` (repo root — none exists yet)
- Modify: `FSharpQueryStudio.slnx` — add both new projects
- Modify: `.gitignore` — ignore the restored tool folder

**Interfaces:**
- Produces: a `FsacTool/fsautocomplete.dll` file present in `AppContext.BaseDirectory` for any project this build step targets (initially only `FSharpQueryStudio.Fsac.Tests`; `FSharpQueryStudio.Worker` is added to the same `Condition` in Task 12).

- [ ] **Step 1: Create the `Fsac` project**

`src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <Compile Include="AssemblyInfo.fs" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\FSharpQueryStudio.Protocol\FSharpQueryStudio.Protocol.fsproj" />
  </ItemGroup>

</Project>
```

`src/FSharpQueryStudio.Fsac/AssemblyInfo.fs`:

```fsharp
module FSharpQueryStudio.Fsac.AssemblyInfo
```

- [ ] **Step 2: Create the `Fsac.Tests` project**

`tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <Compile Include="PackagingTests.fs" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\FSharpQueryStudio.Fsac\FSharpQueryStudio.Fsac.fsproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add both projects to the solution**

Edit `FSharpQueryStudio.slnx`: add `<Project Path="src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj" />` alphabetically under `/src/`, and `<Project Path="tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj" />` alphabetically under `/tests/`.

- [ ] **Step 4: Write the failing packaging test**

`tests/FSharpQueryStudio.Fsac.Tests/PackagingTests.fs`:

```fsharp
module PackagingTests

open System
open System.IO
open Xunit

[<Fact>]
let ``the bundled fsautocomplete tool is present in the build output`` () =
    let dllPath = Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")
    Assert.True(File.Exists(dllPath), $"expected {dllPath} to exist — the RestoreFsautocomplete build target should have copied it here")
```

- [ ] **Step 5: Run it to verify it fails**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: FAIL — `FsacTool/fsautocomplete.dll` doesn't exist yet (no restore step exists).

- [ ] **Step 6: Add the restore build target**

`Directory.Build.targets` (new file, repo root):

```xml
<Project>

  <!-- Restores the bundled fsautocomplete (FSAC) dotnet tool straight into
       the output directory of whichever project spawns it directly:
       FSharpQueryStudio.Fsac.Tests (integration tests) and, once wired in
       a later task, FSharpQueryStudio.Worker (the real running app).
       `dotnet tool update` (not `install`) is idempotent — it installs
       fresh into an empty tool-path, or is a no-op exit-0 if the pinned
       version is already there, so repeated local builds and CI don't
       fail on a second run. -->
  <Target Name="RestoreFsautocomplete" AfterTargets="Build"
          Condition="'$(MSBuildProjectName)' == 'FSharpQueryStudio.Fsac.Tests'">
    <Exec Command="dotnet tool update --tool-path &quot;$(OutDir)FsacTool&quot; fsautocomplete" />
  </Target>

</Project>
```

- [ ] **Step 7: Ignore the restored tool folder**

Add to `.gitignore` (near the other build-output ignores):

```
# Restored dotnet tool, not source - see Directory.Build.targets
**/FsacTool/
```

- [ ] **Step 8: Run the test again to verify it passes**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: PASS. If the restore target's `AfterTargets="Build"` timing doesn't run before the test executable actually launches (MSBuild target ordering can vary by SDK version), switch it to `AfterTargets="Build" BeforeTargets="VSTest"` or add an explicit `dotnet build` step before `dotnet test` in this same command until it's green.

- [ ] **Step 9: Commit**

```bash
git add src/FSharpQueryStudio.Fsac tests/FSharpQueryStudio.Fsac.Tests FSharpQueryStudio.slnx Directory.Build.targets .gitignore
git commit -m "feat: scaffold Fsac project and bundle fsautocomplete"
```

---

## Task 2: LSP `Content-Length` framing (`LspTransport`)

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/LspTransport.fs`
- Modify: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj` (add the new `Compile` item, ahead of `AssemblyInfo.fs`)
- Create: `tests/FSharpQueryStudio.Fsac.Tests/LspTransportTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj` (add the new `Compile` item)

**Interfaces:**
- Produces: `LspTransport.writeMessage: Stream -> string -> Task`, `LspTransport.readMessage: Stream -> Task<string option>`.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/LspTransportTests.fs`:

```fsharp
module LspTransportTests

open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac

/// Yields exactly one byte per `ReadAsync` call, regardless of how many
/// bytes the caller asked for — proves `readMessage` reassembles a full
/// header/body regardless of how the underlying pipe chooses to deliver
/// bytes, the same concern `NdjsonStream`'s own fragmented-stream test
/// covers for the Worker<->App leg.
type private OneByteAtATimeStream(bytes: byte[]) =
    inherit Stream()
    let mutable position = 0
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = int64 bytes.Length
    override _.Position with get () = int64 position and set _ = failwith "not seekable"
    override _.Flush() = ()
    override _.SetLength(_) = failwith "not supported"
    override _.Seek(_, _) = failwith "not supported"
    override _.Write(_, _, _) = failwith "not supported"

    override _.Read(buffer: byte[], offset: int, _count: int) =
        if position >= bytes.Length then
            0
        else
            buffer.[offset] <- bytes.[position]
            position <- position + 1
            1

[<Fact>]
let ``writeMessage then readMessage round-trips a JSON payload`` () =
    task {
        let stream = new MemoryStream()
        do! LspTransport.writeMessage stream """{"jsonrpc":"2.0","id":1,"method":"initialize"}"""
        stream.Position <- 0L
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some """{"jsonrpc":"2.0","id":1,"method":"initialize"}""", result)
    }

[<Fact>]
let ``readMessage reassembles a frame delivered one byte at a time`` () =
    task {
        let json = """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{}}"""
        let bodyBytes = Encoding.UTF8.GetBytes(json)
        let header = $"Content-Length: {bodyBytes.Length}\r\n\r\n"
        let allBytes = Array.append (Encoding.ASCII.GetBytes(header)) bodyBytes
        let stream = new OneByteAtATimeStream(allBytes)
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some json, result)
    }

[<Fact>]
let ``a JSON body containing multi-byte UTF-8 characters round-trips intact`` () =
    task {
        let stream = new MemoryStream()
        let json = """{"jsonrpc":"2.0","method":"x","params":{"text":"héllo wörld — ✓"}}"""
        do! LspTransport.writeMessage stream json
        stream.Position <- 0L
        let! result = LspTransport.readMessage stream
        Assert.Equal(Some json, result)
    }

[<Fact>]
let ``readMessage returns None at a clean end of stream`` () =
    task {
        let stream = new MemoryStream([||])
        let! result = LspTransport.readMessage stream
        Assert.Equal(None, result)
    }
```

- [ ] **Step 2: Add both new files to their `.fsproj`s and run to verify failure**

Add `<Compile Include="LspTransport.fs" />` above `AssemblyInfo.fs` in `FSharpQueryStudio.Fsac.fsproj`, and `<Compile Include="LspTransportTests.fs" />` in `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: FAIL to build — `LspTransport` doesn't exist yet.

- [ ] **Step 3: Implement `LspTransport`**

`src/FSharpQueryStudio.Fsac/LspTransport.fs`:

```fsharp
namespace FSharpQueryStudio.Fsac

open System.IO
open System.Text
open System.Threading.Tasks

/// Reads/writes LSP's `Content-Length:`-framed JSON-RPC messages over a
/// stdio pipe — the Worker<->fsautocomplete leg's own wire format,
/// distinct from `Worker.NdjsonStream`'s newline-delimited framing (which
/// stays exactly as-is for the App<->Worker leg). Per the LSP base
/// protocol: a `Content-Length: <n>\r\n\r\n` ASCII header followed by
/// exactly `n` UTF-8 bytes of JSON body, no trailing newline.
module LspTransport =

    let writeMessage (stream: Stream) (json: string) : Task =
        task {
            let bodyBytes = Encoding.UTF8.GetBytes(json)
            let headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {bodyBytes.Length}\r\n\r\n")
            do! stream.WriteAsync(headerBytes, 0, headerBytes.Length)
            do! stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
            do! stream.FlushAsync()
        }

    /// One ASCII, `\n`-terminated header line (a bare `\r`, if present
    /// right before it, is dropped). `None` only at a genuine end of
    /// stream with nothing at all read for this line — the same "no more
    /// frames" convention `NdjsonStream.readFrame` uses.
    let private readHeaderLine (stream: Stream) : Task<string option> =
        task {
            let bytes = ResizeArray<byte>()
            let buffer = Array.zeroCreate<byte> 1
            let mutable finished = false
            let mutable sawAnyByte = false

            while not finished do
                let! read = stream.ReadAsync(buffer, 0, 1)

                if read = 0 then
                    finished <- true
                else
                    sawAnyByte <- true

                    if buffer.[0] = byte '\n' then
                        finished <- true
                    elif buffer.[0] <> byte '\r' then
                        bytes.Add(buffer.[0])

            if not sawAnyByte then
                return None
            else
                return Some(Encoding.ASCII.GetString(bytes.ToArray()))
        }

    let readMessage (stream: Stream) : Task<string option> =
        task {
            let mutable contentLength = -1
            let mutable reachedEnd = false
            let mutable sawBlankLine = false

            while not sawBlankLine && not reachedEnd do
                let! line = readHeaderLine stream

                match line with
                | None -> reachedEnd <- true
                | Some "" -> sawBlankLine <- true
                | Some line ->
                    let prefix = "Content-Length:"

                    if line.StartsWith(prefix) then
                        contentLength <- int (line.Substring(prefix.Length).Trim())

            if reachedEnd || contentLength < 0 then
                return None
            else
                let body = Array.zeroCreate<byte> contentLength
                let mutable totalRead = 0
                let mutable bodyEnded = false

                while totalRead < contentLength && not bodyEnded do
                    let! read = stream.ReadAsync(body, totalRead, contentLength - totalRead)

                    if read = 0 then
                        bodyEnded <- true // stream closed mid-body - return whatever arrived rather than hang
                    else
                        totalRead <- totalRead + read

                return Some(Encoding.UTF8.GetString(body, 0, totalRead))
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: PASS (all 4 new tests, plus Task 1's packaging test).

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/LspTransport.fs src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj tests/FSharpQueryStudio.Fsac.Tests/LspTransportTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: LSP Content-Length framing (LspTransport)"
```

---

## Task 3: JSON-RPC request/response correlation and notifications (`LspClient`)

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/LspClient.fs`
- Modify: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/LspClientTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Consumes: `LspTransport.writeMessage`/`readMessage` (Task 2); `FSharpQueryStudio.Protocol.Serialization.options`.
- Produces: `type LspError = { Code: int; Message: string }`; `type LspResult = LspOk of JsonElement | LspFail of LspError`; `type LspClient(input: Stream, output: Stream)` with `member SendRequest<'TParams> : method_: string * requestParams: 'TParams -> Task<LspResult>`, `member SendNotification<'TParams> : method_: string * notificationParams: 'TParams -> Task`, `member OnNotification : method_: string * handler: (JsonElement -> unit) -> unit`.

- [ ] **Step 1: Write the failing tests**

These use two anonymous pipes wired back to back so the test can play "fake fsautocomplete" against a real `LspClient`, without needing the real tool.

`tests/FSharpQueryStudio.Fsac.Tests/LspClientTests.fs`:

```fsharp
module LspClientTests

open System.IO.Pipes
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharpQueryStudio.Fsac

type private Loopback =
    { OurClient: LspClient
      ServerRead: AnonymousPipeClientStream
      ServerWrite: AnonymousPipeClientStream }

    interface System.IDisposable with
        member this.Dispose() =
            this.ServerRead.Dispose()
            this.ServerWrite.Dispose()

/// Two pipe pairs, wired so `LspClient` writes onto one and a hand-written
/// "fake server" (this test file) reads from it, and vice versa — a real
/// in-process JSON-RPC round trip with no dependency on the real
/// `fsautocomplete` binary.
let private makeLoopback () =
    let toServer = new AnonymousPipeServerStream(PipeDirection.Out)
    let toServerClient = new AnonymousPipeClientStream(PipeDirection.In, toServer.GetClientHandleAsString())
    let toClient = new AnonymousPipeServerStream(PipeDirection.Out)
    let toClientClient = new AnonymousPipeClientStream(PipeDirection.In, toClient.GetClientHandleAsString())

    let ourClient = LspClient(input = toClientClient, output = toServer)

    { OurClient = ourClient
      ServerRead = toServerClient
      ServerWrite = toClient }

[<Fact>]
let ``SendRequest returns the matching response's result`` () =
    task {
        use loopback = makeLoopback ()

        let serverTask =
            task {
                let! raw = LspTransport.readMessage loopback.ServerRead
                let requestId = JsonDocument.Parse(raw.Value).RootElement.GetProperty("id").GetInt32()
                let response = $"""{{"jsonrpc":"2.0","id":{requestId},"result":{{"ok":true}}}}"""
                do! LspTransport.writeMessage loopback.ServerWrite response
            }

        let! result = loopback.OurClient.SendRequest("workspace/echo", {| ping = "hi" |})
        do! serverTask

        match result with
        | LspOk element -> Assert.True(element.GetProperty("ok").GetBoolean())
        | LspFail err -> failwith $"expected success, got error: {err.Message}"
    }

[<Fact>]
let ``SendRequest surfaces a JSON-RPC error response`` () =
    task {
        use loopback = makeLoopback ()

        let serverTask =
            task {
                let! raw = LspTransport.readMessage loopback.ServerRead
                let requestId = JsonDocument.Parse(raw.Value).RootElement.GetProperty("id").GetInt32()
                let response = $"""{{"jsonrpc":"2.0","id":{requestId},"error":{{"code":-32601,"message":"boom"}}}}"""
                do! LspTransport.writeMessage loopback.ServerWrite response
            }

        let! result = loopback.OurClient.SendRequest("workspace/echo", {| ping = "hi" |})
        do! serverTask

        match result with
        | LspOk _ -> failwith "expected an error"
        | LspFail err ->
            Assert.Equal(-32601, err.Code)
            Assert.Equal("boom", err.Message)
    }

[<Fact>]
let ``OnNotification fires for a matching server-initiated notification`` () =
    task {
        use loopback = makeLoopback ()
        let received = TaskCompletionSource<string>()

        loopback.OurClient.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl -> received.TrySetResult(paramsEl.GetProperty("uri").GetString()) |> ignore
        )

        let notification =
            """{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///a.fsx","diagnostics":[]}}"""

        do! LspTransport.writeMessage loopback.ServerWrite notification
        let! uri = received.Task
        Assert.Equal("file:///a.fsx", uri)
    }
```

- [ ] **Step 2: Wire up the project files and run to verify failure**

Add `<Compile Include="LspClient.fs" />` (after `LspTransport.fs`) to `FSharpQueryStudio.Fsac.fsproj`, and `<Compile Include="LspClientTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: FAIL to build — `LspClient` doesn't exist yet.

- [ ] **Step 3: Implement `LspClient`**

`src/FSharpQueryStudio.Fsac/LspClient.fs`:

```fsharp
namespace FSharpQueryStudio.Fsac

open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

type LspError = { Code: int; Message: string }

type LspResult =
    | LspOk of JsonElement
    | LspFail of LspError

/// A minimal JSON-RPC 2.0 client over `LspTransport`'s framing — the
/// Worker<->fsautocomplete leg. Owns request/response correlation by
/// integer id and dispatches server-initiated notifications
/// (`textDocument/publishDiagnostics`, etc.) to whatever handler is
/// registered for that method *at the moment the notification arrives* —
/// same "no buffering for a not-yet-registered listener" tradeoff
/// `WorkerClient`'s own `pending` dictionary accepts for responses that
/// arrive before anyone is waiting.
type LspClient(input: Stream, output: Stream) =
    let mutable nextId = 0
    let pending = ConcurrentDictionary<int, TaskCompletionSource<LspResult>>()
    let notificationHandlers = ConcurrentDictionary<string, JsonElement -> unit>()
    let writeLock = new SemaphoreSlim(1, 1)
    let nullElement = JsonDocument.Parse("null").RootElement

    let handleIncoming (json: string) =
        let root = JsonDocument.Parse(json).RootElement

        let tryGetInt (name: string) =
            match root.TryGetProperty(name) with
            | true, el when el.ValueKind = JsonValueKind.Number -> Some(el.GetInt32())
            | _ -> None

        let tryGetString (name: string) =
            match root.TryGetProperty(name) with
            | true, el when el.ValueKind = JsonValueKind.String -> Some(el.GetString())
            | _ -> None

        let tryGetElement (name: string) =
            match root.TryGetProperty(name) with
            | true, el -> Some el
            | false, _ -> None

        match tryGetInt "id", tryGetString "method" with
        | Some id, _ ->
            match pending.TryRemove(id) with
            | false, _ -> () // no one is waiting on this id anymore
            | true, tcs ->
                match tryGetElement "error" with
                | Some errEl ->
                    let err = JsonSerializer.Deserialize<LspError>(errEl.GetRawText(), Serialization.options)
                    tcs.TrySetResult(LspFail err) |> ignore
                | None ->
                    let result = tryGetElement "result" |> Option.defaultValue nullElement
                    tcs.TrySetResult(LspOk result) |> ignore
        | None, Some method_ ->
            match notificationHandlers.TryGetValue(method_) with
            | true, handler -> handler (tryGetElement "params" |> Option.defaultValue nullElement)
            | false, _ -> () // nobody registered for this notification
        | None, None -> () // not a valid LSP message - ignored rather than crashing the reader loop

    let startReaderLoop () =
        Task.Run(fun () ->
            (task {
                let mutable keepGoing = true

                while keepGoing do
                    let! message = LspTransport.readMessage input

                    match message with
                    | None ->
                        keepGoing <- false

                        for id in pending.Keys |> Seq.toList do
                            match pending.TryRemove(id) with
                            | true, tcs ->
                                tcs.TrySetResult(LspFail { Code = -1; Message = "the LSP server process exited" })
                                |> ignore
                            | false, _ -> ()
                    | Some json -> handleIncoming json
            })
            :> Task)
        |> ignore

    do startReaderLoop ()

    let send (envelope: obj) : Task =
        task {
            let json = JsonSerializer.Serialize(envelope, Serialization.options)
            do! writeLock.WaitAsync()

            try
                do! LspTransport.writeMessage output json
            finally
                writeLock.Release() |> ignore
        }

    /// Registers `handler` for every future `method_`-named notification.
    /// Must be called before the notification of interest can arrive.
    member _.OnNotification(method_: string, handler: JsonElement -> unit) : unit =
        notificationHandlers.[method_] <- handler

    member _.SendRequest<'TParams>(method_: string, requestParams: 'TParams) : Task<LspResult> =
        task {
            let id = Interlocked.Increment(&nextId)
            let tcs = TaskCompletionSource<LspResult>(TaskCreationOptions.RunContinuationsAsynchronously)
            pending.[id] <- tcs

            do!
                send (
                    {| jsonrpc = "2.0"
                       id = id
                       method = method_
                       ``params`` = requestParams |}
                )

            return! tcs.Task
        }

    member _.SendNotification<'TParams>(method_: string, notificationParams: 'TParams) : Task =
        send (
            {| jsonrpc = "2.0"
               method = method_
               ``params`` = notificationParams |}
        )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/LspClient.fs src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj tests/FSharpQueryStudio.Fsac.Tests/LspClientTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: JSON-RPC request/response correlation (LspClient)"
```

---

## Task 4: Spawn and supervise the real `fsautocomplete` process (`FsacProcess`)

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/FsacProcess.fs`
- Modify: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacProcessTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Consumes: `LspClient` (Task 3).
- Produces: `type FsacProcess(fsautocompleteDllPath: string)` with `member EnsureClient : unit -> Task<LspClient>` (starts the process and completes the `initialize`/`initialized` handshake on first call, or on any call after the previous process has exited; returns the live client otherwise).

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacProcessTests.fs`:

```fsharp
module FsacProcessTests

open System
open System.IO
open Xunit
open FSharpQueryStudio.Fsac

let private fsacDllPath () =
    Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")

[<Fact>]
let ``EnsureClient starts fsautocomplete and completes the initialize handshake`` () =
    task {
        let fsac = FsacProcess(fsacDllPath ())
        let! client = fsac.EnsureClient()

        let! result = client.SendRequest("textDocument/hover", {| textDocument = {| uri = "file:///doesnotexist.fsx" |}; position = {| line = 0; character = 0 |} |})

        // Any well-formed response (success or a structured LSP error) proves
        // the process is alive and speaking JSON-RPC back - the content of
        // this particular hover doesn't matter, only that the handshake
        // completed and the process answers requests.
        match result with
        | LspOk _
        | LspFail _ -> ()
    }

[<Fact>]
let ``EnsureClient respawns after the process is killed`` () =
    task {
        let fsac = FsacProcess(fsacDllPath ())
        let! _firstClient = fsac.EnsureClient()
        fsac.KillForTests()

        let! secondClient = fsac.EnsureClient()
        let! result = secondClient.SendRequest("textDocument/hover", {| textDocument = {| uri = "file:///a.fsx" |}; position = {| line = 0; character = 0 |} |})

        match result with
        | LspOk _
        | LspFail { Code = -1 } -> failwith "expected a real response from a freshly respawned process, not the old dead one"
        | LspFail _ -> ()
    }
```

- [ ] **Step 2: Wire up the project files and run to verify failure**

Add `<Compile Include="FsacProcess.fs" />` (after `LspClient.fs`) to `FSharpQueryStudio.Fsac.fsproj`, and `<Compile Include="FsacProcessTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacProcessTests`
Expected: FAIL to build — `FsacProcess` doesn't exist yet.

- [ ] **Step 3: Implement `FsacProcess`**

`src/FSharpQueryStudio.Fsac/FsacProcess.fs`:

```fsharp
namespace FSharpQueryStudio.Fsac

open System.Diagnostics
open System.Threading
open System.Threading.Tasks

/// Spawns and supervises the bundled `fsautocomplete` child process,
/// mirroring `App.WorkerClient`'s own "start on first use, detect exit,
/// respawn on the next call" ownership model one process level down: the
/// App supervises the Worker, the Worker (via this type) supervises FSAC.
/// `fsautocomplete` speaks LSP over stdio by default (no CLI mode flag) —
/// confirmed against the pinned tool version during this task's own test
/// run; if a future version needs an explicit flag, add it to `Arguments`
/// below.
type FsacProcess(fsautocompleteDllPath: string) =
    let mutable current: (Process * LspClient) option = None
    let startLock = new SemaphoreSlim(1, 1)

    let spawn () : Process * LspClient =
        let psi =
            ProcessStartInfo(
                FileName = "dotnet",
                Arguments = $"\"{fsautocompleteDllPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        let proc = new Process(StartInfo = psi)
        proc.Start() |> ignore
        let client = LspClient(input = proc.StandardOutput.BaseStream, output = proc.StandardInput.BaseStream)
        proc, client

    let initializeHandshake (client: LspClient) : Task =
        task {
            let! _ =
                client.SendRequest(
                    "initialize",
                    {| processId = System.Diagnostics.Process.GetCurrentProcess().Id
                       rootUri = null
                       capabilities = {| |} |}
                )

            do! client.SendNotification("initialized", {| |})
        }

    /// Returns the live `LspClient` for the current `fsautocomplete`
    /// process, starting one (and completing the `initialize`/
    /// `initialized` handshake) if none is running or the previous one
    /// has exited. Serialized by `startLock` so two concurrent
    /// `analysis/*` calls arriving while nothing is running don't each
    /// spawn their own process.
    member _.EnsureClient() : Task<LspClient> =
        task {
            do! startLock.WaitAsync()

            try
                match current with
                | Some(proc, client) when not proc.HasExited -> return client
                | _ ->
                    let proc, client = spawn ()
                    do! initializeHandshake client
                    current <- Some(proc, client)
                    return client
            finally
                startLock.Release() |> ignore
        }

    /// Test-only: simulates a crash so `EnsureClient`'s respawn path can
    /// be exercised deterministically, without relying on a real fault.
    member _.KillForTests() : unit =
        match current with
        | Some(proc, _) when not proc.HasExited -> proc.Kill()
        | _ -> ()

    /// Best-effort graceful shutdown — never throws past this call, same
    /// convention as `Handlers.deleteAnalysisWorkDir`'s own best-effort
    /// cleanup. Wired to the Worker process's own exit in a later task.
    member _.Shutdown() : unit =
        match current with
        | Some(proc, _) ->
            try
                if not proc.HasExited then
                    proc.Kill()
            with _ ->
                ()

            current <- None
        | None -> ()
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacProcessTests`
Expected: PASS. If `initialize` fails outright (a real error result rather than a well-formed `LspFail`, or a hang), inspect the process's redirected `StandardError` to see what the real `fsautocomplete` version actually expects and adjust `Arguments`/`initializeHandshake` accordingly — this is the one step in the whole plan most likely to need on-the-spot adjustment against the real tool.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacProcess.fs src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj tests/FSharpQueryStudio.Fsac.Tests/FsacProcessTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: spawn and supervise the fsautocomplete process (FsacProcess)"
```

---

## Task 5: Prologue and line-offset math (`Prologue`)

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/Prologue.fs`
- Modify: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj` — add `ProjectReference` to `FSharpQueryStudio.Connectors` and `FSharpQueryStudio.Results` (needed to resolve `typeof<...>.Assembly.Location`, same as `AnalysisContext.build` does today)

**Interfaces:**
- Consumes: `typeof<FSharpQueryStudio.Connectors.IConnector>`, `typeof<FSharpQueryStudio.Results.PagingCursor>` (existing types, for their assembly paths).
- Produces: `Prologue.build : unit -> string` (the full prologue text to prepend), `Prologue.lineCount : string -> int` (number of real lines the prologue occupies, for offset math), `Prologue.sourceForCheck : string -> string` (prologue + source), `Prologue.shiftUp : int -> int` (caller's 1-based line -> the line to ask FSAC about), `Prologue.shiftDown : int -> int` (an FSAC-reported line in the checked document -> the caller's real line).

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs`:

```fsharp
module PrologueTests

open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``build emits real #r and open lines for Connectors and Results`` () =
    let text = Prologue.build ()
    Assert.Contains("#r \"", text)
    Assert.Contains("open FSharpQueryStudio.Connectors", text)
    Assert.Contains("open FSharpQueryStudio.Results", text)

[<Fact>]
let ``lineCount matches the number of real lines build() occupies`` () =
    let text = Prologue.build ()
    let expected = text.Split('\n').Length - 1 // build() ends with a trailing newline, matching AnalysisContext's own prologue
    Assert.Equal(expected, Prologue.lineCount text)

[<Fact>]
let ``sourceForCheck prepends the prologue ahead of the real source`` () =
    let source = "let x = 1"
    let checked_ = Prologue.sourceForCheck source
    Assert.StartsWith(Prologue.build (), checked_)
    Assert.EndsWith(source, checked_)

[<Fact>]
let ``shiftUp then shiftDown is the identity`` () =
    let original = 4
    Assert.Equal(original, Prologue.shiftDown (Prologue.shiftUp original))
```

- [ ] **Step 2: Wire up the project files and run to verify failure**

Add `<Compile Include="Prologue.fs" />` (after `FsacProcess.fs`) to `FSharpQueryStudio.Fsac.fsproj`, plus `<ProjectReference Include="..\FSharpQueryStudio.Connectors\FSharpQueryStudio.Connectors.fsproj" />` and `<ProjectReference Include="..\FSharpQueryStudio.Results\FSharpQueryStudio.Results.fsproj" />`. Add `<Compile Include="PrologueTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter PrologueTests`
Expected: FAIL to build — `Prologue` doesn't exist yet.

- [ ] **Step 3: Implement `Prologue`**

`src/FSharpQueryStudio.Fsac/Prologue.fs`:

```fsharp
namespace FSharpQueryStudio.Fsac

/// The controlled prologue every script checked through FSAC gets
/// prepended with, so `Connections.sql`/`Results.table` resolve
/// unqualified — matching what real execution (`Execution.FsiSession
/// .create`) already does, and what `Analysis.AnalysisContext` used to do
/// via FCS `otherFlags`. Unlike `otherFlags` (an FCS-project-options-only
/// mechanism with no FSAC equivalent), this uses real `#r`/`open` lines
/// physically in the checked text — an ordinary FCS/FSI script directive
/// FSAC's own FCS-backed script handling resolves with no special
/// configuration on FSAC's side.
module Prologue =

    let build () : string =
        let connectorsPath = (typeof<FSharpQueryStudio.Connectors.IConnector>).Assembly.Location
        let resultsPath = (typeof<FSharpQueryStudio.Results.PagingCursor>).Assembly.Location

        $"#r \"{connectorsPath}\"\n#r \"{resultsPath}\"\nopen FSharpQueryStudio.Connectors\nopen FSharpQueryStudio.Results\n"

    /// The number of real (newline-terminated) lines `prologueText`
    /// occupies — computed from the actual text rather than a hardcoded
    /// constant, so a future edit to `build()` can't silently desync the
    /// offset math from what's really being prepended.
    let lineCount (prologueText: string) : int =
        prologueText.Split('\n').Length - 1

    let sourceForCheck (source: string) : string = build () + source

    /// A caller's own 1-based line (against the unprefixed source) ->
    /// the line to ask FSAC about in the prologue-prefixed document.
    let shiftUp (line: int) : int = line + lineCount (build ())

    /// An FSAC-reported line *inside this same checked document* -> the
    /// caller's real, unprefixed line. Never apply this to a location FSAC
    /// resolves in a different file (FSharp.Core, another script) — that
    /// has its own real, unshifted lines, same caveat `AnalysisContext
    /// .lineOffset`'s doc comment already carries.
    let shiftDown (line: int) : int = line - lineCount (build ())
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter PrologueTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/Prologue.fs src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj tests/FSharpQueryStudio.Fsac.Tests/PrologueTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: prologue and line-offset math (Prologue)"
```

---

## Task 6: `FsacBridge.check` — diagnostics via `publishDiagnostics`

**Files:**
- Create: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Modify: `src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCheckTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Consumes: `FsacProcess.EnsureClient` (Task 4), `Prologue.*` (Task 5), `FSharpQueryStudio.Protocol.Diagnostic`/`DiagnosticSeverity`.
- Produces: a shared, module-level `FsacBridge.sharedProcess: FsacProcess` plus `FsacBridge.check : documentUri: string -> version: int -> source: string -> Task<Diagnostic list>`. Establishes the document-sync helper `FsacBridge.syncDocument : client: LspClient -> documentUri: string -> version: int -> checkedSource: string -> Task<unit>` (didOpen the first time a `documentUri` is seen, didChange after) that every later method in this task reuses.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCheckTests.fs`:

```fsharp
module FsacBridgeCheckTests

open System
open Xunit
open FSharpQueryStudio.Fsac
open FSharpQueryStudio.Protocol

[<Fact>]
let ``check on a script with a real type error reports a diagnostic at the real (unshifted) line`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        // Line 0: the error. The prologue is invisible to the caller -
        // if line-offset math is wrong, this reports line 4 (prologue is
        // 4 real lines: two #r, two open) instead of 0.
        let! diagnostics = FsacBridge.check uri 1 "let x : int = \"not an int\""

        Assert.NotEmpty(diagnostics)
        Assert.Equal(0, diagnostics.[0].StartLine)
        Assert.Equal(DiagnosticSeverity.Error, diagnostics.[0].Severity)
    }

[<Fact>]
let ``check on a script that resolves Connections.sql with no #r or open of its own reports no diagnostics`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        let! diagnostics = FsacBridge.check uri 1 "let f = Connections.sql"
        Assert.Empty(diagnostics)
    }

[<Fact>]
let ``a later check for the same documentUri and a newer version reflects the newer source, not the stale one`` () =
    task {
        let uri = $"file:///check-test-{Guid.NewGuid()}.fsx"
        let! _first = FsacBridge.check uri 1 "let x : int = \"bad\""
        let! second = FsacBridge.check uri 2 "let x = 1"
        Assert.Empty(second)
    }
```

- [ ] **Step 2: Wire up the project files and run to verify failure**

Add `<Compile Include="FsacBridge.fs" />` (after `Prologue.fs`) to `FSharpQueryStudio.Fsac.fsproj`, and `<Compile Include="FsacBridgeCheckTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeCheckTests`
Expected: FAIL to build — `FsacBridge` doesn't exist yet.

- [ ] **Step 3: Implement `FsacBridge` (document sync + `check`)**

`src/FSharpQueryStudio.Fsac/FsacBridge.fs`:

```fsharp
namespace FSharpQueryStudio.Fsac

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

/// Translates the Worker's existing `analysis/*` payloads into LSP calls
/// against a shared `FsacProcess`, and maps LSP responses back into the
/// existing `Protocol` result types. `Handlers.fs` calls straight into
/// this module (Task 12) — no change to any wire type.
module FsacBridge =

    let private fsacDllPath () =
        Path.Combine(AppContext.BaseDirectory, "FsacTool", "fsautocomplete.dll")

    /// One shared `FsacProcess` for the Worker's whole lifetime, same
    /// "one long-lived instance" shape as `AnalysisContext.checker`.
    let sharedProcess = FsacProcess(fsacDllPath ())

    /// Every `documentUri` this Worker process has ever `didOpen`'d, so
    /// the next sync for that same uri sends `didChange` instead of a
    /// second `didOpen` (which LSP servers, including FSAC, reject for an
    /// already-open document).
    let private openDocuments = ConcurrentDictionary<string, bool>()

    let syncDocument (client: LspClient) (documentUri: string) (version: int) (checkedSource: string) : Task =
        if openDocuments.TryAdd(documentUri, true) then
            client.SendNotification(
                "textDocument/didOpen",
                {| textDocument =
                    {| uri = documentUri
                       languageId = "fsharp"
                       version = version
                       text = checkedSource |} |}
            )
        else
            client.SendNotification(
                "textDocument/didChange",
                {| textDocument = {| uri = documentUri; version = version |}
                   contentChanges = [ {| text = checkedSource |} ] |}
            )

    let private diagnosticsPending =
        ConcurrentDictionary<struct (string * int), TaskCompletionSource<Diagnostic list>>()

    let private mapSeverity (severity: int option) : DiagnosticSeverity =
        match severity with
        | Some 1 -> DiagnosticSeverity.Error
        | Some 2 -> DiagnosticSeverity.Warning
        | _ -> DiagnosticSeverity.Information

    /// Registered once, the first time any `FsacBridge` function runs
    /// against a live client — `EnsureClient` always returns the *same*
    /// `LspClient` instance while the underlying process is alive, so a
    /// second registration would just overwrite the first with an
    /// identical handler. Resolves whichever pending entry matches the
    /// notification's own `(uri, version)`, and drops it if none does (a
    /// push for an already-superseded version, or one nobody is waiting
    /// on) — the version-staleness guard the design calls for.
    let private registerDiagnosticsHandler (client: LspClient) =
        client.OnNotification(
            "textDocument/publishDiagnostics",
            fun paramsEl ->
                let uri = paramsEl.GetProperty("uri").GetString()

                let version =
                    match paramsEl.TryGetProperty("version") with
                    | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                    | _ -> -1

                match diagnosticsPending.TryRemove(struct (uri, version)) with
                | false, _ -> ()
                | true, tcs ->
                    let diagnostics =
                        paramsEl.GetProperty("diagnostics").EnumerateArray()
                        |> Seq.map (fun d ->
                            let range = d.GetProperty("range")
                            let start = range.GetProperty("start")
                            let end_ = range.GetProperty("end")

                            let severity =
                                match d.TryGetProperty("severity") with
                                | true, s when s.ValueKind = JsonValueKind.Number -> Some(s.GetInt32())
                                | _ -> None

                            let code =
                                match d.TryGetProperty("code") with
                                | true, c when c.ValueKind = JsonValueKind.String -> Some(c.GetString())
                                | true, c when c.ValueKind = JsonValueKind.Number -> Some(string (c.GetInt32()))
                                | _ -> None

                            { Message = d.GetProperty("message").GetString()
                              Severity = mapSeverity severity
                              StartLine = Prologue.shiftDown (start.GetProperty("line").GetInt32() + 1) - 1
                              StartColumn = start.GetProperty("character").GetInt32()
                              EndLine = Prologue.shiftDown (end_.GetProperty("line").GetInt32() + 1) - 1
                              EndColumn = end_.GetProperty("character").GetInt32()
                              Code = code })
                        |> List.ofSeq

                    tcs.TrySetResult(diagnostics) |> ignore
        )

    let mutable private handlerRegistered = false

    let check (documentUri: string) (version: int) (source: string) : Task<Diagnostic list> =
        task {
            let! client = sharedProcess.EnsureClient()

            if not handlerRegistered then
                registerDiagnosticsHandler client
                handlerRegistered <- true

            let tcs = TaskCompletionSource<Diagnostic list>(TaskCreationOptions.RunContinuationsAsynchronously)
            diagnosticsPending.[struct (documentUri, version)] <- tcs

            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! winner = Task.WhenAny(tcs.Task :> Task, Task.Delay(TimeSpan.FromSeconds 10.0))

            if winner = (tcs.Task :> Task) then
                return! tcs.Task
            else
                diagnosticsPending.TryRemove(struct (documentUri, version)) |> ignore
                return []
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeCheckTests`
Expected: PASS. If FSAC reports `severity`/`code`/`range` under slightly different JSON shapes than assumed above, adjust the property-reading code to match what the real process actually sends (inspect via a temporary `printfn` of the raw notification JSON, then remove it).

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs src/FSharpQueryStudio.Fsac/FSharpQueryStudio.Fsac.fsproj tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCheckTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.check via publishDiagnostics"
```

---

## Task 7: `FsacBridge.hover`

**Files:**
- Modify: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeHoverTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Consumes: `FsacBridge.syncDocument`, `FsacBridge.sharedProcess`, `Prologue.*` (all Tasks 5-6).
- Produces: `FsacBridge.hover : documentUri: string -> version: int -> source: string -> line: int -> column: int -> Task<HoverInfo option>`. `line`/`column` use the same convention every existing `Analysis.Hover`/`GoToDefinition`/etc. call already used (1-based line, 0-based column, at/after the identifier's end) — `Handlers.fs`'s callers don't change in Task 12.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeHoverTests.fs`:

```fsharp
module FsacBridgeHoverTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``hover on a typed let binding returns its display name and inferred type`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        // "total" ends at column 9 (0-based) on line 1: "let total = 42".
        let! result = FsacBridge.hover uri 1 "let total = 42" 1 9

        match result with
        | None -> failwith "expected hover info"
        | Some info ->
            Assert.Equal("total", info.DisplayName)
            Assert.Equal("int", info.Type)
    }

[<Fact>]
let ``hover on Connections.sql resolves with no #r or open of its own`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.hover uri 1 "Connections.sql" 1 15

        match result with
        | None -> failwith "expected hover info"
        | Some info -> Assert.Equal("sql", info.DisplayName)
    }

[<Fact>]
let ``hover at a position with no resolvable symbol returns None`` () =
    task {
        let uri = $"file:///hover-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.hover uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
```

- [ ] **Step 2: Wire up the project file and run to verify failure**

Add `<Compile Include="FsacBridgeHoverTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeHoverTests`
Expected: FAIL to build — `FsacBridge.hover` doesn't exist yet.

- [ ] **Step 3: Add `hover` to `FsacBridge`**

Append to `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (inside the `FsacBridge` module, after `check`):

```fsharp
    let private markupText (contentsEl: JsonElement) : string =
        match contentsEl.ValueKind with
        | JsonValueKind.Object -> contentsEl.GetProperty("value").GetString()
        | JsonValueKind.String -> contentsEl.GetString()
        | JsonValueKind.Array ->
            contentsEl.EnumerateArray()
            |> Seq.map (fun el -> if el.ValueKind = JsonValueKind.String then el.GetString() else el.GetProperty("value").GetString())
            |> String.concat "\n"
        | _ -> ""

    /// Best-effort split of FSAC's single markdown hover blob into this
    /// app's `DisplayName`/`Type`/`Signature`/`XmlDoc` shape. FSAC's
    /// hover text isn't structured the way FCS's own `FSharpMemberOrFunctionOrValue`
    /// was directly - the first line is the symbol's signature (used for
    /// both `Type` and `DisplayName`'s fallback), everything after a
    /// blank line is treated as documentation.
    let private parseHoverMarkdown (markdown: string) : HoverInfo =
        let lines = markdown.Replace("\r\n", "\n").Split('\n') |> Array.toList

        let signatureLine =
            lines
            |> List.tryFind (fun l -> not (String.IsNullOrWhiteSpace l))
            |> Option.defaultValue ""
            |> fun l -> l.Trim('`', ' ')

        let docLines =
            lines
            |> List.skipWhile (fun l -> not (String.IsNullOrWhiteSpace l))
            |> List.skipWhile String.IsNullOrWhiteSpace
            |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l))

        let displayName =
            match signatureLine.Split(':') with
            | parts when parts.Length > 0 -> parts.[0].Trim()
            | _ -> signatureLine

        { DisplayName = displayName
          Type = signatureLine
          Signature = if signatureLine.Contains("->") then Some signatureLine else None
          XmlDoc = if docLines.IsEmpty then None else Some(docLines |> String.concat "\n") }

    let hover (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<HoverInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()
            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/hover",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl -> return Some(parseHoverMarkdown (markupText (resultEl.GetProperty("contents"))))
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeHoverTests`
Expected: PASS. If FSAC's real hover markdown doesn't split into signature/doc the way `parseHoverMarkdown` assumes, adjust it against the actual returned text (print it once, inspect, fix the split logic) — this mapping is inherently best-effort text-parsing, unlike the structured LSP JSON the other five methods use.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeHoverTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.hover"
```

---

## Task 8: `FsacBridge.definition`

**Files:**
- Modify: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeDefinitionTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Produces: `FsacBridge.definition : documentUri: string -> version: int -> source: string -> line: int -> column: int -> Task<DefinitionLocation option>`.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeDefinitionTests.fs`:

```fsharp
module FsacBridgeDefinitionTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``definition on a same-file use resolves to the declaration, with the caller's own DocumentUri`` () =
    task {
        let uri = $"file:///definition-test-{Guid.NewGuid()}.fsx"
        let source = "let add x y = x + y\nlet total = add 1 2"
        // "add" on line 2 ends at column 6 (0-based): "let total = add 1 2".
        let! result = FsacBridge.definition uri 1 source 2 6

        match result with
        | None -> failwith "expected a resolved definition"
        | Some loc ->
            Assert.Equal(uri, loc.FilePath)
            Assert.Equal(0, loc.StartLine) // the declaration is on the real (unshifted) line 0
    }

[<Fact>]
let ``definition at a position with nothing resolvable returns None`` () =
    task {
        let uri = $"file:///definition-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.definition uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
```

- [ ] **Step 2: Wire up the project file and run to verify failure**

Add `<Compile Include="FsacBridgeDefinitionTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeDefinitionTests`
Expected: FAIL to build — `FsacBridge.definition` doesn't exist yet.

- [ ] **Step 3: Add `definition` to `FsacBridge`**

Append to `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (after `hover`):

```fsharp
    /// LSP's `textDocument/definition` result is `Location | Location[] |
    /// LocationLink[] | null` - parses defensively and takes the first
    /// hit, matching today's "one resolved declaration" behavior.
    let private parseFirstLocation (resultEl: JsonElement) : (string * JsonElement) option =
        match resultEl.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Array when resultEl.GetArrayLength() = 0 -> None
        | JsonValueKind.Array ->
            let first = resultEl.[0]

            let rangeEl =
                match first.TryGetProperty("range") with
                | true, r -> r
                | false, _ -> first.GetProperty("targetRange") // LocationLink shape

            let uri =
                match first.TryGetProperty("uri") with
                | true, u -> u.GetString()
                | false, _ -> first.GetProperty("targetUri").GetString()

            Some(uri, rangeEl)
        | JsonValueKind.Object -> Some(resultEl.GetProperty("uri").GetString(), resultEl.GetProperty("range"))
        | _ -> None

    let definition
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<DefinitionLocation option> =
        task {
            let! client = sharedProcess.EnsureClient()
            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/definition",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl ->
                match parseFirstLocation resultEl with
                | None -> return None
                | Some(resolvedUri, range) ->
                    let start = range.GetProperty("start")
                    let end_ = range.GetProperty("end")

                    // Only a declaration inside *this* checked document was
                    // ever shifted by the prologue - one FSAC resolves in a
                    // different file (FSharp.Core, another script) has its
                    // own real, unshifted lines. `resolvedUri` for a
                    // same-file result is FSAC's own internal identity for
                    // `documentUri` (it always echoes back the uri we
                    // `didOpen`'d it under), so comparing against
                    // `documentUri` itself is the right same-file check.
                    let filePath, lineAdjustment =
                        if resolvedUri = documentUri then
                            documentUri, true
                        else
                            resolvedUri, false

                    let shift l = if lineAdjustment then Prologue.shiftDown (l + 1) - 1 else l

                    return
                        Some
                            { FilePath = filePath
                              StartLine = shift (start.GetProperty("line").GetInt32())
                              StartColumn = start.GetProperty("character").GetInt32()
                              EndLine = shift (end_.GetProperty("line").GetInt32())
                              EndColumn = end_.GetProperty("character").GetInt32() }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeDefinitionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeDefinitionTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.definition"
```

---

## Task 9: `FsacBridge.signatureHelp`

**Files:**
- Modify: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeSignatureHelpTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Produces: `FsacBridge.signatureHelp : documentUri: string -> version: int -> source: string -> line: int -> column: int -> Task<SignatureHelpInfo option>`.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeSignatureHelpTests.fs`:

```fsharp
module FsacBridgeSignatureHelpTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``signatureHelp on a function symbol returns its parameters and return type`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"
        let source = "let add (x: int) (y: int) : int = x + y"
        // "add" ends at column 6 (0-based) on line 1.
        let! result = FsacBridge.signatureHelp uri 1 source 1 6

        match result with
        | None -> failwith "expected signature help"
        | Some info ->
            Assert.Equal("add", info.DisplayName)
            Assert.Equal(2, info.Parameters.Length)
    }

[<Fact>]
let ``signatureHelp on a non-callable symbol returns None`` () =
    task {
        let uri = $"file:///sighelp-test-{Guid.NewGuid()}.fsx"
        // "total" ends at column 9 (0-based): "let total = 42".
        let! result = FsacBridge.signatureHelp uri 1 "let total = 42" 1 9
        Assert.Equal(None, result)
    }
```

- [ ] **Step 2: Wire up the project file and run to verify failure**

Add `<Compile Include="FsacBridgeSignatureHelpTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeSignatureHelpTests`
Expected: FAIL to build — `FsacBridge.signatureHelp` doesn't exist yet.

- [ ] **Step 3: Add `signatureHelp` to `FsacBridge`**

Append to `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (after `definition`):

```fsharp
    let signatureHelp
        (documentUri: string)
        (version: int)
        (source: string)
        (line: int)
        (column: int)
        : Task<SignatureHelpInfo option> =
        task {
            let! client = sharedProcess.EnsureClient()
            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/signatureHelp",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl ->
                let signatures = resultEl.GetProperty("signatures")

                if signatures.GetArrayLength() = 0 then
                    return None
                else
                    let sig_ = signatures.[0]
                    let label = sig_.GetProperty("label").GetString()

                    let parameters =
                        match sig_.TryGetProperty("parameters") with
                        | true, paramsEl when paramsEl.ValueKind = JsonValueKind.Array ->
                            paramsEl.EnumerateArray()
                            |> Seq.map (fun p -> p.GetProperty("label").GetString())
                            |> List.ofSeq
                        | _ -> []

                    if parameters.IsEmpty then
                        // A resolved-but-not-callable symbol - FSAC returns
                        // an empty signature list's worth of parameters
                        // rather than omitting the result entirely for some
                        // symbol kinds; treat it the same as "not callable".
                        return None
                    else
                        // FSAC's `label` is formatted as
                        // `name(param1: T1, param2: T2) -> TReturn`-ish
                        // text - the name is everything before the first
                        // '(', the return type everything after the last
                        // "->".
                        let displayName =
                            match label.IndexOf('(') with
                            | -1 -> label
                            | i -> label.Substring(0, i).Trim()

                        let returnType =
                            match label.LastIndexOf("->") with
                            | -1 -> label
                            | i -> label.Substring(i + 2).Trim()

                        return
                            Some
                                { DisplayName = displayName
                                  Parameters = parameters
                                  ReturnType = returnType }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeSignatureHelpTests`
Expected: PASS. If FSAC's real `label` format differs from the `name(params) -> return` shape assumed above, adjust `displayName`/`returnType`'s parsing against the actual string.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeSignatureHelpTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.signatureHelp"
```

---

## Task 10: `FsacBridge.complete`

**Files:**
- Modify: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCompletionTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Produces: `FsacBridge.complete : documentUri: string -> version: int -> source: string -> line: int -> column: int -> Task<string list>`. Response is flattened to labels only (`AnalysisCompletionResult.Items : string list`, unchanged — see spec Non-goals).

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCompletionTests.fs`:

```fsharp
module FsacBridgeCompletionTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``complete after 's.Sub' on a string returns String's members, not a top-level dump`` () =
    task {
        let uri = $"file:///complete-test-{Guid.NewGuid()}.fsx"
        let source = "let s = \"hello\"\ns.Sub"
        // Caret right after "Sub" on line 2 - column 5 (0-based, exclusive end).
        let! items = FsacBridge.complete uri 1 source 2 5

        Assert.Contains("Substring", items)
    }

[<Fact>]
let ``complete on an empty document returns a non-empty top-level scope`` () =
    task {
        let uri = $"file:///complete-test-{Guid.NewGuid()}.fsx"
        let! items = FsacBridge.complete uri 1 "" 1 0
        Assert.NotEmpty(items)
    }
```

- [ ] **Step 2: Wire up the project file and run to verify failure**

Add `<Compile Include="FsacBridgeCompletionTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeCompletionTests`
Expected: FAIL to build — `FsacBridge.complete` doesn't exist yet.

- [ ] **Step 3: Add `complete` to `FsacBridge`**

Append to `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (after `signatureHelp`):

```fsharp
    /// LSP's `textDocument/completion` result is `CompletionItem[] |
    /// CompletionList | null` - parses defensively, then keeps only each
    /// item's `label` (see the spec's Non-goals: richer `kind`/`detail`
    /// isn't consumed by `Protocol.AnalysisCompletionResult` yet).
    let private parseCompletionLabels (resultEl: JsonElement) : string list =
        match resultEl.ValueKind with
        | JsonValueKind.Null -> []
        | JsonValueKind.Array ->
            resultEl.EnumerateArray() |> Seq.map (fun i -> i.GetProperty("label").GetString()) |> List.ofSeq
        | JsonValueKind.Object ->
            resultEl.GetProperty("items").EnumerateArray()
            |> Seq.map (fun i -> i.GetProperty("label").GetString())
            |> List.ofSeq
        | _ -> []

    let complete (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<string list> =
        task {
            let! client = sharedProcess.EnsureClient()
            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/completion",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |} |}
                )

            match result with
            | LspFail _ -> return []
            | LspOk resultEl -> return parseCompletionLabels resultEl |> List.distinct
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeCompletionTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeCompletionTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.complete"
```

---

## Task 11: `FsacBridge.rename` (via `textDocument/references`)

**Files:**
- Modify: `src/FSharpQueryStudio.Fsac/FsacBridge.fs`
- Create: `tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeRenameTests.fs`
- Modify: `tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj`

**Interfaces:**
- Produces: `FsacBridge.rename : documentUri: string -> version: int -> source: string -> line: int -> column: int -> Task<RenameEdit list option>`. Uses `textDocument/references` with `includeDeclaration = true`, **not** `textDocument/rename` — see spec's Method mapping table: `AnalysisRenameParams` carries no `newName` and `AnalysisRenameResult` carries no replacement text, so this app only ever needs reference spans.

- [ ] **Step 1: Write the failing tests**

`tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeRenameTests.fs`:

```fsharp
module FsacBridgeRenameTests

open System
open Xunit
open FSharpQueryStudio.Fsac

[<Fact>]
let ``rename on a let binding returns every use, including the declaration itself`` () =
    task {
        let uri = $"file:///rename-test-{Guid.NewGuid()}.fsx"
        let source = "let total = 1\nlet doubled = total + total"
        // "total" ends at column 9 (0-based) on line 1: "let total = 1".
        let! result = FsacBridge.rename uri 1 source 1 9

        match result with
        | None -> failwith "expected reference edits"
        | Some edits ->
            // Declaration (line 0) + two uses (line 1) = 3 spans.
            Assert.Equal(3, edits.Length)
            Assert.Contains(edits, fun e -> e.StartLine = 0)
    }

[<Fact>]
let ``rename at a position with nothing resolvable returns None`` () =
    task {
        let uri = $"file:///rename-test-{Guid.NewGuid()}.fsx"
        let! result = FsacBridge.rename uri 1 "()" 1 0
        Assert.Equal(None, result)
    }
```

- [ ] **Step 2: Wire up the project file and run to verify failure**

Add `<Compile Include="FsacBridgeRenameTests.fs" />` to `FSharpQueryStudio.Fsac.Tests.fsproj`.

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeRenameTests`
Expected: FAIL to build — `FsacBridge.rename` doesn't exist yet.

- [ ] **Step 3: Add `rename` to `FsacBridge`**

Append to `src/FSharpQueryStudio.Fsac/FsacBridge.fs` (after `complete`):

```fsharp
    let rename (documentUri: string) (version: int) (source: string) (line: int) (column: int) : Task<RenameEdit list option> =
        task {
            let! client = sharedProcess.EnsureClient()
            do! syncDocument client documentUri version (Prologue.sourceForCheck source)

            let! result =
                client.SendRequest(
                    "textDocument/references",
                    {| textDocument = {| uri = documentUri |}
                       position = {| line = Prologue.shiftUp line - 1; character = column |}
                       context = {| includeDeclaration = true |} |}
                )

            match result with
            | LspFail _ -> return None
            | LspOk resultEl when resultEl.ValueKind = JsonValueKind.Null -> return None
            | LspOk resultEl when resultEl.GetArrayLength() = 0 -> return None
            | LspOk resultEl ->
                let edits =
                    resultEl.EnumerateArray()
                    |> Seq.filter (fun loc -> loc.GetProperty("uri").GetString() = documentUri)
                    |> Seq.map (fun loc ->
                        let range = loc.GetProperty("range")
                        let start = range.GetProperty("start")
                        let end_ = range.GetProperty("end")

                        { StartLine = Prologue.shiftDown (start.GetProperty("line").GetInt32() + 1) - 1
                          StartColumn = start.GetProperty("character").GetInt32()
                          EndLine = Prologue.shiftDown (end_.GetProperty("line").GetInt32() + 1) - 1
                          EndColumn = end_.GetProperty("character").GetInt32() })
                    |> List.ofSeq

                return Some edits
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj --filter FsacBridgeRenameTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/FSharpQueryStudio.Fsac/FsacBridge.fs tests/FSharpQueryStudio.Fsac.Tests/FsacBridgeRenameTests.fs tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
git commit -m "feat: FsacBridge.rename via textDocument/references"
```

---

## Task 12: Wire `Worker.Handlers` to `FsacBridge`; retire `FSharpQueryStudio.Analysis`

**Files:**
- Modify: `src/FSharpQueryStudio.Worker/Handlers.fs` — six `handleAnalysis*` functions
- Modify: `src/FSharpQueryStudio.Worker/Program.fs` — graceful `FsacProcess` shutdown on exit
- Modify: `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj` — drop the `Analysis` reference, add `Fsac`
- Modify: `Directory.Build.targets` — add `FSharpQueryStudio.Worker` to the `RestoreFsautocomplete` `Condition`
- Delete: `src/FSharpQueryStudio.Analysis/` (whole project)
- Delete: `tests/FSharpQueryStudio.Analysis.Tests/` (whole project)
- Modify: `FSharpQueryStudio.slnx` — remove both `Analysis` entries
- Modify: `.github/workflows/ci.yml` — replace `Analysis.Tests` with `Fsac.Tests` in the Linux matrix
- Modify: `tests/FSharpQueryStudio.Worker.Tests/HandlersTests.fs` — any test asserting FCS-specific error text/behavior now asserts against FSAC's real behavior instead (see Step 4)

**Interfaces:**
- Consumes: `FsacBridge.check/hover/definition/signatureHelp/complete/rename` (Tasks 6-11).

- [ ] **Step 1: Swap `Worker.fsproj`'s project reference**

In `src/FSharpQueryStudio.Worker/FSharpQueryStudio.Worker.fsproj`, replace:

```xml
    <ProjectReference Include="..\FSharpQueryStudio.Analysis\FSharpQueryStudio.Analysis.fsproj" />
```

with:

```xml
    <ProjectReference Include="..\FSharpQueryStudio.Fsac\FSharpQueryStudio.Fsac.fsproj" />
```

- [ ] **Step 2: Add `Worker` to the fsautocomplete restore target**

In `Directory.Build.targets`, change the `Condition` to:

```xml
Condition="'$(MSBuildProjectName)' == 'FSharpQueryStudio.Fsac.Tests' or '$(MSBuildProjectName)' == 'FSharpQueryStudio.Worker'"
```

- [ ] **Step 3: Swap each `handleAnalysis*` body in `Handlers.fs`**

Replace `open FSharpQueryStudio.Analysis` with `open FSharpQueryStudio.Fsac` near the top of `src/FSharpQueryStudio.Worker/Handlers.fs`.

Replace `handleAnalysisCheck` (currently building an `AnalysisContext`/calling `Checker.check`/`DiagnosticMapping.map`) with:

```fsharp
    let handleAnalysisCheck (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let checkParams =
                JsonSerializer.Deserialize<AnalysisCheckParams>(request.Params, Serialization.options)

            let! diagnostics = FsacBridge.check checkParams.DocumentUri checkParams.Version checkParams.Source
            let result: AnalysisCheckResult = { Diagnostics = diagnostics }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Replace `handleAnalysisHover` with:

```fsharp
    let handleAnalysisHover (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let hoverParams =
                JsonSerializer.Deserialize<AnalysisHoverParams>(request.Params, Serialization.options)

            let! hover =
                FsacBridge.hover hoverParams.DocumentUri hoverParams.Version hoverParams.Source hoverParams.Line hoverParams.Column

            let result: AnalysisHoverResult = { Hover = hover }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Replace `handleAnalysisDefinition` with (the same-file `FilePath` rewrite already happens inside `FsacBridge.definition`, so this handler no longer needs its own):

```fsharp
    let handleAnalysisDefinition (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let definitionParams =
                JsonSerializer.Deserialize<AnalysisDefinitionParams>(request.Params, Serialization.options)

            let! location =
                FsacBridge.definition
                    definitionParams.DocumentUri
                    definitionParams.Version
                    definitionParams.Source
                    definitionParams.Line
                    definitionParams.Column

            let result: AnalysisDefinitionResult = { Location = location }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Replace `handleAnalysisSignatureHelp` with:

```fsharp
    let handleAnalysisSignatureHelp (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let signatureHelpParams =
                JsonSerializer.Deserialize<AnalysisSignatureHelpParams>(request.Params, Serialization.options)

            let! signatureHelp =
                FsacBridge.signatureHelp
                    signatureHelpParams.DocumentUri
                    signatureHelpParams.Version
                    signatureHelpParams.Source
                    signatureHelpParams.Line
                    signatureHelpParams.Column

            let result: AnalysisSignatureHelpResult = { SignatureHelp = signatureHelp }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Replace `handleAnalysisComplete` with:

```fsharp
    let handleAnalysisComplete (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let completionParams =
                JsonSerializer.Deserialize<AnalysisCompletionParams>(request.Params, Serialization.options)

            let! items =
                FsacBridge.complete
                    completionParams.DocumentUri
                    completionParams.Version
                    completionParams.Source
                    completionParams.Line
                    completionParams.Column

            let result: AnalysisCompletionResult = { Items = items }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Replace `handleAnalysisRename` (and delete the now-unused `toProtocolRenameEdit` helper — `FsacBridge.rename` already returns `Protocol.RenameEdit` directly) with:

```fsharp
    let handleAnalysisRename (request: WorkerRequestFrame) : Task<WorkerResponseFrame> =
        task {
            let renameParams =
                JsonSerializer.Deserialize<AnalysisRenameParams>(request.Params, Serialization.options)

            let! edits =
                FsacBridge.rename
                    renameParams.DocumentUri
                    renameParams.Version
                    renameParams.Source
                    renameParams.Line
                    renameParams.Column

            let result: AnalysisRenameResult = { Edits = edits }

            return
                { Id = request.Id
                  Result = Some(JsonSerializer.SerializeToElement(result, Serialization.options))
                  Error = None }
        }
```

Delete `createAnalysisWorkDir`/`deleteAnalysisWorkDir` — nothing in `Handlers.fs` needs a throwaway analysis temp directory any more (`FsacBridge` never writes scripts to disk either; FSAC checks the in-memory text sent via `didOpen`/`didChange`, same as FCS's own `SourceText.ofString` did before).

- [ ] **Step 4: Best-effort `FsacProcess` shutdown on Worker exit**

In `src/FSharpQueryStudio.Worker/Program.fs`, inside `main`'s `try`/`finally` (after the read loop returns, before `Log.CloseAndFlush()`), add:

```fsharp
        FSharpQueryStudio.Fsac.FsacBridge.sharedProcess.Shutdown()
```

- [ ] **Step 5: Run `Worker.Tests`, fix any FSAC-specific behavior differences**

Run: `dotnet test tests/FSharpQueryStudio.Worker.Tests/FSharpQueryStudio.Worker.Tests.fsproj`

`HandlersTests.fs` almost certainly asserts on exact FCS diagnostic message text somewhere (e.g. a specific "type mismatch" wording) — FSAC's diagnostic `message` text comes from the same underlying FCS compiler, so wording should match, but confirm by running the suite and adjusting any assertion that turns out to differ in phrasing (not in meaning) against what FSAC actually returns. Any assertion checking exact line/column math should already agree, since `FsacBridge` reproduces `AnalysisContext`'s exact offset convention (Task 5).
Expected end state: PASS.

- [ ] **Step 6: Delete `FSharpQueryStudio.Analysis` and its test project**

```bash
git rm -r src/FSharpQueryStudio.Analysis tests/FSharpQueryStudio.Analysis.Tests
```

- [ ] **Step 7: Remove both `Analysis` entries from the solution**

Delete the two `<Project Path="...Analysis...">` lines from `FSharpQueryStudio.slnx`.

- [ ] **Step 8: Update the CI Linux matrix**

In `.github/workflows/ci.yml`, replace:

```yaml
          - tests/FSharpQueryStudio.Analysis.Tests/FSharpQueryStudio.Analysis.Tests.fsproj
```

with:

```yaml
          - tests/FSharpQueryStudio.Fsac.Tests/FSharpQueryStudio.Fsac.Tests.fsproj
```

- [ ] **Step 9: Full solution build and test**

Run: `dotnet build FSharpQueryStudio.slnx --configuration Release`
Expected: PASS — no leftover reference to the deleted `Analysis` project anywhere.

Run: `dotnet test FSharpQueryStudio.slnx --configuration Release --filter "Category!=RequiresDocker&Category!=RequiresPublish"`
Expected: PASS.

- [ ] **Step 10: Manual smoke test**

Launch the real app (`dotnet run --project src/FSharpQueryStudio.App`), open a new script tab, type a script that calls `Connections.sql`, and confirm hover/completion/diagnostics still show up in the Monaco editor exactly as before — this is the one behavior no automated test in this plan observes end-to-end through the real WebView/Monaco UI (same standing limitation the spec's Testing section and this codebase's other UI-adjacent changes already carry — no headless rendering harness for the WebView layer).

- [ ] **Step 11: Commit**

```bash
git add src/FSharpQueryStudio.Worker Directory.Build.targets FSharpQueryStudio.slnx .github/workflows/ci.yml tests/FSharpQueryStudio.Worker.Tests
git commit -m "feat: wire Worker.Handlers to FsacBridge, retire FSharpQueryStudio.Analysis"
```

---

## Self-Review Notes

- **Spec coverage:** every Architecture component (`FsacProcess`/`LspClient`/`FsacBridge`), every row of the Method mapping table, the packaging step, every Error handling bullet (crash/restart → Task 4's respawn test; `publishDiagnostics` timeout → Task 6's 10s bound; version staleness → Task 6's stale-version test; packaging failure → Task 1's packaging test), and the Testing section's framing/correlation/crash/timeout/packaging bullets each have a task and a test. Rollout's "single cutover" is Task 12.
- **Type consistency:** `FsacBridge`'s six public functions all take `(documentUri: string) (version: int) (source: string) [line] [column]` in that order, matching `Handlers.fs`'s existing `*Params` field order; `Task 12`'s handler bodies were written against exactly the signatures defined in Tasks 6-11, not renamed or reordered along the way.
- **Deferred, by design (see spec Non-goals):** `AnalysisCompletionResult` stays a flat `string list`; `AnalysisContext.buildForProject`'s multi-file path has no FSAC equivalent built here.

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-18-fsac-analysis-backend.md`. Two execution options:

**1. Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
