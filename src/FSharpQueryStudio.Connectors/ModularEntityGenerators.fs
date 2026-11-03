namespace FSharpQueryStudio.Connectors

open System
open System.Threading
open FSharpQueryStudio.Protocol

type SqlEntityGenerator() =
    interface IConnectorEntityGenerator with
        member _.Category = ConnectorCategory.Sql
        member _.EmitModuleCode(connectionName: string, connector: IConnector, uniqueModuleName: string) : string =
            match connector with
            | :? IQueryableConnector as qc ->
                let dbConn = Connections.DatabaseConnection(connectionName, qc)
                dbConn.GenerateEntities(uniqueModuleName)
            | _ -> ""

        member _.EmitExtensionCode(connectionName: string, uniqueModuleName: string, propertyName: string) : string =
            $"        member _.{CodeGenerationHelpers.escapeIdent propertyName} : {uniqueModuleName}.Database = {uniqueModuleName}.Database(\"{connectionName}\")"

type CsvEntityGenerator() =
    interface IConnectorEntityGenerator with
        member _.Category = ConnectorCategory.TabularFile
        member _.EmitModuleCode(connectionName: string, connector: IConnector, uniqueModuleName: string) : string =
            match connector with
            | :? CsvConnector as cc ->
                let qr = cc.ReadRows()
                let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
                let sanitizedCols = CodeGenerationHelpers.sanitizeColumns qr.Columns

                let sb = System.Text.StringBuilder()
                sb.AppendLine($"module {uniqueModuleName} =") |> ignore
                sb.AppendLine("    type Row =") |> ignore
                sb.AppendLine("        {") |> ignore
                if List.isEmpty sanitizedCols then
                    sb.AppendLine("            Value: string") |> ignore
                else
                    for idx, col in List.indexed sanitizedCols do
                        let colType =
                            match cc.Options.ColumnTypes.TryFind col with
                            | Some t -> t
                            | None ->
                                match cc.Options.ColumnTypes.TryFind qr.Columns.[idx] with
                                | Some t -> t
                                | None ->
                                    let samples =
                                        rowsSample
                                        |> Seq.map (fun r ->
                                            if idx < r.Length && not (isNull r.[idx]) then
                                                Convert.ToString(r.[idx], System.Globalization.CultureInfo.InvariantCulture)
                                            else
                                                "")
                                    TabularTypeInference.inferType samples
                        sb.AppendLine($"            {CodeGenerationHelpers.escapeIdent col}: {colType}") |> ignore
                sb.AppendLine("        }") |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.CsvConnection) =") |> ignore
                sb.AppendLine("        let queryable = conn.Queryable<Row>()") |> ignore
                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.getCsvConnection(name))") |> ignore
                sb.AppendLine($"        new() = Data(\"{connectionName}\")") |> ignore
                sb.AppendLine("        member _.Rows : System.Linq.IQueryable<Row> = queryable") |> ignore
                sb.AppendLine("        member _.Queryable : System.Linq.IQueryable<Row> = queryable") |> ignore
                sb.AppendLine("        member _.Raw() : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.Raw()") |> ignore
                sb.AppendLine("        member _.Connection : FSharpQueryStudio.Connectors.Connections.CsvConnection = conn") |> ignore
                sb.AppendLine("        interface System.Linq.IQueryable<Row> with") |> ignore
                sb.AppendLine("            member _.ElementType = (queryable :> System.Linq.IQueryable).ElementType") |> ignore
                sb.AppendLine("            member _.Expression = (queryable :> System.Linq.IQueryable).Expression") |> ignore
                sb.AppendLine("            member _.Provider = (queryable :> System.Linq.IQueryable).Provider") |> ignore
                sb.AppendLine("        interface System.Collections.Generic.IEnumerable<Row> with") |> ignore
                sb.AppendLine("            member _.GetEnumerator() = (queryable :> System.Collections.Generic.IEnumerable<Row>).GetEnumerator()") |> ignore
                sb.AppendLine("        interface System.Collections.IEnumerable with") |> ignore
                sb.AppendLine("            member _.GetEnumerator() = (queryable :> System.Collections.IEnumerable).GetEnumerator()") |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("    let rows () : System.Linq.IQueryable<Row> = (Data()).Rows") |> ignore
                sb.AppendLine("    let data () : Data = Data()") |> ignore
                sb.ToString()
            | _ -> ""

        member _.EmitExtensionCode(connectionName: string, uniqueModuleName: string, propertyName: string) : string =
            $"        member _.{CodeGenerationHelpers.escapeIdent propertyName} : {uniqueModuleName}.Data = {uniqueModuleName}.Data(\"{connectionName}\")"

