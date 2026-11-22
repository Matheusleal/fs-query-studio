module FsacParsingTests

open System.Text.Json
open Xunit
open FSharpQueryStudio.Fsac
open FSharpQueryStudio.Protocol

[<Fact>]
let ``mapSeverity maps 1 to Error, 2 to Warning, and others or None to Information`` () =
    Assert.Equal(DiagnosticSeverity.Error, FsacParsing.mapSeverity (Some 1))
    Assert.Equal(DiagnosticSeverity.Warning, FsacParsing.mapSeverity (Some 2))
    Assert.Equal(DiagnosticSeverity.Information, FsacParsing.mapSeverity (Some 3))
    Assert.Equal(DiagnosticSeverity.Information, FsacParsing.mapSeverity (Some 4))
    Assert.Equal(DiagnosticSeverity.Information, FsacParsing.mapSeverity None)

[<Fact>]
let ``parseDiagnostics handles Warning, Information, and code formats (number, string, none)`` () =
    let json =
        """
    {
        "diagnostics": [
            {
                "range": { "start": { "line": 0, "character": 1 }, "end": { "line": 0, "character": 5 } },
                "severity": 2,
                "code": "FS0001",
                "message": "Warning message"
            },
            {
                "range": { "start": { "line": 1, "character": 0 }, "end": { "line": 1, "character": 4 } },
                "severity": 3,
                "code": 42,
                "message": "Info with numeric code"
            },
            {
                "range": { "start": { "line": 2, "character": 0 }, "end": { "line": 2, "character": 2 } },
                "message": "Diagnostic without severity or code"
            }
        ]
    }
    """

    use doc = JsonDocument.Parse json
    let diags = FsacParsing.parseDiagnostics "file:///test.fsx" doc.RootElement
    Assert.Equal(3, diags.Length)
    Assert.Equal(DiagnosticSeverity.Warning, diags.[0].Severity)
    Assert.Equal(Some "FS0001", diags.[0].Code)
    Assert.Equal(DiagnosticSeverity.Information, diags.[1].Severity)
    Assert.Equal(Some "42", diags.[1].Code)
    Assert.Equal(DiagnosticSeverity.Information, diags.[2].Severity)
    Assert.Equal(None, diags.[2].Code)

[<Fact>]
let ``parseFirstLocation handles null, empty array, Location, LocationLink, and Object`` () =
    // Null
    let nullEl = JsonDocument.Parse("null").RootElement
    Assert.True(FsacParsing.parseFirstLocation nullEl |> Option.isNone)

    // Empty array
    let emptyArr = JsonDocument.Parse("[]").RootElement
    Assert.True(FsacParsing.parseFirstLocation emptyArr |> Option.isNone)

    // Other kind (e.g. string or number)
    let numberEl = JsonDocument.Parse("123").RootElement
    Assert.True(FsacParsing.parseFirstLocation numberEl |> Option.isNone)

    // Location (range + uri) in array
    let locArr =
        JsonDocument
            .Parse(
                """[{"uri": "file:///path/a.fs", "range": {"start": {"line": 10, "character": 5}, "end": {"line": 10, "character": 10}}}]"""
            )
            .RootElement

    let resLoc = FsacParsing.parseFirstLocation locArr
    Assert.True(resLoc.IsSome)
    let uri1, range1 = resLoc.Value
    Assert.Equal("file:///path/a.fs", uri1)
    Assert.Equal(10, range1.GetProperty("start").GetProperty("line").GetInt32())

    // LocationLink (targetUri + targetRange) in array
    let linkArr =
        JsonDocument
            .Parse(
                """[{"targetUri": "file:///path/b.fs", "targetRange": {"start": {"line": 20, "character": 2}, "end": {"line": 20, "character": 8}}}]"""
            )
            .RootElement

    let resLink = FsacParsing.parseFirstLocation linkArr
    Assert.True(resLink.IsSome)
    let uri2, range2 = resLink.Value
    Assert.Equal("file:///path/b.fs", uri2)
    Assert.Equal(20, range2.GetProperty("start").GetProperty("line").GetInt32())

    // Direct Object Location
    let objLoc =
        JsonDocument
            .Parse(
                """{"uri": "file:///path/c.fs", "range": {"start": {"line": 30, "character": 0}, "end": {"line": 30, "character": 4}}}"""
            )
            .RootElement

    let resObj = FsacParsing.parseFirstLocation objLoc
    Assert.True(resObj.IsSome)
    let uri3, range3 = resObj.Value
    Assert.Equal("file:///path/c.fs", uri3)
    Assert.Equal(30, range3.GetProperty("start").GetProperty("line").GetInt32())

