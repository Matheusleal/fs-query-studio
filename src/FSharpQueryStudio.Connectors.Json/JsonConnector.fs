namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module JsonConnectorExtensions =
    type ConnectorKind with
        static member Json = ConnectorKind("Json")

    type ConnectorCategory with
        static member Json = ConnectorCategory("json", "Json", "FSharpQueryStudio.Connectors.Connections.JsonCategory")

/// Configuration options for local JSON file connections.
/// `TableName = None` and `JsonPath = None` discover all collections in the JSON file.
type JsonOptions =
    { TableName: string option
      JsonPath: string option
      FlattenNested: bool
      ColumnTypes: Map<string, string>
      ColumnTransforms: Map<string, string> }

    static member Default =
        { TableName = None
          JsonPath = None
          FlattenNested = true
          ColumnTypes = Map.empty
          ColumnTransforms = Map.empty }

module private JsonCells =
    let toBoxedScalar (elem: JsonElement) : obj =
        match elem.ValueKind with
        | JsonValueKind.Null
        | JsonValueKind.Undefined -> null
        | JsonValueKind.True -> box true
        | JsonValueKind.False -> box false
        | JsonValueKind.Number ->
            let rawText = elem.GetRawText()
            if not (rawText.Contains('.') || rawText.Contains('e') || rawText.Contains('E')) then
                match elem.TryGetInt32() with
                | true, i -> box i
                | false, _ ->
                    match elem.TryGetInt64() with
                    | true, l -> box l
                    | false, _ ->
                        match elem.TryGetDouble() with
                        | true, d when Double.IsFinite d -> box d
                        | _ -> box rawText
            else
                match elem.TryGetDouble() with
                | true, d when Double.IsFinite d -> box d
                | _ -> box rawText
        | JsonValueKind.String ->
            box (elem.GetString())
        | JsonValueKind.Array
        | JsonValueKind.Object ->
            box (elem.GetRawText())
        | _ ->
            box (elem.GetRawText())

    let rec flattenObject (prefix: string) (depth: int) (flattenNested: bool) (objElem: JsonElement) (acc: ResizeArray<string * obj>) =
        for prop in objElem.EnumerateObject() do
            let key =
                if String.IsNullOrEmpty prefix then prop.Name
                else $"{prefix}_{prop.Name}"
            if flattenNested && depth < 4 && prop.Value.ValueKind = JsonValueKind.Object then
                flattenObject key (depth + 1) flattenNested prop.Value acc
            else
                acc.Add(key, toBoxedScalar prop.Value)

    let tryNavigatePath (root: JsonElement) (jsonPath: string) : (string * JsonElement) option =
        let segments =
            jsonPath.Trim().Split([| '.'; '/' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun s -> s.Trim())
            |> Array.filter (fun s -> not (String.IsNullOrEmpty s))
        if segments.Length = 0 then
            Some ("Items", root)
        else
            let mutable current = root
            let mutable ok = true
            for seg in segments do
                if ok then
                    if current.ValueKind = JsonValueKind.Object then
                        let found =
                            current.EnumerateObject()
                            |> Seq.tryFind (fun p -> String.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase))
                        match found with
                        | Some p -> current <- p.Value
                        | None -> ok <- false
                    else
                        ok <- false
            if ok then
                Some (segments.[segments.Length - 1], current)
            else
                None

    /// Discovers all logical collections `(tableName, elements)` from a JSON root element.
    let discoverCollections (root: JsonElement) (options: JsonOptions) : (string * JsonElement array) list =
        let extractFromElement (defaultName: string) (target: JsonElement) : (string * JsonElement array) list =
            match target.ValueKind with
            | JsonValueKind.Array ->
                let items = target.EnumerateArray() |> Seq.toArray
                [ (defaultName, items) ]
            | JsonValueKind.Object ->
                let directArrays =
                    target.EnumerateObject()
                    |> Seq.choose (fun p ->
                        if p.Value.ValueKind = JsonValueKind.Array then
                            Some (p.Name, p.Value.EnumerateArray() |> Seq.toArray)
                        else
                            None)
                    |> Seq.toList

                if not directArrays.IsEmpty then
                    directArrays
                else
                    // Check one level deeper for nested container objects holding arrays
                    let nestedArrays =
                        target.EnumerateObject()
                        |> Seq.collect (fun p ->
                            if p.Value.ValueKind = JsonValueKind.Object then
                                p.Value.EnumerateObject()
                                |> Seq.choose (fun child ->
                                    if child.Value.ValueKind = JsonValueKind.Array then
                                        Some (child.Name, child.Value.EnumerateArray() |> Seq.toArray)
                                    else
                                        None)
                            else
                                Seq.empty)
                        |> Seq.toList
                    if not nestedArrays.IsEmpty then
                        nestedArrays
                    else
                        [ (defaultName, [| target |]) ]
            | _ ->
                [ (defaultName, [| target |]) ]

        let fallbackName =
            match options.TableName with
            | Some t when not (String.IsNullOrWhiteSpace t) -> t.Trim()
            | _ -> "Items"

        match options.JsonPath with
        | Some path when not (String.IsNullOrWhiteSpace path) ->
            match tryNavigatePath root path with
            | Some (segName, target) ->
                let effectiveName =
                    match options.TableName with
                    | Some t when not (String.IsNullOrWhiteSpace t) -> t.Trim()
                    | _ -> segName
                extractFromElement effectiveName target
            | None -> []
        | _ ->
            extractFromElement fallbackName root

    let projectRows (items: JsonElement array) (options: JsonOptions) (maxRows: int option) : ConnectorQueryResult =
        let limit = defaultArg maxRows Int32.MaxValue
        let boundedItems =
            if items.Length > limit then Array.sub items 0 (max 0 limit)
            else items

        if boundedItems.Length = 0 then
            { Columns = []; Rows = Seq.empty }
        else
            let hasObjects = boundedItems |> Array.exists (fun e -> e.ValueKind = JsonValueKind.Object)
            if not hasObjects then
                let colName = "Value"
                let hasTransforms = not options.ColumnTransforms.IsEmpty
                let applyTransform (v: obj) =
                    if not hasTransforms then v
                    else
                        match options.ColumnTransforms.TryFind colName with
                        | Some expr -> ColumnTransformation.applyTransform expr v
                        | None -> v
                let rows =
                    boundedItems
                    |> Array.map (fun item -> [| applyTransform (toBoxedScalar item) |])
                { Columns = [ colName ]; Rows = rows :> seq<obj array> }
            else
                let colOrder = ResizeArray<string>()
                let colIndexMap = System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                let flattenedRows = ResizeArray<ResizeArray<string * obj>>(boundedItems.Length)

                for item in boundedItems do
                    let pairs = ResizeArray<string * obj>()
                    if item.ValueKind = JsonValueKind.Object then
                        flattenObject "" 0 options.FlattenNested item pairs
                    else
                        pairs.Add("Value", toBoxedScalar item)
                    for (k, _) in pairs do
                        if not (colIndexMap.ContainsKey k) then
                            colIndexMap.[k] <- colOrder.Count
                            colOrder.Add(k)
                    flattenedRows.Add(pairs)

                let columns = colOrder |> Seq.toList
                let colCount = columns.Length
                let hasTransforms = not options.ColumnTransforms.IsEmpty

                let rows =
                    flattenedRows
                    |> Seq.map (fun pairs ->
                        let arr = Array.zeroCreate<obj> colCount
                        for (k, rawVal) in pairs do
                            match colIndexMap.TryGetValue(k) with
                            | true, idx ->
                                let v =
                                    if hasTransforms then
                                        match options.ColumnTransforms.TryFind columns.[idx] with
                                        | Some expr -> ColumnTransformation.applyTransform expr rawVal
                                        | None -> rawVal
                                    else
                                        rawVal
                                arr.[idx] <- v
                            | false, _ -> ()
                        arr)
                    |> Seq.toArray

                { Columns = columns; Rows = rows :> seq<obj array> }