type ExcelEntityGenerator() =
    interface IConnectorEntityGenerator with
        member _.Category = ConnectorCategory.Excel
        member _.EmitModuleCode(connectionName: string, connector: IConnector, uniqueModuleName: string) : string =
            match connector with
            | :? ExcelConnector as ec ->
                let tables = (ec :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result
                let sb = System.Text.StringBuilder()
                sb.AppendLine($"module {uniqueModuleName} =") |> ignore
                let seenSheets = System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
                let sheetEntries =
                    tables
                    |> List.map (fun tbl ->
                        let rawClean = CodeGenerationHelpers.cleanIdentifier tbl.Name
                        let baseClean = if String.IsNullOrWhiteSpace rawClean then "Sheet" else rawClean
                        let mutable propIdent = baseClean
                        let mutable counter = 2
                        while seenSheets.Contains propIdent do
                            propIdent <- $"{baseClean}_{counter}"
                            counter <- counter + 1
                        seenSheets.Add propIdent |> ignore
                        // Avoid F# type/module collision inside `type Data` when a worksheet is named "Data"
                        let modIdent =
                            if String.Equals(propIdent, "Data", StringComparison.OrdinalIgnoreCase) then
                                "Data_Sheet"
                            else
                                propIdent
                        (tbl.Name, propIdent, modIdent))

                for sheetName, _, modIdent in sheetEntries do
                    let qr = ec.ReadRows(sheetName = sheetName, maxRows = 200)
                    let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
                    let sanitizedCols = CodeGenerationHelpers.sanitizeColumns qr.Columns

                    sb.AppendLine($"    module {CodeGenerationHelpers.escapeIdent modIdent} =") |> ignore
                    sb.AppendLine("        type Row =") |> ignore
                    sb.AppendLine("            {") |> ignore
                    if List.isEmpty sanitizedCols then
                        sb.AppendLine("                Value: string") |> ignore
                    else
                        for idx, col in List.indexed sanitizedCols do
                            let colType =
                                match ec.Options.ColumnTypes.TryFind col with
                                | Some t -> t
                                | None ->
                                    match ec.Options.ColumnTypes.TryFind qr.Columns.[idx] with
                                    | Some t -> t
                                    | None ->
                                        let samples =
                                            rowsSample
                                            |> Seq.map (fun r ->
                                                if idx < r.Length && not (isNull r.[idx]) then
                                                    Convert.ToString(r.[idx], System.Globalization.CultureInfo.InvariantCulture)
                                                else
                                                    "")
                                        TabularTypeInference.inferType samples
                            sb.AppendLine($"                {CodeGenerationHelpers.escapeIdent col}: {colType}") |> ignore
                    sb.AppendLine("            }") |> ignore
                    sb.AppendLine() |> ignore
                    sb.AppendLine($"        let rows () : System.Linq.IQueryable<Row> = (FSharpQueryStudio.Connectors.Connections.getExcelConnection(\"{connectionName}\")).SheetQueryable<Row>(\"{sheetName}\")") |> ignore
                    sb.AppendLine() |> ignore

                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.ExcelConnection) =") |> ignore
                match sheetEntries with
                | (firstSheetName, _, firstModIdent) :: _ ->
                    sb.AppendLine($"        let defaultQueryable = conn.SheetQueryable<{CodeGenerationHelpers.escapeIdent firstModIdent}.Row>(\"{firstSheetName}\")") |> ignore
                | [] -> ()
                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.getExcelConnection(name))") |> ignore
                sb.AppendLine($"        new() = Data(\"{connectionName}\")") |> ignore
                sb.AppendLine("        member _.Raw(?maxRows: int) : FSharpQueryStudio.Connectors.ConnectorQueryResult = conn.Raw(?maxRows = maxRows)") |> ignore
                sb.AppendLine("        member _.Connection : FSharpQueryStudio.Connectors.Connections.ExcelConnection = conn") |> ignore
                for sheetName, propIdent, modIdent in sheetEntries do
                    sb.AppendLine($"        member _.{CodeGenerationHelpers.escapeIdent propIdent} : System.Linq.IQueryable<{CodeGenerationHelpers.escapeIdent modIdent}.Row> = conn.SheetQueryable<{CodeGenerationHelpers.escapeIdent modIdent}.Row>(\"{sheetName}\")") |> ignore
                match sheetEntries with
                | (_, _, firstModIdent) :: _ ->
                    let escMod = CodeGenerationHelpers.escapeIdent firstModIdent
                    sb.AppendLine($"        member _.Rows : System.Linq.IQueryable<{escMod}.Row> = defaultQueryable") |> ignore
                    sb.AppendLine($"        interface System.Linq.IQueryable<{escMod}.Row> with") |> ignore
                    sb.AppendLine("            member _.ElementType = (defaultQueryable :> System.Linq.IQueryable).ElementType") |> ignore
                    sb.AppendLine("            member _.Expression = (defaultQueryable :> System.Linq.IQueryable).Expression") |> ignore
                    sb.AppendLine("            member _.Provider = (defaultQueryable :> System.Linq.IQueryable).Provider") |> ignore
                    sb.AppendLine($"        interface System.Collections.Generic.IEnumerable<{escMod}.Row> with") |> ignore
                    sb.AppendLine($"            member _.GetEnumerator() = (defaultQueryable :> System.Collections.Generic.IEnumerable<{escMod}.Row>).GetEnumerator()") |> ignore
                    sb.AppendLine("        interface System.Collections.IEnumerable with") |> ignore
                    sb.AppendLine("            member _.GetEnumerator() = (defaultQueryable :> System.Collections.IEnumerable).GetEnumerator()") |> ignore
                | [] -> ()
                sb.AppendLine() |> ignore
                match sheetEntries with
                | (_, _, firstModIdent) :: _ ->
                    let escMod = CodeGenerationHelpers.escapeIdent firstModIdent
                    sb.AppendLine($"    let rows () : System.Linq.IQueryable<{escMod}.Row> = (Data()).Rows") |> ignore
                | [] -> ()
                sb.AppendLine("    let data () : Data = Data()") |> ignore
                sb.ToString()
            | _ -> ""

        member _.EmitExtensionCode(connectionName: string, uniqueModuleName: string, propertyName: string) : string =
            $"        member _.{CodeGenerationHelpers.escapeIdent propertyName} : {uniqueModuleName}.Data = {uniqueModuleName}.Data(\"{connectionName}\")"

