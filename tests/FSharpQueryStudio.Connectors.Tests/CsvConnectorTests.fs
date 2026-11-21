module CsvConnectorTests

open System
open System.IO
open System.Text
open System.Threading
open Xunit
open FSharpQueryStudio.Protocol
open FSharpQueryStudio.Connectors

let private tempCsvFile (encoding: Encoding) (content: string) : string =
    let path = Path.Combine(Path.GetTempPath(), "fqs-csv-" + Guid.NewGuid().ToString("N") + ".csv")
    File.WriteAllText(path, content, encoding)
    path

[<Fact>]
let ``ReadRows parses a header row and each data row as columns/rows`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Name,Total\n1,Sales,100.0\n2,Inventory,50.0\n"
    let connector = CsvConnector(path)

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name"; "Total" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal<obj list>([ box "1"; box "Sales"; box "100.0" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "2"; box "Inventory"; box "50.0" ], rows.[1] |> Array.toList)

[<Fact>]
let ``ReadRows's Rows sequence can be enumerated more than once`` () =
    // A caller that materializes `Rows` (e.g. `Seq.toArray`) and then hands
    // the same `ConnectorQueryResult` to something else that also reads
    // `Rows` (e.g. the results projection, then Export CSV) must see the
    // same data twice, not a "closed TextReader" failure from a stream
    // handle only good for one pass.
    let path = tempCsvFile Encoding.UTF8 "Id,Name\n1,Ada\n2,Alan\n"
    let connector = CsvConnector(path)
    let result = connector.ReadRows()

    let firstPass = result.Rows |> Seq.toArray
    let secondPass = result.Rows |> Seq.toArray

    Assert.Equal(2, firstPass.Length)
    Assert.Equal<obj array array>(firstPass, secondPass)

[<Fact>]
let ``ReadRows keeps a quoted field's comma inside a single field, not splitting it`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Description\n1,\"Sales, EMEA\"\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "Sales, EMEA" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows keeps a quoted field's embedded newline inside a single field, not ending the row`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Notes\n1,\"line one\nline two\"\n2,plain\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "line one\nline two" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "2"; box "plain" ], rows.[1] |> Array.toList)

