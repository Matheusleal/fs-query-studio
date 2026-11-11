namespace FSharpQueryStudio.Connectors

open System
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Xml
open System.Xml.Linq
open FSharpQueryStudio.Protocol

[<AutoOpen>]
module XmlConnectorExtensions =
    type ConnectorKind with
        static member Xml = ConnectorKind("Xml")

    type ConnectorCategory with
        static member Xml = ConnectorCategory("xml", "Xml", "FSharpQueryStudio.Connectors.Connections.XmlCategory")

/// Configuration options for local XML file connections.
/// `RowElement = None` discovers all repeating element collections in the XML document.
type XmlOptions =
    { RowElement: string option
      IncludeAttributes: bool
      FlattenNested: bool
      ColumnTypes: Map<string, string>
      ColumnTransforms: Map<string, string> }

    static member Default =
        { RowElement = None
          IncludeAttributes = true
          FlattenNested = true
          ColumnTypes = Map.empty
          ColumnTransforms = Map.empty }

module private XmlCells =
    let private isNamespaceDeclaration (attr: XAttribute) : bool =
        attr.IsNamespaceDeclaration
        || String.Equals(attr.Name.LocalName, "xmlns", StringComparison.OrdinalIgnoreCase)

    let private nonNamespaceAttributes (el: XElement) : XAttribute list =
        el.Attributes()
        |> Seq.filter (fun a -> not (isNamespaceDeclaration a))
        |> Seq.toList

    let rec flattenElement (prefix: string) (depth: int) (options: XmlOptions) (el: XElement) (acc: ResizeArray<string * obj>) =
        if options.IncludeAttributes then
            for attr in nonNamespaceAttributes el do
                let key =
                    if String.IsNullOrEmpty prefix then attr.Name.LocalName
                    else $"{prefix}_{attr.Name.LocalName}"
                acc.Add(key, box attr.Value)

        if not el.HasElements then
            let rawText = el.Value
            if String.IsNullOrEmpty prefix then
                if (nonNamespaceAttributes el).IsEmpty || not (String.IsNullOrWhiteSpace rawText) then
                    let colName = if (nonNamespaceAttributes el).IsEmpty then "Value" else $"{el.Name.LocalName}_Value"
                    acc.Add(colName, box rawText)
            else
                acc.Add(prefix, box rawText)
        else
            for child in el.Elements() do
                let childKey =
                    if String.IsNullOrEmpty prefix then child.Name.LocalName
                    else $"{prefix}_{child.Name.LocalName}"
                if child.HasElements then
                    if options.FlattenNested && depth < 4 then
                        flattenElement childKey (depth + 1) options child acc
                    else
                        acc.Add(childKey, box (child.ToString(SaveOptions.DisableFormatting)))
                else
                    if options.IncludeAttributes then
                        let childAttrs = nonNamespaceAttributes child
                        for attr in childAttrs do
                            acc.Add($"{childKey}_{attr.Name.LocalName}", box attr.Value)
                        if childAttrs.IsEmpty || not (String.IsNullOrWhiteSpace child.Value) then
                            acc.Add(childKey, box child.Value)
                    else
                        acc.Add(childKey, box child.Value)

    /// Determines whether `container` acts as a wrapper element around repeating record elements
    /// (e.g., `<Products><Product .../><Product .../></Products>`).
    let private isCollectionWrapper (container: XElement) : bool =
        let children = container.Elements() |> Seq.toArray
        if children.Length = 0 then
            false
        elif children.Length > 1 then
            let distinctNames =
                children
                |> Array.map (fun c -> c.Name.LocalName)
                |> Array.distinct
            // Either multiple children share a tag name (e.g. <Product/>, <Product/>)
            // or every child has its own attributes/sub-elements
            distinctNames.Length < children.Length
            || children |> Array.forall (fun c -> c.HasElements || not (nonNamespaceAttributes c).IsEmpty)
        else
            // Single child inside container: wrapper if the child itself has elements or attributes
            let single = children.[0]
            single.HasElements || not (nonNamespaceAttributes single).IsEmpty

    /// Discovers all logical collections `(tableName, rowElements)` in an XML document.
    let discoverCollections (root: XElement) (options: XmlOptions) : (string * XElement array) list =
        match options.RowElement with
        | Some rowTag when not (String.IsNullOrWhiteSpace rowTag) ->
            let segments =
                rowTag.Trim().Split([| '/'; '.' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun s -> s.Trim())
                |> Array.filter (fun s -> not (String.IsNullOrEmpty s))
            if segments.Length = 0 then
                []
            else
                let targetTag = segments.[segments.Length - 1]
                let matches =
                    root.Descendants()
                    |> Seq.filter (fun el -> String.Equals(el.Name.LocalName, targetTag, StringComparison.OrdinalIgnoreCase))
                    |> Seq.toArray
                if matches.Length > 0 then
                    [ (targetTag, matches) ]
                elif String.Equals(root.Name.LocalName, targetTag, StringComparison.OrdinalIgnoreCase) then
                    [ (targetTag, [| root |]) ]
                else
                    []
        | _ ->
            let directChildren = root.Elements() |> Seq.toArray
            if directChildren.Length = 0 then
                [ (root.Name.LocalName, [| root |]) ]
            else
                let groupedDirect =
                    directChildren
                    |> Seq.groupBy (fun el -> el.Name.LocalName)
                    |> Seq.map (fun (tagName, els) -> (tagName, els |> Seq.toArray))
                    |> Seq.toList

                let results = ResizeArray<string * XElement array>()
                for (tagName, els) in groupedDirect do
                    if els.Length > 1 then
                        // Multiple sibling elements with the same tag directly under root (e.g. <Catalog><Book/><Book/></Catalog>)
                        results.Add(tagName, els)
                    else
                        let single = els.[0]
                        if isCollectionWrapper single then
                            // Wrapper element (e.g. <Erp><Products><Product/><Product/></Products></Erp>)
                            let innerChildren = single.Elements() |> Seq.toArray
                            results.Add(tagName, innerChildren)
                        else
                            results.Add(tagName, els)

                results |> Seq.toList

    let projectRows (elements: XElement array) (options: XmlOptions) (maxRows: int option) : ConnectorQueryResult =
        let limit = defaultArg maxRows Int32.MaxValue
        let bounded =
            if elements.Length > limit then Array.sub elements 0 (max 0 limit)
            else elements

        if bounded.Length = 0 then
            { Columns = []; Rows = Seq.empty }
        else
            let colOrder = ResizeArray<string>()
            let colIndexMap = System.Collections.Generic.Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            let flattenedRows = ResizeArray<ResizeArray<string * obj>>(bounded.Length)

            for el in bounded do
                let pairs = ResizeArray<string * obj>()
                flattenElement "" 0 options el pairs
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

/// Local XML file connector with XXE-safe parsing, multi-collection discovery,
/// attribute & child element projection, and SQLite staging support.
type XmlConnector(filePath: string, ?options: XmlOptions) =
    let cleanPath =
        if String.IsNullOrWhiteSpace filePath then ""
        else filePath.Trim().Trim('"', '\'')

    let options = defaultArg options XmlOptions.Default

    let loadRootElement () : XElement option =
        use fs = new FileStream(cleanPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete, 65536)
        let settings =
            XmlReaderSettings(
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true
            )
        use reader = XmlReader.Create(fs, settings)
        let doc = XDocument.Load(reader, LoadOptions.None)
        if isNull doc.Root then None else Some doc.Root

    member _.FilePath = cleanPath
    member _.Options = options

    /// Reads data rows from the target collection/table in the XML document.
    member _.ReadRows(?tableName: string, ?maxRows: int) : ConnectorQueryResult =
        if not (File.Exists cleanPath) then
            { Columns = []; Rows = Seq.empty }
        else
            match loadRootElement () with
            | None -> { Columns = []; Rows = Seq.empty }
            | Some root ->
                let collections = XmlCells.discoverCollections root options
                let targetName =
                    match tableName with
                    | Some n when not (String.IsNullOrWhiteSpace n) -> Some (n.Trim())
                    | _ -> options.RowElement

                let selectedElements =
                    match targetName with
                    | Some name ->
                        collections
                        |> List.tryFind (fun (n, els) ->
                            String.Equals(n, name, StringComparison.OrdinalIgnoreCase)
                            || (els.Length > 0 && String.Equals(els.[0].Name.LocalName, name, StringComparison.OrdinalIgnoreCase)))
                        |> Option.orElseWith (fun () -> collections |> List.tryHead)
                        |> Option.map snd
                        |> Option.defaultValue [||]
                    | None ->
                        collections
                        |> List.tryHead
                        |> Option.map snd
                        |> Option.defaultValue [||]

                XmlCells.projectRows selectedElements options maxRows

    interface IConnector with
        member _.Kind = ConnectorKind.Xml

        member _.TestConnectionAsync(_ct: CancellationToken) : Task<bool> =
            Task.FromResult(File.Exists cleanPath)

        member _.DiscoverSchemaAsync(_ct: CancellationToken) : Task<ConnectorTable list> =
            if not (File.Exists cleanPath) then
                Task.FromResult []
            else
                match loadRootElement () with
                | None -> Task.FromResult []
                | Some root ->
                    let collections = XmlCells.discoverCollections root options
                    let tables =
                        collections
                        |> List.map (fun (tblName, elements) ->
                            let projected = XmlCells.projectRows elements options (Some 200)
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

