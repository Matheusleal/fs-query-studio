namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Serilog
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

module EntityBackgroundGenerator =

    let generatedDir (workspaceDir: string) : string =
        Path.Combine(workspaceDir, "generated")

    let generatedFilePath (workspaceDir: string) : string =
        Path.Combine(generatedDir workspaceDir, "GeneratedEntities.fs")

    let cleanIdentifier (name: string) : string =
        let cleaned = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_")
        if Regex.IsMatch(cleaned, @"^[0-9]") then $"_{cleaned}" else cleaned

    let private keywords =
        set [ "abstract"; "and"; "as"; "assert"; "base"; "begin"; "class"; "default"; "delegate";
              "do"; "done"; "downcast"; "downto"; "elif"; "else"; "end"; "exception"; "extern";
              "false"; "finally"; "fixed"; "for"; "fun"; "function"; "global"; "if"; "in";
              "inherit"; "inline"; "interface"; "internal"; "lazy"; "let"; "match"; "member";
              "module"; "mutable"; "namespace"; "new"; "not"; "null"; "of"; "open"; "or";
              "override"; "private"; "public"; "rec"; "return"; "static"; "struct"; "then";
              "to"; "true"; "try"; "type"; "upcast"; "use"; "val"; "void"; "when"; "while";
              "with"; "yield"; "atomic"; "break"; "checked"; "component"; "const"; "constraint";
              "constructor"; "continue"; "eager"; "event"; "external"; "functor"; "include";
              "method"; "mixin"; "object"; "parallel"; "process"; "protected"; "pure"; "sealed";
              "tailcall"; "trait"; "virtual"; "volatile" ]

    let escapeIdent (s: string) =
        if Regex.IsMatch(s, @"^[a-zA-Z][a-zA-Z0-9_]*$") && not (Set.contains s keywords) then
            s
        else
            $"``{s}``"

    let cleanSample (s: string) : string = CsvTypeInference.cleanSample s

    /// Infers an F# type from a collection of sample string values (up to 200 rows).
    /// When sentinel values ("N/A", "-", "null") or empty values in non-string columns are encountered,
    /// the inferred type becomes an option type (e.g. `decimal option`, `int option`, `string option`).
    let inferType (samples: string seq) : string = CsvTypeInference.inferType samples

    /// Sanitizes and deduplicates column headers so they form valid, unique F# record fields.
    let sanitizeColumns (cols: string list) : string list =
        let seen = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        cols
        |> List.mapi (fun idx col ->
            let baseCol =
                if String.IsNullOrWhiteSpace col then $"Column{idx + 1}"
                else col.Trim()
            let mutable candidate = baseCol
            let mutable counter = 2
            while seen.Contains candidate do
                candidate <- $"{baseCol}_{counter}"
                counter <- counter + 1
            seen.Add candidate |> ignore
            candidate
        )

    /// Infers F# property names and types from a JSON element (array or object).
    let inferJsonTypes (elem: JsonElement) : (string * string) list =
        let sampleObjects =
            if elem.ValueKind = JsonValueKind.Array then
                elem.EnumerateArray()
                |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Object)
                |> Seq.truncate 50
                |> Seq.toList
            elif elem.ValueKind = JsonValueKind.Object then
                let arrayProp =
                    elem.EnumerateObject()
                    |> Seq.tryFind (fun p -> p.Value.ValueKind = JsonValueKind.Array)
                match arrayProp with
                | Some p ->
                    p.Value.EnumerateArray()
                    |> Seq.filter (fun e -> e.ValueKind = JsonValueKind.Object)
                    |> Seq.truncate 50
                    |> Seq.toList
                | None -> [ elem ]
            else
                []

        if List.isEmpty sampleObjects then
            []
        else
            let allProps =
                sampleObjects
                |> Seq.collect (fun o -> o.EnumerateObject() |> Seq.map (fun p -> p.Name))
                |> Seq.distinct
                |> Seq.toList

            allProps
            |> List.map (fun propName ->
                let values =
                    sampleObjects
                    |> List.choose (fun o ->
                        match o.TryGetProperty(propName) with
                        | true, v -> Some v
                        | false, _ -> None)

                let inferred =
                    if values |> List.forall (fun v -> v.ValueKind = JsonValueKind.Number && (let ok, _ = v.TryGetInt32() in ok)) then
                        "int"
                    elif values |> List.forall (fun v -> v.ValueKind = JsonValueKind.Number && (let ok, _ = v.TryGetInt64() in ok)) then
                        "int64"
                    elif values |> List.forall (fun v -> v.ValueKind = JsonValueKind.Number && (let ok, _ = v.TryGetDecimal() in ok)) then
                        "decimal"
                    elif values |> List.forall (fun v -> v.ValueKind = JsonValueKind.Number) then
                        "double"
                    elif values |> List.forall (fun v -> v.ValueKind = JsonValueKind.True || v.ValueKind = JsonValueKind.False) then
                        "bool"
                    elif values |> List.forall (fun v ->
                        if v.ValueKind = JsonValueKind.String then
                            match DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                            | true, _ -> true | false, _ -> false
                        else false) then
                        "System.DateTime"
                    elif values |> List.forall (fun v ->
                        if v.ValueKind = JsonValueKind.String then
                            match Guid.TryParse(v.GetString()) with
                            | true, _ -> true | false, _ -> false
                        else false) then
                        "System.Guid"
                    else
                        "string"

                (propName, inferred)
            )

    /// Generates strongly-typed entity modules and connection extension properties
    /// for all accessible connections (SQL DB, CSV, Excel, and REST/JSON) in the workspace.
    let generateForConnectionsAsync
        (workspaceDir: string)
        (connections: ConnectionDefinition list)
        (secretStore: ISecretStore)
        : Task<bool> =
        task {
            try
                if List.isEmpty connections then
                    return false
                else
                    let resolver = WorkspaceConnectionResolver(connections, secretStore)

                    let sb = System.Text.StringBuilder()
                    sb.AppendLine("// <auto-generated>") |> ignore
                    sb.AppendLine("// This file is automatically generated in the background by FS Query Studio.") |> ignore
                    sb.AppendLine("// Supports strongly-typed LINQ queries across SQL, CSV, Excel and JSON connections.") |> ignore
                    sb.AppendLine("// Do not edit this file manually.") |> ignore
                    sb.AppendLine("// </auto-generated>") |> ignore
                    sb.AppendLine() |> ignore
                    sb.AppendLine("namespace FSharpQueryStudio.Generated") |> ignore
                    sb.AppendLine() |> ignore
                    sb.AppendLine("open System.Linq") |> ignore
                    sb.AppendLine("open FSharpQueryStudio.Connectors") |> ignore
                    sb.AppendLine() |> ignore

                    let usedModuleNames = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let makeUniqueModuleName (baseName: string) =
                        let mutable candidate = baseName
                        let mutable counter = 2
                        while usedModuleNames.Contains candidate do
                            candidate <- $"{baseName}_{counter}"
                            counter <- counter + 1
                        usedModuleNames.Add candidate |> ignore
                        candidate

                    let usedPropNames = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    let makeUniquePropName (baseName: string) =
                        let mutable candidate = baseName
                        let mutable counter = 2
                        while usedPropNames.Contains candidate do
                            candidate <- $"{baseName}_{counter}"
                            counter <- counter + 1
                        usedPropNames.Add candidate |> ignore
                        candidate

                    let mutable dbExtensions: (string * string * string) list = []
                    let mutable csvExtensions: (string * string * string) list = []
                    let mutable excelExtensions: (string * string * string) list = []
                    let mutable restExtensions: (string * string * string) list = []

                    for def in connections do
                        try
                            let cleanName = cleanIdentifier def.Name
                            let rawConnector = (resolver :> IConnectionResolver).Resolve def.Name

                            match rawConnector with
                            | :? IQueryableConnector as qc ->
                                let moduleName = makeUniqueModuleName $"{cleanName}Db"
                                let propName = makeUniquePropName cleanName
                                let dbConn = Connections.DatabaseConnection(def.Name, qc)
                                let code = dbConn.GenerateEntities(moduleName)
                                sb.AppendLine(code) |> ignore
                                sb.AppendLine() |> ignore
                                dbExtensions <- (propName, moduleName, def.Name) :: dbExtensions

                            | :? CsvConnector as cc ->
                                let moduleName = makeUniqueModuleName $"{cleanName}Csv"
                                let propName = makeUniquePropName cleanName
                                let qr = cc.ReadRows()
                                let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
                                let sanitizedCols = sanitizeColumns qr.Columns

                                sb.AppendLine($"module {moduleName} =") |> ignore
                                sb.AppendLine("    type Row =") |> ignore
                                sb.AppendLine("        {") |> ignore
                                for idx, col in List.indexed sanitizedCols do
                                    let samples = rowsSample |> Seq.map (fun r -> if idx < r.Length then string r.[idx] else "")
                                    let colType = inferType samples
                                    sb.AppendLine($"            {escapeIdent col}: {colType}") |> ignore
                                sb.AppendLine("        }") |> ignore
                                sb.AppendLine() |> ignore
                                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.CsvConnection) =") |> ignore
                                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.connectCsv(name))") |> ignore
                                sb.AppendLine($"        new() = Data(\"{def.Name}\")") |> ignore
                                sb.AppendLine("        member _.Rows : System.Linq.IQueryable<Row> = conn.Queryable<Row>()") |> ignore
                                sb.AppendLine("        member _.Queryable : System.Linq.IQueryable<Row> = conn.Queryable<Row>()") |> ignore
                                sb.AppendLine() |> ignore
                                sb.AppendLine("    let rows () : System.Linq.IQueryable<Row> = (Data()).Rows") |> ignore
                                sb.AppendLine("    let data () : Data = Data()") |> ignore
                                sb.AppendLine() |> ignore
                                csvExtensions <- (propName, moduleName, def.Name) :: csvExtensions

                            | :? ExcelConnector as ec ->
                                let moduleName = makeUniqueModuleName $"{cleanName}Excel"
                                let propName = makeUniquePropName cleanName
                                let tables = (ec :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result

                                sb.AppendLine($"module {moduleName} =") |> ignore
                                for tbl in tables do
                                    let sheetClean = cleanIdentifier tbl.Name
                                    let qr = ec.ReadRows(sheetName = tbl.Name, maxRows = 200)
                                    let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
                                    let sanitizedCols = sanitizeColumns qr.Columns

                                    sb.AppendLine($"    module {sheetClean} =") |> ignore
                                    sb.AppendLine("        type Row =") |> ignore
                                    sb.AppendLine("            {") |> ignore
                                    for idx, col in List.indexed sanitizedCols do
                                        let samples = rowsSample |> Seq.map (fun r -> if idx < r.Length then string r.[idx] else "")
                                        let colType = inferType samples
                                        sb.AppendLine($"                {escapeIdent col}: {colType}") |> ignore
                                    sb.AppendLine("            }") |> ignore
                                    sb.AppendLine() |> ignore
                                    sb.AppendLine($"        let rows () : System.Linq.IQueryable<Row> = (FSharpQueryStudio.Connectors.Connections.queryExcelSheet<Row> (\"{def.Name}\") (Some \"{tbl.Name}\") None).AsQueryable()") |> ignore
                                    sb.AppendLine() |> ignore

                                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.ExcelConnection) =") |> ignore
                                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.connectExcel(name))") |> ignore
                                sb.AppendLine($"        new() = Data(\"{def.Name}\")") |> ignore
                                for tbl in tables do
                                    let sheetClean = cleanIdentifier tbl.Name
                                    sb.AppendLine($"        member _.{escapeIdent sheetClean} : System.Linq.IQueryable<{sheetClean}.Row> = conn.SheetQueryable<{sheetClean}.Row>(\"{tbl.Name}\")") |> ignore
                                sb.AppendLine() |> ignore
                                sb.AppendLine("    let data () : Data = Data()") |> ignore
                                sb.AppendLine() |> ignore
                                excelExtensions <- (propName, moduleName, def.Name) :: excelExtensions

                            | :? RestConnector as rc ->
                                let moduleName = makeUniqueModuleName $"{cleanName}Json"
                                let propName = makeUniquePropName cleanName
                                let elem = (rc.SendAsync rc.BaseOptions CancellationToken.None).Result
                                let props = inferJsonTypes elem

                                sb.AppendLine($"module {moduleName} =") |> ignore
                                sb.AppendLine("    type Item =") |> ignore
                                sb.AppendLine("        {") |> ignore
                                for propNameItem, propType in props do
                                    sb.AppendLine($"            {escapeIdent propNameItem}: {propType}") |> ignore
                                sb.AppendLine("        }") |> ignore
                                sb.AppendLine() |> ignore
                                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.RestConnection) =") |> ignore
                                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.connectRest(name))") |> ignore
                                sb.AppendLine($"        new() = Data(\"{def.Name}\")") |> ignore
                                sb.AppendLine("        member _.Items(?options: FSharpQueryStudio.Connectors.RestRequestOptions) : System.Linq.IQueryable<Item> = conn.Queryable<Item>(?options = options)") |> ignore
                                sb.AppendLine() |> ignore
                                sb.AppendLine("    let items (?options: FSharpQueryStudio.Connectors.RestRequestOptions) : System.Linq.IQueryable<Item> = (Data()).Items(?options = options)") |> ignore
                                sb.AppendLine("    let data () : Data = Data()") |> ignore
                                sb.AppendLine() |> ignore
                                restExtensions <- (propName, moduleName, def.Name) :: restExtensions

                            | _ -> ()
                        with ex ->
                            Log.Warning("FS Query Studio: Could not generate background entities for connection '{Name}': {Error}", def.Name, ex.Message)

                    let totalExtensions = dbExtensions.Length + csvExtensions.Length + excelExtensions.Length + restExtensions.Length

                    if totalExtensions > 0 then
                        sb.AppendLine("[<AutoOpen>]") |> ignore
                        sb.AppendLine("module Extensions =") |> ignore

                        if not (List.isEmpty dbExtensions) then
                            sb.AppendLine("    type FSharpQueryStudio.Connectors.Connections.DatabaseConnection with") |> ignore
                            for (propName, modName, _) in List.rev dbExtensions do
                                sb.AppendLine($"        member this.{propName} = {modName}.Database(this)") |> ignore
                            if dbExtensions.Length = 1 then
                                let (_, singleModName, _) = dbExtensions.[0]
                                sb.AppendLine($"        member this.asEntities = {singleModName}.Database(this)") |> ignore
                            else
                                sb.AppendLine("        member this.asEntities : obj =") |> ignore
                                sb.AppendLine("            match this.Name with") |> ignore
                                for (_, modName, origName) in List.rev dbExtensions do
                                    let escapedOrig = origName.Replace("\"", "\\\"")
                                    sb.AppendLine($"            | \"{escapedOrig}\" -> box ({modName}.Database(this))") |> ignore
                                let (_, firstMod, _) = dbExtensions.[dbExtensions.Length - 1]
                                sb.AppendLine($"            | _ -> box ({firstMod}.Database(this))") |> ignore
                            sb.AppendLine() |> ignore

                        if not (List.isEmpty csvExtensions) then
                            sb.AppendLine("    type FSharpQueryStudio.Connectors.Connections.CsvConnection with") |> ignore
                            for (propName, modName, _) in List.rev csvExtensions do
                                sb.AppendLine($"        member this.{propName} = {modName}.Data(this)") |> ignore
                            if csvExtensions.Length = 1 then
                                let (_, singleModName, _) = csvExtensions.[0]
                                sb.AppendLine($"        member this.asEntities = {singleModName}.Data(this)") |> ignore
                            else
                                sb.AppendLine("        member this.asEntities : obj =") |> ignore
                                sb.AppendLine("            match this.Name with") |> ignore
                                for (_, modName, origName) in List.rev csvExtensions do
                                    let escapedOrig = origName.Replace("\"", "\\\"")
                                    sb.AppendLine($"            | \"{escapedOrig}\" -> box ({modName}.Data(this))") |> ignore
                                let (_, firstMod, _) = csvExtensions.[csvExtensions.Length - 1]
                                sb.AppendLine($"            | _ -> box ({firstMod}.Data(this))") |> ignore
                            sb.AppendLine() |> ignore

                        if not (List.isEmpty excelExtensions) then
                            sb.AppendLine("    type FSharpQueryStudio.Connectors.Connections.ExcelConnection with") |> ignore
                            for (propName, modName, _) in List.rev excelExtensions do
                                sb.AppendLine($"        member this.{propName} = {modName}.Data(this)") |> ignore
                            if excelExtensions.Length = 1 then
                                let (_, singleModName, _) = excelExtensions.[0]
                                sb.AppendLine($"        member this.asEntities = {singleModName}.Data(this)") |> ignore
                            else
                                sb.AppendLine("        member this.asEntities : obj =") |> ignore
                                sb.AppendLine("            match this.Name with") |> ignore
                                for (_, modName, origName) in List.rev excelExtensions do
                                    let escapedOrig = origName.Replace("\"", "\\\"")
                                    sb.AppendLine($"            | \"{escapedOrig}\" -> box ({modName}.Data(this))") |> ignore
                                let (_, firstMod, _) = excelExtensions.[excelExtensions.Length - 1]
                                sb.AppendLine($"            | _ -> box ({firstMod}.Data(this))") |> ignore
                            sb.AppendLine() |> ignore

                        if not (List.isEmpty restExtensions) then
                            sb.AppendLine("    type FSharpQueryStudio.Connectors.Connections.RestConnection with") |> ignore
                            for (propName, modName, _) in List.rev restExtensions do
                                sb.AppendLine($"        member this.{propName} = {modName}.Data(this)") |> ignore
                            if restExtensions.Length = 1 then
                                let (_, singleModName, _) = restExtensions.[0]
                                sb.AppendLine($"        member this.asEntities = {singleModName}.Data(this)") |> ignore
                            else
                                sb.AppendLine("        member this.asEntities : obj =") |> ignore
                                sb.AppendLine("            match this.Name with") |> ignore
                                for (_, modName, origName) in List.rev restExtensions do
                                    let escapedOrig = origName.Replace("\"", "\\\"")
                                    sb.AppendLine($"            | \"{escapedOrig}\" -> box ({modName}.Data(this))") |> ignore
                                let (_, firstMod, _) = restExtensions.[restExtensions.Length - 1]
                                sb.AppendLine($"            | _ -> box ({firstMod}.Data(this))") |> ignore
                            sb.AppendLine() |> ignore



                        let targetDir = generatedDir workspaceDir
                        Directory.CreateDirectory(targetDir) |> ignore
                        let targetPath = generatedFilePath workspaceDir
                        let newContent = sb.ToString()

                        if not (File.Exists targetPath) || File.ReadAllText(targetPath) <> newContent then
                            let tempPath = targetPath + ".tmp"
                            File.WriteAllText(tempPath, newContent)
                            File.Move(tempPath, targetPath, overwrite = true)
                            Log.Information("FS Query Studio: Background entity types generated at {Path} ({Count} connections)", targetPath, totalExtensions)
                            return true
                        else
                            return false
                    else
                        return false
            with ex ->
                Log.Warning("FS Query Studio: Background entity generation failed: {Error}", ex.Message)
                return false
        }

    let mutable private currentGenerationTask: Task = Task.CompletedTask
    let private taskLock = obj()

    /// Triggers generation asynchronously in a background thread without blocking callers.
    let triggerBackgroundGeneration
        (workspaceDir: string)
        (connections: ConnectionDefinition list)
        (secretStore: ISecretStore)
        : unit =
        lock taskLock (fun () ->
            let t = Task.Run(fun () ->
                generateForConnectionsAsync workspaceDir connections secretStore :> Task
            )
            currentGenerationTask <- t
        )

    /// Waits for any in-flight background generation task to complete. Useful for tests and cleanups.
    let waitForPendingGeneration () : unit =
        try
            currentGenerationTask.Wait()
        with _ -> ()

    /// Ensures generated entities exist, generating synchronously on first access or refreshing in background.
    let ensureGenerated
        (workspaceDir: string)
        (connections: ConnectionDefinition list)
        (secretStore: ISecretStore)
        : unit =
        if List.isEmpty connections then ()
        else
            let targetPath = generatedFilePath workspaceDir
            if not (File.Exists targetPath) then
                try
                    (generateForConnectionsAsync workspaceDir connections secretStore).GetAwaiter().GetResult() |> ignore
                with ex ->
                    Log.Warning("FS Query Studio: Initial entity generation failed: {Error}", ex.Message)
            else
                triggerBackgroundGeneration workspaceDir connections secretStore