[<Fact>]
let ``ReadRows unescapes a doubled quote inside a quoted field`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Quote\n1,\"She said \"\"hi\"\"\"\n"
    let connector = CsvConnector(path)

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "She said \"hi\"" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows reads a non-UTF8 encoded file correctly when Encoding is set`` () =
    let latin1 = Encoding.GetEncoding("iso-8859-1")
    let path = tempCsvFile latin1 "Id,City\n1,São Paulo\n"
    let connector = CsvConnector(path, { CsvOptions.Default with Encoding = latin1 })

    let rows = connector.ReadRows().Rows |> Seq.toList

    Assert.Equal<obj list>([ box "1"; box "São Paulo" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows generates Column1/Column2 names and includes the first row as data when HasHeader is false`` () =
    let path = tempCsvFile Encoding.UTF8 "1,Sales\n2,Inventory\n"
    let connector = CsvConnector(path, { CsvOptions.Default with HasHeader = false })

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Column1"; "Column2" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "Sales" ], rows.[0] |> Array.toList)

[<Fact>]
let ``ReadRows splits on a custom delimiter`` () =
    let path = tempCsvFile Encoding.UTF8 "Id;Name\n1;Sales\n"
    let connector = CsvConnector(path, { CsvOptions.Default with Delimiter = ';' })

    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)
    Assert.Equal<obj list>([ box "1"; box "Sales" ], (result.Rows |> Seq.exactlyOne) |> Array.toList)

[<Fact>]
let ``TestConnectionAsync returns true for an existing file and false for a missing one`` () =
    task {
        let path = tempCsvFile Encoding.UTF8 "Id\n1\n"
        let existing = CsvConnector(path) :> IConnector
        let missing = CsvConnector(path + ".does-not-exist") :> IConnector

        let! existingResult = existing.TestConnectionAsync(CancellationToken.None)
        let! missingResult = missing.TestConnectionAsync(CancellationToken.None)

        Assert.True(existingResult)
        Assert.False(missingResult)
    }

[<Fact>]
let ``DiscoverSchemaAsync reports one table named after the file, with header-derived columns`` () =
    task {
        let path = tempCsvFile Encoding.UTF8 "Id,Name\n1,Sales\n"
        let connector = CsvConnector(path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)

        let table = tables |> List.exactlyOne
        Assert.Equal(Path.GetFileNameWithoutExtension(path), table.Name)
        Assert.Equal<string list>([ "Id"; "Name" ], table.Columns |> List.map (fun c -> c.Name))
    }

[<Fact>]
let ``CsvConnector reports its Kind as Csv`` () =
    let path = tempCsvFile Encoding.UTF8 "Id\n1\n"
    let connector = CsvConnector(path) :> IConnector

    Assert.Equal(ConnectorKind.Csv, connector.Kind)

[<Fact>]
let ``typed conversion helpers parse valid text and reject invalid text`` () =
    Assert.Equal(Some 42, CsvConversions.tryParseInt "42")
    Assert.Equal(None, CsvConversions.tryParseInt "not-a-number")

    Assert.Equal(Some 3.5, CsvConversions.tryParseFloat "3.5")
    Assert.Equal(Some 3.5, CsvConversions.tryParseFloat "3,5")
    Assert.Equal(None, CsvConversions.tryParseFloat "not-a-float")

    Assert.Equal(Some 123.45m, CsvConversions.tryParseDecimal "123.45")
    Assert.Equal(Some 123.45m, CsvConversions.tryParseDecimal "123,45")
    Assert.Equal(None, CsvConversions.tryParseDecimal "not-a-decimal")

    Assert.Equal(Some true, CsvConversions.tryParseBool "true")
    Assert.Equal(None, CsvConversions.tryParseBool "not-a-bool")

    Assert.Equal(Some(DateTime(2026, 1, 15)), CsvConversions.tryParseDateTime "2026-01-15")
    Assert.Equal(None, CsvConversions.tryParseDateTime "not-a-date")

[<Fact>]
let ``CsvConnector automatically detects semicolon delimiter when no options are passed`` () =
    let path = tempCsvFile Encoding.UTF8 "Id;Name;Preco\n1;Monitor;499,90\n2;Teclado;120,00\n"
    let connector = CsvConnector(path)
    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name"; "Preco" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(2, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "Monitor"; box "499,90" ], rows.[0] |> Array.toList)
    Assert.Equal<obj list>([ box "2"; box "Teclado"; box "120,00" ], rows.[1] |> Array.toList)

[<Fact>]
let ``CsvConnector automatically detects tab delimiter when no options are passed`` () =
    let path = tempCsvFile Encoding.UTF8 "Id\tName\tCity\n1\tAlice\tSP\n"
    let connector = CsvConnector(path)
    let result = connector.ReadRows()

    Assert.Equal<string list>([ "Id"; "Name"; "City" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal(1, rows.Length)
    Assert.Equal<obj list>([ box "1"; box "Alice"; box "SP" ], rows.[0] |> Array.toList)

[<Fact>]
let ``CsvConnector trims surrounding quotes from Windows copy-as-path`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Name\n1,Product\n"
    let quotedPath = $"\"{path}\""
    let connector = CsvConnector(quotedPath)

    Assert.Equal(path, connector.FilePath)
    let result = connector.ReadRows()
    Assert.Equal<string list>([ "Id"; "Name" ], result.Columns)
    let rows = result.Rows |> Seq.toList
    Assert.Equal<obj list>([ box "1"; box "Product" ], rows.[0] |> Array.toList)

[<Fact>]
let ``DiscoverSchemaAsync infers column types and wraps in option when N/A, -, or null is found`` () =
    task {
        let content =
            "Id,Amount,Discount,Status,Notes\n" +
            "1,100.50,10.00,Active,First note\n" +
            "2,200.00,N/A,Active,-\n" +
            "3,300.00,-,Pending,null\n"
        let path = tempCsvFile Encoding.UTF8 content
        let connector = CsvConnector(path) :> IConnector

        let! tables = connector.DiscoverSchemaAsync(CancellationToken.None)
        let table = tables |> List.exactlyOne

        let colTypes = table.Columns |> List.map (fun c -> (c.Name, c.DataType))
        Assert.Equal<(string * string) list>(
            [ ("Id", "int")
              ("Amount", "decimal")
              ("Discount", "decimal option")
              ("Status", "string")
              ("Notes", "string option") ],
            colTypes
        )
    }

[<Fact>]
let ``CsvDescriptor properties and validation work correctly`` () =
    let descriptor = CsvDescriptor.Instance :> IConnectorDescriptor
    Assert.Equal("Csv", descriptor.KindId)
    Assert.Equal("CSV", descriptor.DisplayName)
    Assert.Equal("file-text", descriptor.Icon)
    Assert.Equal(ConnectorCategory.TabularFile, descriptor.Category)
    Assert.True(descriptor.FormFields.Length >= 4)
    Assert.True(descriptor.EntityGenerator.IsSome)

    // Validation
    let validJson = System.Text.Json.JsonDocument.Parse("""{"filePath": "data.csv"}""").RootElement
    let validRes = descriptor.ValidateConfiguration(validJson)
    Assert.True(validRes.IsOk)

    let invalidJson = System.Text.Json.JsonDocument.Parse("""{}""").RootElement
    let invalidRes = descriptor.ValidateConfiguration(invalidJson)
    Assert.True(invalidRes.IsError)