open System.Globalization
open System.Text.Json

module JsonTypeInference =
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
                        | true, v when v.ValueKind <> JsonValueKind.Null && v.ValueKind <> JsonValueKind.Undefined -> Some v
                        | _ -> None)

                let inferred =
                    if values.IsEmpty then
                        "string"
                    elif values |> List.forall (fun v -> v.ValueKind = JsonValueKind.Number && (let ok, _ = v.TryGetInt32() in ok)) then
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

                (propName, inferred))

type RestEntityGenerator() =
    interface IConnectorEntityGenerator with
        member _.Category = ConnectorCategory.Rest
        member _.EmitModuleCode(connectionName: string, connector: IConnector, uniqueModuleName: string) : string =
            match connector with
            | :? RestConnector as rc ->
                let elem = (rc.SendAsync rc.BaseOptions CancellationToken.None).Result
                let props = JsonTypeInference.inferJsonTypes elem

                let sb = System.Text.StringBuilder()
                sb.AppendLine($"module {uniqueModuleName} =") |> ignore
                sb.AppendLine("    type Item =") |> ignore
                sb.AppendLine("        {") |> ignore
                if List.isEmpty props then
                    sb.AppendLine("            Value: string") |> ignore
                else
                    for propNameItem, propType in props do
                        sb.AppendLine($"            {CodeGenerationHelpers.escapeIdent propNameItem}: {propType}") |> ignore
                sb.AppendLine("        }") |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("    type Data(conn: FSharpQueryStudio.Connectors.Connections.RestConnection) =") |> ignore
                sb.AppendLine("        let queryable = conn.Queryable<Item>()") |> ignore
                sb.AppendLine($"        new(name: string) = Data(FSharpQueryStudio.Connectors.Connections.getRestConnection(name))") |> ignore
                sb.AppendLine($"        new() = Data(\"{connectionName}\")") |> ignore
                sb.AppendLine("        member _.Rows : System.Linq.IQueryable<Item> = queryable") |> ignore
                sb.AppendLine("        member _.Queryable : System.Linq.IQueryable<Item> = queryable") |> ignore
                sb.AppendLine("        member _.Items(?options: FSharpQueryStudio.Connectors.RestRequestOptions) : System.Linq.IQueryable<Item> = conn.Queryable<Item>(?options = options)") |> ignore
                sb.AppendLine("        member _.SendAsync(options: FSharpQueryStudio.Connectors.RestRequestOptions, ?ct: System.Threading.CancellationToken) : System.Threading.Tasks.Task<System.Text.Json.JsonElement> = conn.SendAsync(options, ?ct = ct)") |> ignore
                sb.AppendLine("        member _.Connection : FSharpQueryStudio.Connectors.Connections.RestConnection = conn") |> ignore
                sb.AppendLine("        interface System.Linq.IQueryable<Item> with") |> ignore
                sb.AppendLine("            member _.ElementType = (queryable :> System.Linq.IQueryable).ElementType") |> ignore
                sb.AppendLine("            member _.Expression = (queryable :> System.Linq.IQueryable).Expression") |> ignore
                sb.AppendLine("            member _.Provider = (queryable :> System.Linq.IQueryable).Provider") |> ignore
                sb.AppendLine("        interface System.Collections.Generic.IEnumerable<Item> with") |> ignore
                sb.AppendLine("            member _.GetEnumerator() = (queryable :> System.Collections.Generic.IEnumerable<Item>).GetEnumerator()") |> ignore
                sb.AppendLine("        interface System.Collections.IEnumerable with") |> ignore
                sb.AppendLine("            member _.GetEnumerator() = (queryable :> System.Collections.IEnumerable).GetEnumerator()") |> ignore
                sb.AppendLine() |> ignore
                sb.AppendLine("    let items () : System.Linq.IQueryable<Item> = (Data()).Items()") |> ignore
                sb.AppendLine("    let itemsWithOptions (options: FSharpQueryStudio.Connectors.RestRequestOptions) : System.Linq.IQueryable<Item> = (Data()).Items(options)") |> ignore
                sb.AppendLine("    let rows () : System.Linq.IQueryable<Item> = (Data()).Rows") |> ignore
                sb.AppendLine("    let data () : Data = Data()") |> ignore
                sb.ToString()
            | _ -> ""

        member _.EmitExtensionCode(connectionName: string, uniqueModuleName: string, propertyName: string) : string =
            $"        member _.{CodeGenerationHelpers.escapeIdent propertyName} : {uniqueModuleName}.Data = {uniqueModuleName}.Data(\"{connectionName}\")"

type CustomDescriptorWithGenerator(inner: IConnectorDescriptor, generator: IConnectorEntityGenerator) =
    interface IConnectorDescriptor with
        member _.KindId = inner.KindId
        member _.DisplayName = inner.DisplayName
        member _.Category = inner.Category
        member _.Icon = inner.Icon
        member _.FormFields = inner.FormFields
        member _.ValidateConfiguration(configuration: JsonElement) = inner.ValidateConfiguration configuration
        member _.Factory = inner.Factory
        member _.EntityGenerator = Some generator