[<Fact>]
let ``simplifyTypeName covers all mapped primitives and unmapped types`` () =
    Assert.Equal("string", FsacParsing.simplifyTypeName "System.String")
    Assert.Equal("string", FsacParsing.simplifyTypeName "String")
    Assert.Equal("int", FsacParsing.simplifyTypeName "System.Int32")
    Assert.Equal("int", FsacParsing.simplifyTypeName "Int32")
    Assert.Equal("int64", FsacParsing.simplifyTypeName "System.Int64")
    Assert.Equal("int64", FsacParsing.simplifyTypeName "Int64")
    Assert.Equal("int16", FsacParsing.simplifyTypeName "System.Int16")
    Assert.Equal("int16", FsacParsing.simplifyTypeName "Int16")
    Assert.Equal("bool", FsacParsing.simplifyTypeName "System.Boolean")
    Assert.Equal("bool", FsacParsing.simplifyTypeName "Boolean")
    Assert.Equal("decimal", FsacParsing.simplifyTypeName "System.Decimal")
    Assert.Equal("decimal", FsacParsing.simplifyTypeName "Decimal")
    Assert.Equal("float", FsacParsing.simplifyTypeName "System.Double")
    Assert.Equal("float", FsacParsing.simplifyTypeName "Double")
    Assert.Equal("float32", FsacParsing.simplifyTypeName "System.Single")
    Assert.Equal("float32", FsacParsing.simplifyTypeName "Single")
    Assert.Equal("byte", FsacParsing.simplifyTypeName "System.Byte")
    Assert.Equal("byte", FsacParsing.simplifyTypeName "Byte")
    Assert.Equal("DateTime", FsacParsing.simplifyTypeName "System.DateTime")
    Assert.Equal("Guid", FsacParsing.simplifyTypeName "System.Guid")
    Assert.Equal("obj", FsacParsing.simplifyTypeName "System.Object")
    Assert.Equal("obj", FsacParsing.simplifyTypeName "Object")
    Assert.Equal("MyCustomType", FsacParsing.simplifyTypeName "MyCustomType")

[<Fact>]
let ``stripSymbolKind and parseHoverMarkdown handle various symbol kinds and markdown formats`` () =
    Assert.Equal("MyMethod() : int", FsacParsing.stripSymbolKind "static member MyMethod() : int")
    Assert.Equal("MyMethod() : int", FsacParsing.stripSymbolKind "abstract member MyMethod() : int")
    Assert.Equal("MyPattern", FsacParsing.stripSymbolKind "active pattern MyPattern")
    Assert.Equal("MyProp : string", FsacParsing.stripSymbolKind "property MyProp : string")
    Assert.Equal("x : int", FsacParsing.stripSymbolKind "val x : int")
    Assert.Equal("T", FsacParsing.stripSymbolKind "type T")
    Assert.Equal("M", FsacParsing.stripSymbolKind "module M")
    Assert.Equal("N", FsacParsing.stripSymbolKind "namespace N")
    Assert.Equal("regular", FsacParsing.stripSymbolKind "regular")

    // Empty contents
    let emptyContents = JsonDocument.Parse("[]").RootElement
    let emptyHover = FsacParsing.parseHoverMarkdown emptyContents
    Assert.Equal("", emptyHover.DisplayName)

    // Markdown with docs and boilerplate filtering
    let mdJson =
        """
    [
        "`val add: int -> int -> int`",
        "<a href=\"...\">docs</a>\n*Full name: Module.add\n*Assembly: Test\nComputes sum of two numbers."
    ]
    """

    let hoverMd =
        FsacParsing.parseHoverMarkdown (JsonDocument.Parse(mdJson).RootElement)

    Assert.Equal("val add: int -> int -> int", hoverMd.Signature.Value)
    Assert.Equal(Some "Computes sum of two numbers.", hoverMd.XmlDoc)

    // Non-array contents object
    let objContents =
        JsonDocument.Parse("""{"value": "val simple : string"}""").RootElement

    let hoverObj = FsacParsing.parseHoverMarkdown objContents
    Assert.Equal("simple", hoverObj.DisplayName)
    Assert.Equal("string", hoverObj.Type)

[<Fact>]
let ``splitTopLevelArrows splits top-level arrows respecting nested parentheses`` () =
    let sig1 = "int -> (int -> string) -> (bool * (int -> unit)) -> string"
    let parts = FsacParsing.splitTopLevelArrows sig1
    Assert.Equal(4, parts.Length)
    Assert.Equal("int", parts.[0])
    Assert.Equal("(int -> string)", parts.[1])
    Assert.Equal("(bool * (int -> unit))", parts.[2])
    Assert.Equal("string", parts.[3])