[<Fact>]
let ``CsvConnectorFactory creates CsvConnector with various json configurations`` () =
    let factory = CsvConnectorFactory() :> IConnectorFactory
    let dummyStore = { new FSharpQueryStudio.Security.ISecretStore with
        member _.TryRead(_) = None
        member _.TryReadAsync(_, _) = System.Threading.Tasks.Task.FromResult None
        member _.Save _ _ = ()
        member _.Delete _ = ()
    }

    // String configuration
    let strDoc = System.Text.Json.JsonDocument.Parse("\"C:/temp/test.csv\"")
    let conn1 = factory.Create(strDoc.RootElement, dummyStore, None) :?> CsvConnector
    Assert.Equal("C:/temp/test.csv", conn1.FilePath)

    // Full object configuration
    let jsonStr = """{"filePath":"C:/temp/data.csv","delimiter":";","hasHeader":false,"columnTypes":{"Col1":"int"},"columnTransforms":{"Col1":"trim"}}"""
    let objDoc = System.Text.Json.JsonDocument.Parse(jsonStr)
    let conn2 = factory.Create(objDoc.RootElement, dummyStore, None) :?> CsvConnector
    Assert.Equal(';', conn2.Options.Delimiter)
    Assert.False(conn2.Options.HasHeader)
    Assert.Equal(Some "int", conn2.Options.ColumnTypes.TryFind "Col1")
    Assert.Equal(Some "trim", conn2.Options.ColumnTransforms.TryFind "Col1")

[<Fact>]
let ``CsvEntityGenerator emits module and extension code`` () =
    let path = tempCsvFile Encoding.UTF8 "Id,Amount,Name\n1,10.5,Item1\n2,20.0,Item2\n"
    let connector = CsvConnector(path)
    let descriptor = CsvDescriptor.Instance :> IConnectorDescriptor
    let generator = descriptor.EntityGenerator.Value
    Assert.Equal(ConnectorCategory.TabularFile, generator.Category)

    let moduleCode = generator.EmitModuleCode("TestConn", connector, "TestConnModule")
    Assert.Contains("module TestConnModule =", moduleCode)
    Assert.Contains("type Row =", moduleCode)
    Assert.Contains("Id: int", moduleCode)
    Assert.Contains("Amount: decimal", moduleCode)
    Assert.Contains("type Data(conn: FSharpQueryStudio.Connectors.Connections.CsvConnection) =", moduleCode)

    let extCode = generator.EmitExtensionCode("TestConn", "TestConnModule", "TestProperty")
    Assert.Contains("TestProperty", extCode)
    Assert.Contains("TestConnModule.Data", extCode)

    let connWithTypes = CsvConnector(path, { CsvOptions.Default with ColumnTypes = Map.ofList [ "Id", "int64" ] })
    let moduleWithTypes = generator.EmitModuleCode("TestConnTypes", connWithTypes, "TestConnTypesMod")
    Assert.Contains("Id: int64", moduleWithTypes)

    // Also exercise ModularEntityGenerators.CsvEntityGenerator
    let modGen = FSharpQueryStudio.Connectors.CsvEntityGenerator() :> IConnectorEntityGenerator
    let modModule = modGen.EmitModuleCode("TestConn2", connector, "TestConn2Module")
    Assert.Contains("module TestConn2Module =", modModule)
    let modExt = modGen.EmitExtensionCode("TestConn2", "TestConn2Module", "TestProp2")
    Assert.Contains("TestProp2", modExt)

[<Fact>]
let ``CsvTypeInference and CsvConnector handle empty files, comma decimals, transforms and explicit types`` () =
    Assert.Equal("string option", CsvTypeInference.inferType [ "N/A"; "-" ])
    Assert.Equal("decimal", CsvTypeInference.inferType [ "12,34"; "56,78" ])
    Assert.Equal("double", CsvTypeInference.inferType [ "1e10"; "12,34" ])

    let emptyPath = tempCsvFile Encoding.UTF8 ""
    let emptyConn = CsvConnector(emptyPath)
    Assert.Empty(emptyConn.ReadRows().Columns)

    let path = tempCsvFile Encoding.UTF8 "Name,Price\nWidget,10\n"
    let opts =
        { CsvOptions.Default with
            ColumnTransforms = Map.ofList [ "Price", "multiply(2)" ]
            ColumnTypes = Map.ofList [ "Price", "decimal" ] }
    let conn = CsvConnector(path, opts)
    let qr = conn.ReadRows()
    let rows = qr.Rows |> Seq.toList
    Assert.Equal(box "20", rows.[0].[1])

    let tables = (conn :> IConnector).DiscoverSchemaAsync(CancellationToken.None).Result
    let priceCol = tables.[0].Columns |> List.find (fun c -> c.Name = "Price")
    Assert.Equal("decimal", priceCol.DataType)