/// Local JSON file connector supporting multi-collection discovery, nested property flattening,
/// schema inference, and SQLite staging for LINQ pushdown queries.
type JsonConnector(filePath: string, ?options: JsonOptions) =
    let cleanPath =
        if String.IsNullOrWhiteSpace filePath then ""
        else filePath.Trim().Trim('"', '\'')

    let options = defaultArg options JsonOptions.Default

    let parseDocument () : JsonDocument =
        use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        let docOpts = JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip)
        JsonDocument.Parse(fs, docOpts)

    member _.FilePath = cleanPath
    member _.Options = options

    /// Reads data rows from the target collection/table in the JSON file.
    member _.ReadRows(?tableName: string, ?maxRows: int) : ConnectorQueryResult =
        if not (File.Exists cleanPath) then
            { Columns = []; Rows = Seq.empty }
        else
            use doc = parseDocument ()
            let collections = JsonCells.discoverCollections doc.RootElement options
            let targetName =
                match tableName with
                | Some n when not (String.IsNullOrWhiteSpace n) -> Some (n.Trim())
                | _ -> options.TableName

            let selectedItems =
                match targetName with
                | Some name ->
                    collections
                    |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    |> Option.orElseWith (fun () -> collections |> List.tryHead)
                    |> Option.map snd
                    |> Option.defaultValue [||]
                | None ->
                    collections
                    |> List.tryHead
                    |> Option.map snd
                    |> Option.defaultValue [||]

            JsonCells.projectRows selectedItems options maxRows

    interface IConnector with
        member _.Kind = ConnectorKind.Json

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> =
            Task.FromResult(File.Exists cleanPath)

        member _.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            if not (File.Exists cleanPath) then
                Task.FromResult []
            else
                use doc = parseDocument ()
                let collections = JsonCells.discoverCollections doc.RootElement options
                let tables =
                    collections
                    |> List.map (fun (tblName, items) ->
                        let projected = JsonCells.projectRows items options (Some 200)
                        let sampleRows = projected.Rows |> Seq.truncate 200 |> Seq.toList
                        let colsWithTypes =
                            projected.Columns
                            |> List.mapi (fun idx colName ->
                                let explicitType = options.ColumnTypes.TryFind colName
                                let colType =
                                    match explicitType with
                                    | Some t -> t
                                    | None ->
                                        let samples =
                                            sampleRows
                                            |> Seq.map (fun r ->
                                                if idx < r.Length && not (isNull r.[idx]) then
                                                    Convert.ToString(r.[idx], CultureInfo.InvariantCulture)
                                                else
                                                    "")
                                        TabularTypeInference.inferType samples
                                { Name = colName; DataType = colType }: ConnectorColumn)
                        { Name = tblName; Columns = colsWithTypes }: ConnectorTable)
                Task.FromResult tables