[<Fact>]
let ``parseItem and parseCompletionItems cover label formats, details, and kinds`` () =
    // Item without label returns None
    let noLabel = JsonDocument.Parse("{}").RootElement
    Assert.True(FsacParsing.parseItem noLabel |> Option.isNone)

    // Item with structured label (CompletionItemLabelDetails)
    let structuredLabel =
        JsonDocument.Parse("""{"label": {"label": "MyItem", "description": ": System.Int32"}, "kind": 6}""").RootElement

    let itemStructured = FsacParsing.parseItem structuredLabel
    Assert.True(itemStructured.IsSome)
    Assert.Equal("MyItem", itemStructured.Value.Label)
    Assert.Equal(Some "int", itemStructured.Value.Detail)

    // Structured label without inner label
    let invalidStructured =
        JsonDocument.Parse("""{"label": {"description": "desc"}}""").RootElement

    Assert.True(FsacParsing.parseItem invalidStructured |> Option.isNone)

    // Kinds without detail mapping to default strings
    let testKind (kindId: int) (expectedDetail: string) =
        let json = sprintf """{"label": "item%d", "kind": %d}""" kindId kindId
        let item = FsacParsing.parseItem (JsonDocument.Parse(json).RootElement)
        Assert.True(item.IsSome)
        Assert.Equal(Some expectedDetail, item.Value.Detail)

    testKind 1 "text"
    testKind 2 "method"
    testKind 3 "function"
    testKind 4 "constructor"
    testKind 5 "field"
    testKind 6 "variable"
    testKind 7 "class"
    testKind 8 "interface"
    testKind 9 "module"
    testKind 10 "property"
    testKind 11 "unit"
    testKind 12 "value"
    testKind 13 "enum"
    testKind 14 "keyword"
    testKind 15 "snippet"
    testKind 21 "constant"
    testKind 22 "struct"
    testKind 23 "event"
    testKind 24 "operator"
    testKind 25 "type"

    // unknown kind -> None detail
    let unknownKind =
        JsonDocument.Parse("""{"label": "item99", "kind": 99}""").RootElement

    Assert.Equal(None, (FsacParsing.parseItem unknownKind).Value.Detail)

    // Detail with colon and with clause
    let detailWithWith =
        JsonDocument
            .Parse("""{"label": "prop", "kind": 10, "detail": "prop: System.String with get, set"}""")
            .RootElement

    Assert.Equal(Some "string", (FsacParsing.parseItem detailWithWith).Value.Detail)

    // Detail with colon and no with
    let detailColon =
        JsonDocument.Parse("""{"label": "func", "kind": 6, "detail": "x: System.Int32"}""").RootElement

    Assert.Equal(Some "int", (FsacParsing.parseItem detailColon).Value.Detail)

    // Plain detail with colon on other kind
    let otherColon =
        JsonDocument.Parse("""{"label": "item", "kind": 1, "detail": "foo: bar"}""").RootElement

    Assert.Equal(Some "bar", (FsacParsing.parseItem otherColon).Value.Detail)

    // Detail without colon on kind that isn't field/prop/var/val
    let plainDetail =
        JsonDocument.Parse("""{"label": "custom", "kind": 99, "detail": "Custom detail"}""").RootElement

    Assert.Equal(Some "Custom detail", (FsacParsing.parseItem plainDetail).Value.Detail)

    // method kind with detail returns "method"
    let methodWithDetail =
        JsonDocument.Parse("""{"label": "ToString", "kind": 2, "detail": "obj.ToString() : string"}""").RootElement

    Assert.Equal(Some "method", (FsacParsing.parseItem methodWithDetail).Value.Detail)

    // Whitespace detail falls back to kind
    let whitespaceDetail =
        JsonDocument.Parse("""{"label": "item", "detail": "   ", "kind": 1}""").RootElement

    Assert.Equal(Some "text", (FsacParsing.parseItem whitespaceDetail).Value.Detail)

    // Description in label with whitespace falls back to kind
    let descWhitespace =
        JsonDocument.Parse("""{"label": {"label": "prop", "description": "   "}, "kind": 1}""").RootElement

    Assert.Equal(Some "text", (FsacParsing.parseItem descWhitespace).Value.Detail)

    // parseCompletionItems with null, array, and object { items: [...] }
    Assert.Empty(FsacParsing.parseCompletionItems (JsonDocument.Parse("null").RootElement))
    Assert.Empty(FsacParsing.parseCompletionItems (JsonDocument.Parse("123").RootElement))
    Assert.Empty(FsacParsing.parseCompletionItems (JsonDocument.Parse("{}").RootElement))

    let directArr =
        JsonDocument.Parse("""[{"label": "A"}, {"label": "B"}, {"label": "A"}]""").RootElement

    let itemsDirect = FsacParsing.parseCompletionItems directArr
    Assert.Equal(2, itemsDirect.Length) // DistinctBy Label

    let wrappedObj =
        JsonDocument.Parse("""{"items": [{"label": "X"}, {"label": "Y"}]}""").RootElement

    let itemsWrapped = FsacParsing.parseCompletionItems wrappedObj
    Assert.Equal(2, itemsWrapped.Length)
