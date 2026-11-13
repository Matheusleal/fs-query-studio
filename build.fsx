// ============================================================================
// build.fsx - Build & Release Automation Script for FSharpQueryStudio
// Usage:
//   dotnet fsi build.fsx [target]
// Targets:
//   build    : Compile backend and extension in Release mode (default)
//   release  : Build everything and package the VS Code extension (.vsix)
//   test     : Run unit tests for backend and extension
//   clean    : Clean build artifacts (bin, obj, dist, *.vsix)
//   all      : clean -> build -> test -> release
// ============================================================================

open System
open System.IO
open System.Diagnostics
open System.Runtime.InteropServices

// --- Configuration & Paths ---
let rootDir = __SOURCE_DIRECTORY__
let solutionFile = Path.Combine(rootDir, "FSharpQueryStudio.slnx")
let workerProject = Path.Combine(rootDir, "src", "FSharpQueryStudio.Worker", "FSharpQueryStudio.Worker.fsproj")
let vscodeDir = Path.Combine(rootDir, "editors", "vscode")
let workerOutDir = Path.Combine(vscodeDir, "dist", "worker")
let vsixOutputDir = Path.Combine(vscodeDir, "fs-query-studio.vsix")

let isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)

// --- Console Utilities & Colors ---
let printHeader (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Cyan
    Console.WriteLine("\n" + String.replicate 70 "=")
    Console.WriteLine($"==> {msg}")
    Console.WriteLine(String.replicate 70 "=")
    Console.ResetColor()

let printSuccess (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Green
    Console.WriteLine($"[OK] {msg}")
    Console.ResetColor()

let printError (msg: string) =
    Console.ForegroundColor <- ConsoleColor.Red
    Console.WriteLine($"[ERROR] {msg}")
    Console.ResetColor()

let printInfo (msg: string) =
    Console.ForegroundColor <- ConsoleColor.DarkGray
    Console.WriteLine($"[INFO] {msg}")
    Console.ResetColor()

// --- Process Execution ---
let runProcess (workingDir: string) (command: string) (args: string) =
    let fileName, arguments = 
        if isWindows && (command = "npm" || command = "npx") then
            "cmd.exe", $"/c \"{command} {args}\""
        else
            command, args

    printInfo $"Executing in '{workingDir}': {command} {args}"
    
    let psi = ProcessStartInfo(
        FileName = fileName,
        Arguments = arguments,
        WorkingDirectory = workingDir,
        UseShellExecute = false,
        RedirectStandardOutput = false,
        RedirectStandardError = false
    )
    
    use proc = Process.Start(psi)
    proc.WaitForExit()
    
    if proc.ExitCode <> 0 then
        printError $"Command '{command} {args}' failed with exit code {proc.ExitCode}."
        failwith $"Process execution failed (exit code: {proc.ExitCode})"

// --- Targets ---

let clean () =
    printHeader "CLEANING BUILD ARTIFACTS"
    
    // Clean bin/ and obj/ directories (excluding .git, .vscode-test, node_modules, and .worktrees)
    let cleanDirs = 
        Directory.GetDirectories(rootDir, "bin", SearchOption.AllDirectories)
        |> Array.append (Directory.GetDirectories(rootDir, "obj", SearchOption.AllDirectories))
        |> Array.append [| workerOutDir |]
        |> Array.filter (fun d -> 
            not (d.Contains(".git") || 
                 d.Contains(".vscode-test") || 
                 d.Contains("node_modules") || 
                 d.Contains(".worktrees")))

    for dir in cleanDirs do
        if Directory.Exists(dir) then
            printInfo $"Removing directory: {dir}"
            try Directory.Delete(dir, true) with _ -> ()

    // Remove .vsix packages
    let vsixFiles = Directory.GetFiles(vscodeDir, "*.vsix")
    for f in vsixFiles do
        printInfo $"Removing package: {f}"
        try File.Delete(f) with _ -> ()

    printSuccess "Clean completed successfully!"

let buildBackend () =
    printHeader "1. BUILDING BACKEND (.NET - RELEASE)"
    runProcess rootDir "dotnet" $"build \"{solutionFile}\" -c Release"
    printSuccess "Backend built successfully."

let publishWorker () =
    printHeader "2. PUBLISHING WORKER (F# WORKER - RELEASE)"
    if not (Directory.Exists(workerOutDir)) then
        Directory.CreateDirectory(workerOutDir) |> ignore
        
    runProcess rootDir "dotnet" $"publish \"{workerProject}\" -c Release -o \"{workerOutDir}\""
    printSuccess "Worker published to editors/vscode/dist/worker successfully."

let compileVscode () =
    printHeader "3. COMPILING VS CODE EXTENSION"
    let vscodeDocsDir = Path.Combine(vscodeDir, "docs")
    if not (Directory.Exists(vscodeDocsDir)) then
        Directory.CreateDirectory(vscodeDocsDir) |> ignore

    let syncFiles = [
        "BENCHMARK_REPORT.md"
        "BENCHMARK_REPORT_PT.md"
        "USER_GUIDE.md"
        "USER_GUIDE_PT.md"
        "CHANGELOG.md"
        "CHANGELOG_PT.md"
    ]
    for file in syncFiles do
        let src = Path.Combine(rootDir, "docs", file)
        let dest = Path.Combine(vscodeDocsDir, file)
        if File.Exists(src) then
            File.Copy(src, dest, true)
            printInfo $"Copied docs/{file} to editors/vscode/docs/{file}"

    let docsReadme = Path.Combine(vscodeDocsDir, "README.md")
    let rootReadme = Path.Combine(vscodeDir, "README.md")
    if File.Exists(docsReadme) then
        let content =
            File.ReadAllText(docsReadme)
                .Replace("(USER_GUIDE.md", "(docs/USER_GUIDE.md")
                .Replace("(USER_GUIDE_PT.md", "(docs/USER_GUIDE_PT.md")
                .Replace("(BENCHMARK_REPORT.md", "(docs/BENCHMARK_REPORT.md")
                .Replace("(BENCHMARK_REPORT_PT.md", "(docs/BENCHMARK_REPORT_PT.md")
                .Replace("(README_PT.md", "(docs/README_PT.md")
        File.WriteAllText(rootReadme, content)

    let docsChangelog = Path.Combine(vscodeDocsDir, "CHANGELOG.md")
    let rootChangelog = Path.Combine(vscodeDir, "CHANGELOG.md")
    if File.Exists(docsChangelog) then
        let content =
            File.ReadAllText(docsChangelog)
                .Replace("(CHANGELOG_PT.md", "(docs/CHANGELOG_PT.md")
        File.WriteAllText(rootChangelog, content)

    runProcess vscodeDir "npm" "run compile"
    printSuccess "VS Code extension compiled successfully."

let testBackend () =
    printHeader "RUNNING BACKEND TESTS (.NET)"
    runProcess rootDir "dotnet" $"test \"{solutionFile}\" -c Release --no-build"
    printSuccess "Backend tests passed with 100% success."

let testVscode () =
    printHeader "RUNNING EXTENSION TESTS (VS CODE)"
    runProcess vscodeDir "npm" "test"
    printSuccess "Extension tests passed successfully."

let packageVscode () =
    printHeader "4. PACKAGING VS CODE EXTENSION (.VSIX)"
    runProcess vscodeDir "npm" "run package"
    
    if File.Exists(vsixOutputDir) then
        let fileInfo = FileInfo(vsixOutputDir)
        let sizeKb = double fileInfo.Length / 1024.0
        printSuccess $"Extension packaged successfully: {vsixOutputDir} ({sizeKb:F2} KB)"
    else
        failwith $"File {vsixOutputDir} was not generated."

// --- Target Aggregators ---

let targetBuild () =
    let sw = Stopwatch.StartNew()
    buildBackend ()
    publishWorker ()
    compileVscode ()
    sw.Stop()
    printSuccess $"Target 'build' completed in {sw.Elapsed.TotalSeconds:F2}s"

let targetRelease () =
    let sw = Stopwatch.StartNew()
    targetBuild ()
    packageVscode ()
    sw.Stop()
    printSuccess $"Target 'release' completed successfully in {sw.Elapsed.TotalSeconds:F2}s"

let targetTest () =
    let sw = Stopwatch.StartNew()
    testBackend ()
    testVscode ()
    sw.Stop()
    printSuccess $"Target 'test' completed successfully in {sw.Elapsed.TotalSeconds:F2}s"

let targetAll () =
    let sw = Stopwatch.StartNew()
    clean ()
    targetBuild ()
    targetTest ()
    packageVscode ()
    sw.Stop()
    printSuccess $"Target 'all' completed successfully in {sw.Elapsed.TotalSeconds:F2}s"

// --- Entry Point ---
let args = fsi.CommandLineArgs |> Array.skip 1

let target = 
    if args.Length > 0 then
        args.[0].ToLowerInvariant()
    else
        "build"

let showHelp () =
    Console.WriteLine("Usage: dotnet fsi build.fsx [target]")
    Console.WriteLine("")
    Console.WriteLine("Available targets:")
    Console.WriteLine("  build    : Compile backend (.NET) and VS Code extension in Release mode (default)")
    Console.WriteLine("  release  : Run full build and package the extension VSIX")
    Console.WriteLine("  test     : Run all backend and VS Code tests")
    Console.WriteLine("  clean    : Clean build binaries and generated artifacts")
    Console.WriteLine("  all      : Run clean -> build -> test -> release")

let executeTarget (tgt: string) =
    match tgt with
    | "build" -> targetBuild ()
    | "release" | "package" -> targetRelease ()
    | "test" -> targetTest ()
    | "clean" -> clean ()
    | "all" -> targetAll ()
    | "--help" | "-h" | "help" -> showHelp ()
    | unknown ->
        printError $"Unknown target: '{unknown}'"
        showHelp ()
        exit 1

try
    executeTarget target
with ex ->
    printError $"Execution failed: {ex.Message}"
    exit 1
