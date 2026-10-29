namespace FSharpQueryStudio.Connectors

open System
open System.Text
open System.Text.Json
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Security

type CsvConnectorFactory() =
    interface IConnectorFactory with
        member _.Create(configuration: JsonElement, _: ISecretStore, _: string option) : IConnector =
            let filePath =
                match JsonElementHelpers.tryGetString "filePath" configuration with
                | Some fp -> fp
                | None ->
                    if configuration.ValueKind = JsonValueKind.String then configuration.GetString()
                    else configuration.GetRawText().Trim('"', '\'')
            let delimiter =
                match JsonElementHelpers.tryGetString "delimiter" configuration with
                | Some d when not (String.IsNullOrEmpty d) -> Some d.[0]
                | _ -> None

            let cleanPath = if String.IsNullOrWhiteSpace filePath then "" else filePath.Trim().Trim('"', '\'')
            let opts =
                match delimiter with
                | Some d -> { CsvOptions.Default with Delimiter = d }
                | None ->
                    let autoDelim = CsvConnector.DetectDelimiter(cleanPath, Encoding.UTF8)
                    { CsvOptions.Default with Delimiter = autoDelim }
            CsvConnector(cleanPath, opts) :> IConnector

type CsvEntityGenerator() =
    interface IConnectorEntityGenerator with
        member _.Category = ConnectorCategory.TabularFile
        member _.EmitModuleCode(connectionName: string, connector: IConnector, uniqueModuleName: string) : string =
            let cc = connector :?> CsvConnector
            let qr = cc.ReadRows()
            let rowsSample = qr.Rows |> Seq.truncate 200 |> Seq.toList
            let sanitizedCols = CodeGenerationHelpers.sanitizeColumns qr.Columns

            let sb = System.Text.StringBuilder()
            sb.AppendLine($"module {uniqueModuleName} =") |> ignore
            sb.AppendLine("    type Row =") |> ignore
            sb.AppendLine("        {") |> ignore
            for idx, col in List.indexed sanitizedCols do
                let samples = rowsSample |> Seq.map (fun r -> if idx < r.Length then string r.[idx] else "")
                let colType = CsvTypeInference.inferType samples
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

        member _.EmitExtensionCode(connectionName: string, uniqueModuleName: string, propertyName: string) : string =
            $"        member _.{CodeGenerationHelpers.escapeIdent propertyName} : {uniqueModuleName}.Data = {uniqueModuleName}.Data(\"{connectionName}\")"

type CsvDescriptor() =
    static let instance = CsvDescriptor()
    static member Instance = instance

    interface IConnectorDescriptor with
        member _.KindId = "Csv"
        member _.DisplayName = "CSV"
        member _.Category = ConnectorCategory.TabularFile
        member _.Icon = "file-text"
        member _.FormFields =
            [ { Name = "filePath"
                Label = "CSV File Path"
                Type = ConnectorFieldType.File
                Required = true
                Placeholder = Some "/path/to/data.csv"
                DefaultValue = None
                SecretHandling = SecretHandling.None }
              { Name = "delimiter"
                Label = "Delimiter"
                Type = ConnectorFieldType.String
                Required = false
                Placeholder = Some "Leave blank for auto-detect (, ; \\t |)"
                DefaultValue = None
                SecretHandling = SecretHandling.None } ]
        member _.Factory = CsvConnectorFactory() :> IConnectorFactory
        member _.EntityGenerator = Some (CsvEntityGenerator() :> IConnectorEntityGenerator)
