module FSharpQueryStudio.Connectors.Tests.RecordReadersTests

open System
open System.Data
open System.Globalization
open Xunit
open FSharpQueryStudio.Connectors

// =========================================================================
// 0. Mock IDataRecord implementation for comprehensive unit testing
// =========================================================================

type MockDataRecord(values: (string * obj) array, ?throwOnTypedGet: bool) =
    let colNames = values |> Array.map fst
    let colValues = values |> Array.map snd
    let shouldThrowOnTyped = defaultArg throwOnTypedGet false

    new(values: obj array, ?throwOnTypedGet: bool) =
        let pairs = values |> Array.mapi (fun i v -> ($"Col{i}", v))
        MockDataRecord(pairs, ?throwOnTypedGet = throwOnTypedGet)

    interface IDataRecord with
        member _.FieldCount = values.Length
        member _.GetName(i) = colNames.[i]

        member _.GetDataTypeName(i) =
            if isNull colValues.[i] then
                "System.Object"
            else
                colValues.[i].GetType().Name

        member _.GetFieldType(i) =
            if isNull colValues.[i] then
                typeof<obj>
            else
                colValues.[i].GetType()

        member _.GetValue(i) = colValues.[i]

        member _.GetValues(valuesArr) =
            let count = min valuesArr.Length colValues.Length
            Array.Copy(colValues, valuesArr, count)
            count

        member _.GetOrdinal(name) =
            match
                colNames
                |> Array.tryFindIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
            with
            | Some idx -> idx
            | None -> raise (IndexOutOfRangeException($"Field '{name}' not found"))

        member _.GetBoolean(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToBoolean(colValues.[i])

        member _.GetByte(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToByte(colValues.[i])

        member _.GetBytes(i, fieldOffset, buffer, bufferoffset, length) = 0L

        member _.GetChar(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToChar(colValues.[i])

        member _.GetChars(i, fieldOffset, buffer, bufferoffset, length) = 0L

        member _.GetGuid(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            match colValues.[i] with
            | :? Guid as g -> g
            | :? string as s -> Guid.Parse(s)
            | :? (byte array) as b -> Guid(b)
            | _ -> raise (InvalidCastException())

        member _.GetInt16(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToInt16(colValues.[i])

        member _.GetInt32(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToInt32(colValues.[i])

        member _.GetInt64(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToInt64(colValues.[i])

        member _.GetFloat(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToSingle(colValues.[i])

        member _.GetDouble(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToDouble(colValues.[i])

        member _.GetString(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToString(colValues.[i])

        member _.GetDecimal(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToDecimal(colValues.[i])

        member _.GetDateTime(i) =
            if shouldThrowOnTyped then
                raise (InvalidCastException())

            Convert.ToDateTime(colValues.[i])

        member _.GetData(i) = raise (NotSupportedException())

        member _.IsDBNull(i) =
            if i < 0 || i >= colValues.Length then
                true
            else
                let v = colValues.[i]
                isNull v || Convert.IsDBNull(v)

        member _.Item
            with get (i: int) = colValues.[i]

        member _.Item
            with get (name: string) =
                let idx =
                    (colNames
                     |> Array.findIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)))

                colValues.[idx]

// Test records for CompiledRecordMapper tests
type SampleRecord =
    {
        Id: int
        Name: string
        Amount: decimal
        Score: double
        IsActive: bool
        CreatedDate: DateTime
        EffectiveDate: DateOnly
        EventTime: TimeOnly
        Notes: string option
        Data: byte[]
    }

type SimpleScalarRecord = { Count: int }

// =========================================================================
// 1. TryParseNormalizedDecimal & TryParseNormalizedDouble tests
// =========================================================================

module DecimalNormalizationTests =

    [<Theory>]
    [<InlineData("1.234,56", 1234.56)>]
    [<InlineData("-1.234,56", -1234.56)>]
    [<InlineData("1.234.567,89", 1234567.89)>]
    [<InlineData("-1.234.567,89", -1234567.89)>]
    let ``TryParseNormalizedDecimal handles European and Brazilian formats with thousand dots and decimal comma``
        (input: string, expected: double)
        =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("1,234.56", 1234.56)>]
    [<InlineData("-1,234.56", -1234.56)>]
    [<InlineData("1,234,567.89", 1234567.89)>]
    [<InlineData("-1,234,567.89", -1234567.89)>]
    let ``TryParseNormalizedDecimal handles US and Invariant formats with thousand commas and decimal dot``
        (input: string, expected: double)
        =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("1234,56", 1234.56)>]
    [<InlineData("-1234,56", -1234.56)>]
    [<InlineData("0,75", 0.75)>]
    [<InlineData(",5", 0.5)>]
    let ``TryParseNormalizedDecimal handles comma-only decimal values`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("1234.56", 1234.56)>]
    [<InlineData("-1234.56", -1234.56)>]
    [<InlineData("0.75", 0.75)>]
    [<InlineData(".5", 0.5)>]
    let ``TryParseNormalizedDecimal handles dot-only decimal values`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("1.23e-4", 0.000123)>]
    [<InlineData("-4.5E+2", -450.0)>]
    [<InlineData("1.5e3", 1500.0)>]
    [<InlineData("2.0E-2", 0.02)>]
    let ``TryParseNormalizedDecimal handles scientific notation`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("   ")>]
    [<InlineData("abc")>]
    [<InlineData("--")>]
    [<InlineData("foo.bar")>]
    [<InlineData("12.34.56.78")>]
    let ``TryParseNormalizedDecimal returns false and 0M on invalid strings`` (input: string) =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.False(success)
        Assert.Equal(0M, result)


module DoubleNormalizationTests =

    [<Theory>]
    [<InlineData("1.234,56", 1234.56)>]
    [<InlineData("-1.234,56", -1234.56)>]
    [<InlineData("1.234.567,89", 1234567.89)>]
    let ``TryParseNormalizedDouble handles European and Brazilian formats`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.True(success)
        Assert.Equal(expected, result, 2)

    [<Theory>]
    [<InlineData("1,234.56", 1234.56)>]
    [<InlineData("-1,234.56", -1234.56)>]
    [<InlineData("1,234,567.89", 1234567.89)>]
    let ``TryParseNormalizedDouble handles US and Invariant formats`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.True(success)
        Assert.Equal(expected, result, 2)

    [<Theory>]
    [<InlineData("1234,56", 1234.56)>]
    [<InlineData("-1234,56", -1234.56)>]
    let ``TryParseNormalizedDouble handles comma-only format`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.True(success)
        Assert.Equal(expected, result, 2)

    [<Theory>]
    [<InlineData("1234.56", 1234.56)>]
    [<InlineData("-1234.56", -1234.56)>]
    let ``TryParseNormalizedDouble handles dot-only format`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.True(success)
        Assert.Equal(expected, result, 2)

    [<Theory>]
    [<InlineData("1.23e-4", 0.000123)>]
    [<InlineData("-4.5E+2", -450.0)>]
    let ``TryParseNormalizedDouble handles scientific notation`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.True(success)
        Assert.Equal(expected, result, 6)

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("   ")>]
    [<InlineData("not-a-number")>]
    [<InlineData("###")>]
    let ``TryParseNormalizedDouble returns false and 0.0 on invalid strings`` (input: string) =
        let success, result = RecordReaders.TryParseNormalizedDouble input
        Assert.False(success)
        Assert.Equal(0.0, result)

    [<Fact>]
    let ``TryParseNormalizedDouble returns false and 0.0 for Infinity and NaN`` () =
        let checkBad s =
            let ok, v = RecordReaders.TryParseNormalizedDouble s
            Assert.False(ok)
            Assert.Equal(0.0, v)

        checkBad (Double.PositiveInfinity.ToString())
        checkBad (Double.NegativeInfinity.ToString())
        checkBad (Double.NaN.ToString())
        checkBad "Infinity"
        checkBad "-Infinity"
        checkBad "NaN"


// =========================================================================
// 2. Sentinels detection tests
// =========================================================================

module SentinelStringTests =

    [<Theory>]
    [<InlineData("")>]
    [<InlineData("   ")>]
    [<InlineData("-")>]
    [<InlineData("--")>]
    [<InlineData("?")>]
    [<InlineData("n/a")>]
    [<InlineData("N/A")>]
    [<InlineData("na")>]
    [<InlineData("null")>]
    [<InlineData("NULL")>]
    [<InlineData("none")>]
    [<InlineData("nil")>]
    [<InlineData("undefined")>]
    [<InlineData("nan")>]
    [<InlineData("NaN")>]
    [<InlineData("infinity")>]
    [<InlineData("-infinity")>]
    [<InlineData("tbd")>]
    [<InlineData("#N/A")>]
    [<InlineData("#VALUE!")>]
    [<InlineData("\"NULL\"")>]
    [<InlineData("'N/A'")>]
    let ``IsSentinelString identifies known null/missing sentinels`` (s: string) =
        Assert.True(RecordReaders.IsSentinelString s)

    [<Theory>]
    [<InlineData("0")>]
    [<InlineData("123")>]
    [<InlineData("valid-text")>]
    [<InlineData("active")>]
    let ``IsSentinelString returns false for regular values`` (s: string) =
        Assert.False(RecordReaders.IsSentinelString s)


// =========================================================================
// 3. IDataRecord typed readers tests
// =========================================================================

module ReadInt32Tests =

    [<Fact>]
    let ``ReadInt32 handles native int32 values`` () =
        let record: IDataRecord = MockDataRecord([| box 42; box -100 |])
        Assert.Equal(42, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(-100, RecordReaders.ReadInt32(record, 1))

    [<Fact>]
    let ``ReadInt32 clamps int64 exceeding Int32 limits`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box (int64 Int32.MaxValue + 500L)
                    box (int64 Int32.MinValue - 500L)
                    box Int64.MaxValue
                    box Int64.MinValue
                |]
            )

        Assert.Equal(Int32.MaxValue, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(Int32.MinValue, RecordReaders.ReadInt32(record, 1))
        Assert.Equal(Int32.MaxValue, RecordReaders.ReadInt32(record, 2))
        Assert.Equal(Int32.MinValue, RecordReaders.ReadInt32(record, 3))

    [<Fact>]
    let ``ReadInt32 converts single double decimal with rounding and clamping`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 42.7f
                    box 42.2
                    box 99.6M
                    box 1e12
                    box -1e12
                    box Double.PositiveInfinity
                    box Double.NaN
                |]
            )

        Assert.Equal(43, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(42, RecordReaders.ReadInt32(record, 1))
        Assert.Equal(100, RecordReaders.ReadInt32(record, 2))
        Assert.Equal(Int32.MaxValue, RecordReaders.ReadInt32(record, 3))
        Assert.Equal(Int32.MinValue, RecordReaders.ReadInt32(record, 4))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 5))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 6))

    [<Fact>]
    let ``ReadInt32 converts boolean true to 1 and false to 0`` () =
        let record: IDataRecord = MockDataRecord([| box true; box false |])
        Assert.Equal(1, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 1))

    [<Fact>]
    let ``ReadInt32 converts byte and int16`` () =
        let record: IDataRecord = MockDataRecord([| box 25uy; box 300s |])
        Assert.Equal(25, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(300, RecordReaders.ReadInt32(record, 1))

    [<Fact>]
    let ``ReadInt32 parses string with quotes spaces and decimal formatting`` () =
        let record: IDataRecord =
            MockDataRecord([| box " \" 1,234.56 \" "; box " '42' "; box " -500 "; box "123" |])

        Assert.Equal(1235, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(42, RecordReaders.ReadInt32(record, 1))
        Assert.Equal(-500, RecordReaders.ReadInt32(record, 2))
        Assert.Equal(123, RecordReaders.ReadInt32(record, 3))

    [<Theory>]
    [<InlineData("-")>]
    [<InlineData("NULL")>]
    [<InlineData("N/A")>]
    [<InlineData("")>]
    [<InlineData("#N/A")>]
    [<InlineData("NaN")>]
    [<InlineData("undefined")>]
    let ``ReadInt32 returns 0 for sentinels`` (sentinel: string) =
        let record: IDataRecord = MockDataRecord([| box sentinel |])
        Assert.Equal(0, RecordReaders.ReadInt32(record, 0))

    [<Fact>]
    let ``ReadInt32 returns 0 for out of bounds index and IsDBNull`` () =
        let record: IDataRecord = MockDataRecord([| box DBNull.Value; null |])
        Assert.Equal(0, RecordReaders.ReadInt32(record, -1))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 10))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(0, RecordReaders.ReadInt32(record, 1))


module ReadInt64Tests =

    [<Fact>]
    let ``ReadInt64 handles native int64 int int16 and byte`` () =
        let record: IDataRecord =
            MockDataRecord([| box 9876543210123L; box 42; box 100s; box 12uy |])

        Assert.Equal(9876543210123L, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(42L, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(100L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(12L, RecordReaders.ReadInt64(record, 3))

    [<Fact>]
    let ``ReadInt64 clamps decimal double and single`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 1e25
                    box -1e25
                    box 9876543210.4M
                    box 123.7f
                    box Double.PositiveInfinity
                    box Double.NaN
                |]
            )

        Assert.Equal(Int64.MaxValue, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(Int64.MinValue, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(9876543210L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(124L, RecordReaders.ReadInt64(record, 3))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 4))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 5))

    [<Fact>]
    let ``ReadInt64 handles bool and strings with formatting`` () =
        let record: IDataRecord =
            MockDataRecord([| box true; box false; box " 9876543210 "; box " \"1,234,567.89\" " |])

        Assert.Equal(1L, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(9876543210L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(1234568L, RecordReaders.ReadInt64(record, 3))

    [<Fact>]
    let ``ReadInt64 returns 0L for sentinels DBNull and invalid index`` () =
        let record: IDataRecord = MockDataRecord([| box "NULL"; box DBNull.Value; null |])
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, -1))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 99))


module ReadInt16AndByteTests =

    [<Fact>]
    let ``ReadInt16 handles int16 and clamping of larger integers`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 1234s
                    box 50000
                    box -50000
                    box 1000000L
                    box -1000000L
                    box 200uy
                    box " 300 "
                    box "1,200.4"
                |],
                throwOnTypedGet = true
            )

        Assert.Equal(1234s, RecordReaders.ReadInt16(record, 0))
        Assert.Equal(Int16.MaxValue, RecordReaders.ReadInt16(record, 1))
        Assert.Equal(Int16.MinValue, RecordReaders.ReadInt16(record, 2))
        Assert.Equal(Int16.MaxValue, RecordReaders.ReadInt16(record, 3))
        Assert.Equal(Int16.MinValue, RecordReaders.ReadInt16(record, 4))
        Assert.Equal(200s, RecordReaders.ReadInt16(record, 5))
        Assert.Equal(300s, RecordReaders.ReadInt16(record, 6))
        Assert.Equal(1200s, RecordReaders.ReadInt16(record, 7))

    [<Fact>]
    let ``ReadInt16 handles sentinels and invalid indices`` () =
        let record: IDataRecord = MockDataRecord([| box "N/A"; box DBNull.Value |])
        Assert.Equal(0s, RecordReaders.ReadInt16(record, 0))
        Assert.Equal(0s, RecordReaders.ReadInt16(record, 1))
        Assert.Equal(0s, RecordReaders.ReadInt16(record, -5))

    [<Fact>]
    let ``ReadByte handles byte and string conversions with error handling`` () =
        let record: IDataRecord =
            MockDataRecord([| box 128uy; box 255; box "200"; box "invalid"; box DBNull.Value |], throwOnTypedGet = true)

        Assert.Equal(128uy, RecordReaders.ReadByte(record, 0))
        Assert.Equal(255uy, RecordReaders.ReadByte(record, 1))
        Assert.Equal(200uy, RecordReaders.ReadByte(record, 2))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, 3))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, 4))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, -1))


module ReadDecimalDoubleSingleTests =

    [<Fact>]
    let ``ReadDecimal converts various types and formats accurately`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 1234.56M
                    box 1234.56
                    box 42.5f
                    box 100
                    box 5000L
                    box " 1.234,56 "
                    box " 1,234.56 "
                    box "NULL"
                    box DBNull.Value
                |]
            )

        Assert.Equal(1234.56M, RecordReaders.ReadDecimal(record, 0))
        Assert.Equal(1234.56M, RecordReaders.ReadDecimal(record, 1))
        Assert.Equal(42.5M, RecordReaders.ReadDecimal(record, 2))
        Assert.Equal(100M, RecordReaders.ReadDecimal(record, 3))
        Assert.Equal(5000M, RecordReaders.ReadDecimal(record, 4))
        Assert.Equal(1234.56M, RecordReaders.ReadDecimal(record, 5))
        Assert.Equal(1234.56M, RecordReaders.ReadDecimal(record, 6))
        Assert.Equal(0M, RecordReaders.ReadDecimal(record, 7))
        Assert.Equal(0M, RecordReaders.ReadDecimal(record, 8))
        Assert.Equal(0M, RecordReaders.ReadDecimal(record, -1))

    [<Fact>]
    let ``ReadDouble converts various types and handles finite checks`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 1234.56
                    box 42.5f
                    box 100M
                    box 1000
                    box " 1.234,56 "
                    box Double.PositiveInfinity
                    box Double.NaN
                    box "NaN"
                    box DBNull.Value
                |]
            )

        Assert.Equal(1234.56, RecordReaders.ReadDouble(record, 0), 2)
        Assert.Equal(42.5, RecordReaders.ReadDouble(record, 1), 1)
        Assert.Equal(100.0, RecordReaders.ReadDouble(record, 2), 1)
        Assert.Equal(1000.0, RecordReaders.ReadDouble(record, 3), 1)
        Assert.Equal(1234.56, RecordReaders.ReadDouble(record, 4), 2)
        Assert.Equal(0.0, RecordReaders.ReadDouble(record, 5))
        Assert.Equal(0.0, RecordReaders.ReadDouble(record, 6))
        Assert.Equal(0.0, RecordReaders.ReadDouble(record, 7))
        Assert.Equal(0.0, RecordReaders.ReadDouble(record, 8))
        Assert.Equal(0.0, RecordReaders.ReadDouble(record, 99))

    [<Fact>]
    let ``ReadSingle converts correctly and handles bounds`` () =
        let record: IDataRecord =
            MockDataRecord([| box 42.5f; box 123.45; box "12.34"; box "N/A"; box DBNull.Value |])

        Assert.Equal(42.5f, RecordReaders.ReadSingle(record, 0))
        Assert.Equal(123.45f, RecordReaders.ReadSingle(record, 1))
        Assert.Equal(12.34f, RecordReaders.ReadSingle(record, 2))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(record, 3))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(record, 4))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(record, -1))


module ReadBooleanTests =

    [<Theory>]
    [<InlineData("true", true)>]
    [<InlineData("True", true)>]
    [<InlineData("false", false)>]
    [<InlineData("False", false)>]
    [<InlineData("yes", true)>]
    [<InlineData("YES", true)>]
    [<InlineData("no", false)>]
    [<InlineData("1", true)>]
    [<InlineData("0", false)>]
    [<InlineData("t", true)>]
    [<InlineData("f", false)>]
    [<InlineData("sim", true)>]
    [<InlineData("y", true)>]
    let ``ReadBool and ReadBoolean parse strings correctly`` (str: string, expected: bool) =
        let record: IDataRecord = MockDataRecord([| box str |])
        Assert.Equal(expected, RecordReaders.ReadBool(record, 0))
        Assert.Equal(expected, RecordReaders.ReadBoolean(record, 0))

    [<Fact>]
    let ``ReadBool handles numbers and sentinels`` () =
        let record: IDataRecord =
            MockDataRecord(
                [|
                    box 1
                    box 0
                    box -1
                    box 1L
                    box 0L
                    box 1.0
                    box 0.0
                    box 1M
                    box 0M
                    box "NULL"
                    box DBNull.Value
                |]
            )

        Assert.True(RecordReaders.ReadBool(record, 0))
        Assert.False(RecordReaders.ReadBool(record, 1))
        Assert.True(RecordReaders.ReadBool(record, 2))
        Assert.True(RecordReaders.ReadBool(record, 3))
        Assert.False(RecordReaders.ReadBool(record, 4))
        Assert.True(RecordReaders.ReadBool(record, 5))
        Assert.False(RecordReaders.ReadBool(record, 6))
        Assert.True(RecordReaders.ReadBool(record, 7))
        Assert.False(RecordReaders.ReadBool(record, 8))
        Assert.False(RecordReaders.ReadBool(record, 9))
        Assert.False(RecordReaders.ReadBool(record, 10))
        Assert.False(RecordReaders.ReadBool(record, -1))


module ReadStringAndGuidTests =

    [<Fact>]
    let ``ReadString handles strings and falls back to string representation`` () =
        let record: IDataRecord =
            MockDataRecord([| box "hello"; box 123; box DBNull.Value; null |], throwOnTypedGet = true)

        Assert.Equal("hello", RecordReaders.ReadString(record, 0))
        Assert.Equal("123", RecordReaders.ReadString(record, 1))
        Assert.Null(RecordReaders.ReadString(record, 2))
        Assert.Null(RecordReaders.ReadString(record, 3))
        Assert.Null(RecordReaders.ReadString(record, -1))

    [<Fact>]
    let ``ReadGuid handles Guid, formatted string, byte array and invalid values`` () =
        let g = Guid.NewGuid()
        let gBytes = g.ToByteArray()

        let record: IDataRecord =
            MockDataRecord(
                [|
                    box g
                    box (g.ToString())
                    box (g.ToString("N"))
                    box ("{" + g.ToString() + "}")
                    box gBytes
                    box "not-a-guid"
                    box "NULL"
                    box DBNull.Value
                |],
                throwOnTypedGet = true
            )

        Assert.Equal(g, RecordReaders.ReadGuid(record, 0))
        Assert.Equal(g, RecordReaders.ReadGuid(record, 1))
        Assert.Equal(g, RecordReaders.ReadGuid(record, 2))
        Assert.Equal(g, RecordReaders.ReadGuid(record, 3))
        Assert.Equal(g, RecordReaders.ReadGuid(record, 4))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(record, 5))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(record, 6))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(record, 7))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(record, -1))


module ReadDateAndDateTimeTests =

    [<Fact>]
    let ``ReadDateTime handles DateTime, DateOnly, DateTimeOffset, OADate and ISO strings`` () =
        let dt = DateTime(2026, 11, 16, 14, 30, 0, DateTimeKind.Utc)
        let d = DateOnly(2026, 11, 16)
        let dto = DateTimeOffset(dt)
        let oa = dt.ToOADate()

        let record: IDataRecord =
            MockDataRecord(
                [|
                    box dt
                    box d
                    box dto
                    box oa
                    box "2026-11-16T14:30:00Z"
                    box "2026-11-16"
                    box "NULL"
                    box DBNull.Value
                |],
                throwOnTypedGet = true
            )

        Assert.Equal(dt, RecordReaders.ReadDateTime(record, 0))
        Assert.Equal(d.ToDateTime(TimeOnly.MinValue), RecordReaders.ReadDateTime(record, 1))
        Assert.Equal(dto.DateTime, RecordReaders.ReadDateTime(record, 2))
        Assert.Equal(DateTime.FromOADate(oa), RecordReaders.ReadDateTime(record, 3))
        Assert.Equal(2026, RecordReaders.ReadDateTime(record, 4).Year)
        Assert.Equal(2026, RecordReaders.ReadDateTime(record, 5).Year)
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(record, 6))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(record, 7))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(record, -1))

    [<Fact>]
    let ``ReadDateOnly handles DateOnly, DateTime, strings and sentinels`` () =
        let d = DateOnly(2026, 11, 16)
        let dt = DateTime(2026, 11, 16, 18, 0, 0)

        let record: IDataRecord =
            MockDataRecord(
                [|
                    box d
                    box dt
                    box "2026-11-16"
                    box "2026-11-16T10:00:00Z"
                    box "N/A"
                    box DBNull.Value
                |]
            )

        Assert.Equal(d, RecordReaders.ReadDateOnly(record, 0))
        Assert.Equal(d, RecordReaders.ReadDateOnly(record, 1))
        Assert.Equal(d, RecordReaders.ReadDateOnly(record, 2))
        Assert.Equal(d, RecordReaders.ReadDateOnly(record, 3))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(record, 4))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(record, 5))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(record, -1))

    [<Fact>]
    let ``ReadTimeOnly handles TimeOnly, TimeSpan, DateTime, strings and sentinels`` () =
        let t = TimeOnly(15, 45, 30)
        let ts = TimeSpan(15, 45, 30)
        let dt = DateTime(2026, 11, 16, 15, 45, 30)

        let record: IDataRecord =
            MockDataRecord([| box t; box ts; box dt; box "15:45:30"; box "NULL"; box DBNull.Value |])

        Assert.Equal(t, RecordReaders.ReadTimeOnly(record, 0))
        Assert.Equal(t, RecordReaders.ReadTimeOnly(record, 1))
        Assert.Equal(t, RecordReaders.ReadTimeOnly(record, 2))
        Assert.Equal(t, RecordReaders.ReadTimeOnly(record, 3))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(record, 4))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(record, 5))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(record, -1))

    [<Fact>]
    let ``ReadByteArray handles byte arrays, base64 and string text`` () =
        let bytes = [| 1uy; 2uy; 3uy; 4uy |]
        let base64 = Convert.ToBase64String(bytes)

        let record: IDataRecord =
            MockDataRecord([| box bytes; box base64; box "hello"; box "NULL"; box DBNull.Value |])

        Assert.Equal<byte seq>(bytes, RecordReaders.ReadByteArray(record, 0))
        Assert.Equal<byte seq>(bytes, RecordReaders.ReadByteArray(record, 1))
        Assert.Equal<byte seq>(System.Text.Encoding.UTF8.GetBytes("hello"), RecordReaders.ReadByteArray(record, 2))
        Assert.Empty(RecordReaders.ReadByteArray(record, 3))
        Assert.Empty(RecordReaders.ReadByteArray(record, 4))
        Assert.Empty(RecordReaders.ReadByteArray(record, -1))


module NullableReadersTests =

    [<Fact>]
    let ``ReadNullableInt32 returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord =
            MockDataRecord([| box 42; box "100"; box "NULL"; box DBNull.Value; null |])

        Assert.Equal(Nullable 42, RecordReaders.ReadNullableInt32(record, 0))
        Assert.Equal(Nullable 100, RecordReaders.ReadNullableInt32(record, 1))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 2))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 3))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 4))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, -1))

    [<Fact>]
    let ``ReadNullableInt64 returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord =
            MockDataRecord([| box 9876543210L; box "NULL"; box DBNull.Value |])

        Assert.Equal(Nullable 9876543210L, RecordReaders.ReadNullableInt64(record, 0))
        Assert.Equal(Nullable<int64>(), RecordReaders.ReadNullableInt64(record, 1))
        Assert.Equal(Nullable<int64>(), RecordReaders.ReadNullableInt64(record, 2))

    [<Fact>]
    let ``ReadNullableDecimal returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord =
            MockDataRecord([| box 123.45M; box "1.234,56"; box "N/A"; box DBNull.Value |])

        Assert.Equal(Nullable 123.45M, RecordReaders.ReadNullableDecimal(record, 0))
        Assert.Equal(Nullable 1234.56M, RecordReaders.ReadNullableDecimal(record, 1))
        Assert.Equal(Nullable<decimal>(), RecordReaders.ReadNullableDecimal(record, 2))
        Assert.Equal(Nullable<decimal>(), RecordReaders.ReadNullableDecimal(record, 3))

    [<Fact>]
    let ``ReadNullableDouble returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord =
            MockDataRecord([| box 45.67; box "45,67"; box "NaN"; box DBNull.Value |])

        Assert.Equal(Nullable 45.67, RecordReaders.ReadNullableDouble(record, 0))
        Assert.Equal(Nullable 45.67, RecordReaders.ReadNullableDouble(record, 1))
        Assert.Equal(Nullable<double>(), RecordReaders.ReadNullableDouble(record, 2))
        Assert.Equal(Nullable<double>(), RecordReaders.ReadNullableDouble(record, 3))

    [<Fact>]
    let ``ReadNullableDateTime returns valid Nullable or empty when null or sentinel`` () =
        let dt = DateTime(2026, 11, 16, 12, 0, 0)

        let record: IDataRecord =
            MockDataRecord([| box dt; box "2026-11-16T12:00:00Z"; box "NULL"; box DBNull.Value |])

        Assert.Equal(Nullable dt, RecordReaders.ReadNullableDateTime(record, 0))
        Assert.True(RecordReaders.ReadNullableDateTime(record, 1).HasValue)
        Assert.Equal(Nullable<DateTime>(), RecordReaders.ReadNullableDateTime(record, 2))
        Assert.Equal(Nullable<DateTime>(), RecordReaders.ReadNullableDateTime(record, 3))


module OptionAndObjectReadersTests =

    [<Fact>]
    let ``ReadOption returns Some or None for primitive and string types`` () =
        let record: IDataRecord =
            MockDataRecord([| box 42; box "NULL"; box DBNull.Value; box "hello"; box "" |])

        Assert.Equal(Some 42, RecordReaders.ReadOption<int>(record, 0))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, 1))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, 2))
        Assert.Equal(Some "hello", RecordReaders.ReadOption<string>(record, 3))
        Assert.Equal(None, RecordReaders.ReadOption<string>(record, 4))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, -1))

    [<Fact>]
    let ``ReadObject returns converted object or default value on DBNull`` () =
        let record: IDataRecord = MockDataRecord([| box "123"; box DBNull.Value |])

        let v1 = RecordReaders.ReadObject(record, 0, typeof<int>)
        Assert.Equal(box 123, v1)

        let v2 = RecordReaders.ReadObject(record, 1, typeof<int>)
        Assert.Equal(box 0, v2)

        let v3 = RecordReaders.ReadObject(record, 1, typeof<string>)
        Assert.Equal(box "", v3)


// =========================================================================
// 4. RecordReaders.ConvertValue and Connections.convertValue tests
// =========================================================================

module ConvertValueTests =

    [<Fact>]
    let ``RecordReaders.ConvertValue handles Nullable types seamlessly`` () =
        let r1 = RecordReaders.ConvertValue (box 123) typeof<Nullable<int>>
        Assert.Equal(box 123, r1)

        let r2 = RecordReaders.ConvertValue (box "1.234,56") typeof<Nullable<decimal>>
        Assert.Equal(box 1234.56M, r2)

        let r3 = RecordReaders.ConvertValue (box "NULL") typeof<Nullable<int>>
        Assert.Null(r3)

        let r4 = RecordReaders.ConvertValue null typeof<Nullable<int>>
        Assert.Null(r4)

        let r5 = RecordReaders.ConvertValue DBNull.Value typeof<Nullable<double>>
        Assert.Null(r5)

    [<Fact>]
    let ``RecordReaders.ConvertValue handles option types`` () =
        let r1 = RecordReaders.ConvertValue (box "hello") typeof<string option>
        Assert.Equal(box (Some "hello"), r1)

        let r2 = RecordReaders.ConvertValue (box "NULL") typeof<string option>
        Assert.Null(r2)

        let r3 = RecordReaders.ConvertValue null typeof<int option>
        Assert.Null(r3)

        let r4 = RecordReaders.ConvertValue (box 42) typeof<int option>
        Assert.Equal(box (Some 42), r4)

    [<Fact>]
    let ``RecordReaders.ConvertValue handles TimeOnly and byte array`` () =
        let t = TimeOnly(12, 30, 0)
        let r1 = RecordReaders.ConvertValue (box "12:30:00") typeof<TimeOnly>
        Assert.Equal(box t, r1)

        let r2 = RecordReaders.ConvertValue (box t) typeof<TimeOnly>
        Assert.Equal(box t, r2)

        let bytes = [| 10uy; 20uy; 30uy |]
        let r3 = RecordReaders.ConvertValue (box bytes) typeof<byte[]>
        Assert.Equal(box bytes, r3)

        let b64 = Convert.ToBase64String(bytes)
        let r4 = RecordReaders.ConvertValue (box b64) typeof<byte[]>
        Assert.Equal<byte seq>(bytes, unbox<byte[]> r4)

    [<Fact>]
    let ``Connections.convertValue handles basic types and sentinels`` () =
        // Tests internal Connections.convertValue via InternalsVisibleTo
        let c1 = Connections.convertValue (box "123") typeof<int>
        Assert.Equal(box 123, c1)

        let c2 = Connections.convertValue (box "1.234,56") typeof<decimal>
        Assert.Equal(box 1234.56M, c2)

        let c3 = Connections.convertValue (box "1.234,56") typeof<double>
        Assert.Equal(box 1234.56, c3)

        let c4 = Connections.convertValue (box "true") typeof<bool>
        Assert.Equal(box true, c4)

        let c5 = Connections.convertValue (box "sim") typeof<bool>
        Assert.Equal(box true, c5)

        let c6 = Connections.convertValue (box "nao") typeof<bool>
        Assert.Equal(box false, c6)

        let c7 = Connections.convertValue (box "NULL") typeof<int>
        Assert.Null(c7)

        let c8 = Connections.convertValue DBNull.Value typeof<string>
        Assert.Null(c8)

        let c9 = Connections.convertValue (box Double.PositiveInfinity) typeof<double>
        Assert.Null(c9)

        let c10 = Connections.convertValue (box "2026-11-16") typeof<DateOnly>
        Assert.Equal(box (DateOnly(2026, 11, 16)), c10)


// =========================================================================
// 5. CompiledRecordMapper tests
// =========================================================================

module CompiledRecordMapperTests =

    [<Fact>]
    let ``CompiledRecordMapper maps record types with full fidelity`` () =
        let mapper =
            CompiledRecordMapper.getCompiledRecordMapper<SampleRecord>
                [
                    "Id"
                    "Name"
                    "Amount"
                    "Score"
                    "IsActive"
                    "CreatedDate"
                    "EffectiveDate"
                    "EventTime"
                    "Notes"
                    "Data"
                ]

        let dt = DateTime(2026, 11, 16, 10, 0, 0)
        let d = DateOnly(2026, 11, 16)
        let t = TimeOnly(10, 0, 0)
        let dataBytes = [| 1uy; 2uy; 3uy |]

        let record: IDataRecord =
            MockDataRecord(
                [|
                    "Id", box "101"
                    "Name", box "Test Entity"
                    "Amount", box "1.250,75"
                    "Score", box "98,5"
                    "IsActive", box "true"
                    "CreatedDate", box dt
                    "EffectiveDate", box d
                    "EventTime", box t
                    "Notes", box "Some notes"
                    "Data", box dataBytes
                |]
            )

        let result = mapper record

        Assert.Equal(101, result.Id)
        Assert.Equal("Test Entity", result.Name)
        Assert.Equal(1250.75M, result.Amount)
        Assert.Equal(98.5, result.Score)
        Assert.True(result.IsActive)
        Assert.Equal(dt, result.CreatedDate)
        Assert.Equal(d, result.EffectiveDate)
        Assert.Equal(t, result.EventTime)
        Assert.Equal(Some "Some notes", result.Notes)
        Assert.Equal<byte seq>(dataBytes, result.Data)

    [<Fact>]
    let ``CompiledRecordMapper maps record with missing or sentinel notes to None`` () =
        let mapper =
            CompiledRecordMapper.getCompiledRecordMapper<SampleRecord>
                [
                    "Id"
                    "Name"
                    "Amount"
                    "Score"
                    "IsActive"
                    "CreatedDate"
                    "EffectiveDate"
                    "EventTime"
                    "Notes"
                    "Data"
                ]

        let record: IDataRecord =
            MockDataRecord(
                [|
                    "Id", box 1
                    "Name", box "Entity"
                    "Amount", box 0M
                    "Score", box 0.0
                    "IsActive", box false
                    "CreatedDate", box DateTime.UtcNow
                    "EffectiveDate", box (DateOnly(2026, 1, 1))
                    "EventTime", box (TimeOnly(0, 0, 0))
                    "Notes", box "NULL"
                    "Data", box (Array.empty<byte>)
                |]
            )

        let result = mapper record
        Assert.Equal(None, result.Notes)

    [<Fact>]
    let ``CompiledRecordMapper maps tuples directly`` () =
        let mapper =
            CompiledRecordMapper.getCompiledRecordMapper<int * string * double> [ "A"; "B"; "C" ]

        let record: IDataRecord = MockDataRecord([| box "42"; box "Hello"; box "3.14" |])
        let (a, b, c) = mapper record
        Assert.Equal(42, a)
        Assert.Equal("Hello", b)
        Assert.Equal(3.14, c, 2)

    [<Fact>]
    let ``CompiledRecordMapper maps single scalar and scalar option`` () =
        let scalarMapper = CompiledRecordMapper.getCompiledRecordMapper<int> [ "Count" ]
        let record1: IDataRecord = MockDataRecord([| box "10" |])
        Assert.Equal(10, scalarMapper record1)

        let optMapper = CompiledRecordMapper.getCompiledRecordMapper<int option> [ "Count" ]
        let record2: IDataRecord = MockDataRecord([| box "20" |])
        Assert.Equal(Some 20, optMapper record2)

        let record3: IDataRecord = MockDataRecord([| box "NULL" |])
        Assert.Equal(None, optMapper record3)

    [<Fact>]
    let ``CompiledRecordMapper maps obj array`` () =
        let arrayMapper =
            CompiledRecordMapper.getCompiledRecordMapper<obj array> [ "X"; "Y" ]

        let record: IDataRecord = MockDataRecord([| box 1; box "text" |])
        let arr = arrayMapper record
        Assert.Equal(2, arr.Length)
        Assert.Equal(box 1, arr.[0])
        Assert.Equal(box "text", arr.[1])

// =========================================================================
// 6. Comprehensive RecordReaders.ConvertValue & CompiledRecordMapper tests
// =========================================================================

type NumericAndIdTypesRecord =
    {
        SmallInt: int16
        ByteVal: byte
        FloatVal: single
        IdVal: Guid
    }

module ConvertValueDeepTests =

    [<Fact>]
    let ``ConvertValue converts string to Boolean with affirmative and negative tokens`` () =
        Assert.Equal(box true, RecordReaders.ConvertValue (box "yes") typeof<bool>)
        Assert.Equal(box true, RecordReaders.ConvertValue (box "y") typeof<bool>)
        Assert.Equal(box true, RecordReaders.ConvertValue (box "sim") typeof<bool>)
        Assert.Equal(box true, RecordReaders.ConvertValue (box "1") typeof<bool>)
        Assert.Equal(box true, RecordReaders.ConvertValue (box "true") typeof<bool>)
        Assert.Equal(box false, RecordReaders.ConvertValue (box "no") typeof<bool>)
        Assert.Equal(box false, RecordReaders.ConvertValue (box "0") typeof<bool>)
        Assert.Equal(box false, RecordReaders.ConvertValue (box "random_token") typeof<bool>)

    [<Fact>]
    let ``ConvertValue converts string to DateTime, DateOnly, and TimeOnly`` () =
        let dtExpected = DateTime(2026, 11, 16, 15, 30, 45)
        let dt = RecordReaders.ConvertValue (box "2026-11-16T15:30:45") typeof<DateTime>
        Assert.Equal(box dtExpected, dt)

        let dExpected = DateOnly(2026, 11, 16)
        let d = RecordReaders.ConvertValue (box "2026-11-16") typeof<DateOnly>
        Assert.Equal(box dExpected, d)

        let tExpected = TimeOnly(15, 30, 45)
        let t = RecordReaders.ConvertValue (box "15:30:45") typeof<TimeOnly>
        Assert.Equal(box tExpected, t)

    [<Fact>]
    let ``ConvertValue converts string to Guid`` () =
        let g = Guid.NewGuid()
        let converted = RecordReaders.ConvertValue (box (string g)) typeof<Guid>
        Assert.Equal(box g, converted)

        let invalidG = RecordReaders.ConvertValue (box "not-a-guid") typeof<Guid>
        Assert.Equal(box Guid.Empty, invalidG)

    [<Fact>]
    let ``ConvertValue converts raw objects to DateTime, DateOnly, and TimeOnly`` () =
        let d = DateOnly(2026, 11, 16)
        let dto = DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero)
        let oaDate = DateTime(2026, 11, 16).ToOADate()

        // raw -> DateTime
        Assert.Equal(box (DateTime(2026, 11, 16, 0, 0, 0)), RecordReaders.ConvertValue (box d) typeof<DateTime>)
        Assert.Equal(box dto.DateTime, RecordReaders.ConvertValue (box dto) typeof<DateTime>)
        Assert.Equal(box (DateTime(2026, 11, 16)), RecordReaders.ConvertValue (box oaDate) typeof<DateTime>)

        // raw -> DateOnly
        Assert.Equal(box d, RecordReaders.ConvertValue (box (DateTime(2026, 11, 16))) typeof<DateOnly>)
        Assert.Equal(box d, RecordReaders.ConvertValue (box dto) typeof<DateOnly>)
        Assert.Equal(box d, RecordReaders.ConvertValue (box oaDate) typeof<DateOnly>)

        // raw -> TimeOnly
        let ts = TimeSpan(14, 20, 10)
        let t = TimeOnly(14, 20, 10)
        Assert.Equal(box t, RecordReaders.ConvertValue (box ts) typeof<TimeOnly>)
        Assert.Equal(box t, RecordReaders.ConvertValue (box (DateTime(2026, 11, 16, 14, 20, 10))) typeof<TimeOnly>)

        Assert.Equal(
            box t,
            RecordReaders.ConvertValue (box (DateTimeOffset(2026, 11, 16, 14, 20, 10, TimeSpan.Zero))) typeof<TimeOnly>
        )

    [<Fact>]
    let ``ConvertValue converts raw objects to byte array`` () =
        let bytes = [| 10uy; 20uy; 30uy |]
        Assert.Equal(box bytes, RecordReaders.ConvertValue (box bytes) typeof<byte[]>)

        let base64 = Convert.ToBase64String(bytes)
        let fromBase64 = RecordReaders.ConvertValue (box base64) typeof<byte[]>
        Assert.Equal<byte seq>(bytes, unbox<byte[]> fromBase64)

        let plain = "Hello"
        let fromPlain = RecordReaders.ConvertValue (box plain) typeof<byte[]>
        Assert.Equal<byte seq>(System.Text.Encoding.UTF8.GetBytes(plain), unbox<byte[]> fromPlain)

    [<Fact>]
    let ``ReadDateTime handles integer ticks directly from IDataRecord`` () =
        let dt = DateTime(2026, 11, 16, 12, 0, 0)
        let record = MockDataRecord([| box dt.Ticks |])
        let readDt = RecordReaders.ReadDateTime(record, 0)
        Assert.Equal(dt, readDt)

    [<Fact>]
    let ``ConvertValue returns DefaultForType when type is non-convertible or causes exception`` () =
        let defInt = RecordReaders.ConvertValue (box "not-a-number") typeof<int>
        Assert.Equal(box 0, defInt)

        let defDecimal = RecordReaders.ConvertValue (box "not-decimal") typeof<decimal>
        Assert.Equal(box 0M, defDecimal)

        let defDate = RecordReaders.ConvertValue (box "invalid-date") typeof<DateTime>
        Assert.Equal(box DateTime.MinValue, defDate)

        // Target type that cannot be constructed from raw object
        let defCustom = RecordReaders.ConvertValue (box (DateTime.UtcNow)) typeof<Uri>
        Assert.Null(defCustom)

    [<Fact>]
    let ``CompiledRecordMapper maps record with int16, byte, single, and Guid fields`` () =
        let mapper =
            CompiledRecordMapper.getCompiledRecordMapper<NumericAndIdTypesRecord>
                [ "SmallInt"; "ByteVal"; "FloatVal"; "IdVal" ]

        let g = Guid.NewGuid()

        let record: IDataRecord =
            MockDataRecord(
                [|
                    "SmallInt", box "32000"
                    "ByteVal", box "255"
                    "FloatVal", box "42.5"
                    "IdVal", box (string g)
                |]
            )

        let result = mapper record
        Assert.Equal(32000s, result.SmallInt)
        Assert.Equal(255uy, result.ByteVal)
        Assert.Equal(42.5f, result.FloatVal)
        Assert.Equal(g, result.IdVal)

    [<Fact>]
    let ``RecordReaders reader methods handle null, DBNull, out-of-bounds, and typed-get exceptions`` () =
        let throwingRecord: IDataRecord =
            MockDataRecord(
                [|
                    box null
                    DBNull.Value
                    box "bad_val"
                    box (Guid.NewGuid())
                    box (DateTime(2026, 11, 16))
                    box (TimeSpan(10, 0, 0))
                    box (DateOnly(2026, 11, 16))
                |],
                throwOnTypedGet = true
            )

        // Out of bounds (< 0 and >= FieldCount)
        Assert.Equal(0, RecordReaders.ReadInt32(throwingRecord, -1))
        Assert.Equal(0, RecordReaders.ReadInt32(throwingRecord, 100))
        Assert.Equal(0L, RecordReaders.ReadInt64(throwingRecord, -1))
        Assert.Equal(0s, RecordReaders.ReadInt16(throwingRecord, -1))
        Assert.Equal(0M, RecordReaders.ReadDecimal(throwingRecord, -1))
        Assert.Equal(0.0, RecordReaders.ReadDouble(throwingRecord, -1))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(throwingRecord, -1))
        Assert.False(RecordReaders.ReadBool(throwingRecord, -1))
        Assert.Null(RecordReaders.ReadString(throwingRecord, -1))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(throwingRecord, -1))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(throwingRecord, -1))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(throwingRecord, -1))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(throwingRecord, -1))
        Assert.Empty(RecordReaders.ReadByteArray(throwingRecord, -1))

        // Null and DBNull values
        Assert.Equal(0, RecordReaders.ReadInt32(throwingRecord, 0))
        Assert.Equal(0, RecordReaders.ReadInt32(throwingRecord, 1))
        Assert.Equal(0L, RecordReaders.ReadInt64(throwingRecord, 0))
        Assert.Equal(0L, RecordReaders.ReadInt64(throwingRecord, 1))
        Assert.Equal(0s, RecordReaders.ReadInt16(throwingRecord, 0))
        Assert.Equal(0M, RecordReaders.ReadDecimal(throwingRecord, 0))
        Assert.Equal(0.0, RecordReaders.ReadDouble(throwingRecord, 0))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(throwingRecord, 0))
        Assert.False(RecordReaders.ReadBool(throwingRecord, 0))
        Assert.Null(RecordReaders.ReadString(throwingRecord, 0))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(throwingRecord, 0))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(throwingRecord, 0))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(throwingRecord, 0))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(throwingRecord, 0))
        Assert.Empty(RecordReaders.ReadByteArray(throwingRecord, 0))

        // Typed get throwing -> fallback to GetValue
        let gRecord: IDataRecord =
            MockDataRecord([| box (Guid.NewGuid()) |], throwOnTypedGet = true)

        Assert.NotEqual(Guid.Empty, RecordReaders.ReadGuid(gRecord, 0))

        let dtRecord: IDataRecord =
            MockDataRecord([| box (DateTime(2026, 11, 16)) |], throwOnTypedGet = true)

        Assert.Equal(DateTime(2026, 11, 16), RecordReaders.ReadDateTime(dtRecord, 0))

        let strRecord: IDataRecord =
            MockDataRecord([| box "ValidString" |], throwOnTypedGet = true)

        Assert.Equal("ValidString", RecordReaders.ReadString(strRecord, 0))

    [<Fact>]
    let ``RecordReaders ConvertValue handles all object and primitive fallback branches`` () =
        // Target obj
        let anyObj = obj ()
        Assert.Same(anyObj, RecordReaders.ConvertValue anyObj typeof<obj>)

        // Nullable with null and sentinel
        Assert.Null(RecordReaders.ConvertValue null typeof<Nullable<int>>)
        Assert.Null(RecordReaders.ConvertValue DBNull.Value typeof<Nullable<int>>)
        Assert.Null(RecordReaders.ConvertValue (box "N/A") typeof<Nullable<int>>)

        // Non-string decimal
        Assert.Equal(box 0M, RecordReaders.ConvertValue (box Single.NaN) typeof<decimal>)
        Assert.Equal(box 123M, RecordReaders.ConvertValue (box "123") typeof<decimal>)

        // Non-string int
        Assert.Equal(box 0, RecordReaders.ConvertValue (box Double.NaN) typeof<int>)
        Assert.Equal(box 100, RecordReaders.ConvertValue (box 99.8M) typeof<int>)
        Assert.Equal(box 50, RecordReaders.ConvertValue (box 50L) typeof<int>)

        // Non-string int64
        Assert.Equal(box 0L, RecordReaders.ConvertValue (box Double.PositiveInfinity) typeof<int64>)
        Assert.Equal(box 200L, RecordReaders.ConvertValue (box 199.9M) typeof<int64>)
        Assert.Equal(box 75L, RecordReaders.ConvertValue (box 75) typeof<int64>)

        // Non-string DateTime & DateOnly
        let dto = DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)
        Assert.Equal(box (DateTime(2026, 11, 16, 8, 0, 0)), RecordReaders.ConvertValue (box dto) typeof<DateTime>)
        Assert.Equal(box (DateOnly(2026, 11, 16)), RecordReaders.ConvertValue (box dto) typeof<DateOnly>)

        // Non-string TimeOnly
        let tVal = TimeOnly(8, 30, 0)
        Assert.Equal(box tVal, RecordReaders.ConvertValue (box tVal) typeof<TimeOnly>)
        Assert.Equal(box tVal, RecordReaders.ConvertValue (box (TimeSpan(8, 30, 0))) typeof<TimeOnly>)
        Assert.Equal(box tVal, RecordReaders.ConvertValue (box (DateTime(2026, 11, 16, 8, 30, 0))) typeof<TimeOnly>)
        Assert.Equal(box (TimeOnly(8, 0, 0)), RecordReaders.ConvertValue (box dto) typeof<TimeOnly>)
        Assert.Equal(box TimeOnly.MinValue, RecordReaders.ConvertValue (box (obj ())) typeof<TimeOnly>)

        // Non-string byte[]
        Assert.Equal(box (Array.empty<byte>), RecordReaders.ConvertValue (box (obj ())) typeof<byte[]>)

        // String DateTime and TimeOnly fallbacks
        let dtFromTimeStr =
            RecordReaders.ConvertValue (box "2026-11-16T12:00:00") typeof<DateTime>

        Assert.Equal(box (DateTime(2026, 11, 16, 12, 0, 0)), dtFromTimeStr)

        let dFromDtStr =
            RecordReaders.ConvertValue (box "2026-11-16T12:00:00") typeof<DateOnly>

        Assert.Equal(box (DateOnly(2026, 11, 16)), dFromDtStr)

        let tFromDtStr =
            RecordReaders.ConvertValue (box "2026-11-16T12:00:00") typeof<TimeOnly>

        Assert.Equal(box (TimeOnly(12, 0, 0)), tFromDtStr)

        // String double fallback failing -> 0.0
        Assert.Equal(box 0.0, RecordReaders.ConvertValue (box "not_double") typeof<double>)

        // CompiledRecordMapper for scalar null
        let scalarMapper = CompiledRecordMapper.getCompiledRecordMapper<string> [ "Col" ]
        let nullRec: IDataRecord = MockDataRecord([| box null |])
        Assert.Null(scalarMapper nullRec)

    [<Fact>]
    let ``RecordReaders TryParseNormalizedDecimal and Double cover all format branches`` () =
        // Comma only
        let okDec1, dec1 = RecordReaders.TryParseNormalizedDecimal "123,45"
        Assert.True(okDec1)
        Assert.Equal(123.45M, dec1)

        let okDecBad1, _ = RecordReaders.TryParseNormalizedDecimal "abc,def"
        Assert.False(okDecBad1)

        // Dot only
        let okDec2, dec2 = RecordReaders.TryParseNormalizedDecimal "123.45"
        Assert.True(okDec2)
        Assert.Equal(123.45M, dec2)

        let okDecBad2, _ = RecordReaders.TryParseNormalizedDecimal "abc.def"
        Assert.False(okDecBad2)

        // Comma and Dot: lastDot > lastComma
        let okDec3, dec3 = RecordReaders.TryParseNormalizedDecimal "1,234.56"
        Assert.True(okDec3)
        Assert.Equal(1234.56M, dec3)

        // Comma and Dot: lastComma > lastDot
        let okDec4, dec4 = RecordReaders.TryParseNormalizedDecimal "1.234,56"
        Assert.True(okDec4)
        Assert.Equal(1234.56M, dec4)

        let okDecBad3, _ = RecordReaders.TryParseNormalizedDecimal "1,2.a"
        Assert.False(okDecBad3)

        // Neither
        let okDec5, dec5 = RecordReaders.TryParseNormalizedDecimal "1234"
        Assert.True(okDec5)
        Assert.Equal(1234M, dec5)

        let okDecBad4, _ = RecordReaders.TryParseNormalizedDecimal "invalid"
        Assert.False(okDecBad4)

        // TryParseNormalizedDouble - comma only
        let okDbl1, dbl1 = RecordReaders.TryParseNormalizedDouble "123,45"
        Assert.True(okDbl1)
        Assert.Equal(123.45, dbl1)

        let okDblBad1, _ = RecordReaders.TryParseNormalizedDouble "abc,def"
        Assert.False(okDblBad1)

        // TryParseNormalizedDouble - dot only
        let okDbl2, dbl2 = RecordReaders.TryParseNormalizedDouble "123.45"
        Assert.True(okDbl2)
        Assert.Equal(123.45, dbl2)

        let okDblBad2, _ = RecordReaders.TryParseNormalizedDouble "abc.def"
        Assert.False(okDblBad2)

        // TryParseNormalizedDouble - comma and dot
        let okDbl3, dbl3 = RecordReaders.TryParseNormalizedDouble "1,234.56"
        Assert.True(okDbl3)
        Assert.Equal(1234.56, dbl3)

        let okDbl4, dbl4 = RecordReaders.TryParseNormalizedDouble "1.234,56"
        Assert.True(okDbl4)
        Assert.Equal(1234.56, dbl4)

        let okDblBad3, _ = RecordReaders.TryParseNormalizedDouble "1,2.a"
        Assert.False(okDblBad3)

        // TryParseNormalizedDouble - neither
        let okDbl5, dbl5 = RecordReaders.TryParseNormalizedDouble "1234"
        Assert.True(okDbl5)
        Assert.Equal(1234.0, dbl5)

        let okDblBad4, _ = RecordReaders.TryParseNormalizedDouble "invalid"
        Assert.False(okDblBad4)

    [<Fact>]
    let ``RecordReaders Read primitives cover all type conversions, sentinels, and fallbacks`` () =
        let vals: obj array =
            [|
                box 12s // 0: int16
                box 5uy // 1: byte
                box 50.2M // 2: decimal
                box 123.0 // 3: double
                box Double.NaN // 4: non-finite double
                box 45.0f // 5: single
                box Single.PositiveInfinity // 6: non-finite single
                box true // 7: bool true
                box false // 8: bool false
                box "-" // 9: sentinel
                box "123" // 10: numeric string
                box "45.6" // 11: float string
                box "invalid" // 12: invalid string
                box null // 13: null
                DBNull.Value // 14: DBNull
                box 1000L // 15: int64
                box "yes" // 16: string bool yes
                box "1" // 17: string bool 1
                box "sim" // 18: string bool sim
                box (Guid.NewGuid()) // 19: guid
                box (DateTime(2026, 11, 16, 10, 30, 0)) // 20: datetime
                box (DateOnly(2026, 11, 16)) // 21: dateonly
                box (TimeOnly(10, 30, 0)) // 22: timeonly
                box (TimeSpan(10, 30, 0)) // 23: timespan
                box [| 1uy; 2uy; 3uy |] // 24: byte array
                box "SGVsbG8=" // 25: base64 string
            |]

        let mockRec: IDataRecord = MockDataRecord(vals)

        // ReadInt32
        Assert.Equal(12, RecordReaders.ReadInt32(mockRec, 0))
        Assert.Equal(5, RecordReaders.ReadInt32(mockRec, 1))
        Assert.Equal(50, RecordReaders.ReadInt32(mockRec, 2))
        Assert.Equal(123, RecordReaders.ReadInt32(mockRec, 3))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 4))
        Assert.Equal(45, RecordReaders.ReadInt32(mockRec, 5))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 6))
        Assert.Equal(1, RecordReaders.ReadInt32(mockRec, 7))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 8))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 9))
        Assert.Equal(123, RecordReaders.ReadInt32(mockRec, 10))
        Assert.Equal(46, RecordReaders.ReadInt32(mockRec, 11))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 12))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 13))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, 14))
        Assert.Equal(0, RecordReaders.ReadInt32(mockRec, -1))

        // ReadInt64
        Assert.Equal(12L, RecordReaders.ReadInt64(mockRec, 0))
        Assert.Equal(5L, RecordReaders.ReadInt64(mockRec, 1))
        Assert.Equal(50L, RecordReaders.ReadInt64(mockRec, 2))
        Assert.Equal(123L, RecordReaders.ReadInt64(mockRec, 3))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, 4))
        Assert.Equal(45L, RecordReaders.ReadInt64(mockRec, 5))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, 6))
        Assert.Equal(1L, RecordReaders.ReadInt64(mockRec, 7))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, 8))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, 9))
        Assert.Equal(123L, RecordReaders.ReadInt64(mockRec, 10))
        Assert.Equal(46L, RecordReaders.ReadInt64(mockRec, 11))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, 12))
        Assert.Equal(1000L, RecordReaders.ReadInt64(mockRec, 15))
        Assert.Equal(0L, RecordReaders.ReadInt64(mockRec, -1))

        // ReadInt16
        Assert.Equal(12s, RecordReaders.ReadInt16(mockRec, 0))
        Assert.Equal(5s, RecordReaders.ReadInt16(mockRec, 1))
        Assert.Equal(123s, RecordReaders.ReadInt16(mockRec, 10))
        Assert.Equal(46s, RecordReaders.ReadInt16(mockRec, 11))
        Assert.Equal(0s, RecordReaders.ReadInt16(mockRec, 9))
        Assert.Equal(0s, RecordReaders.ReadInt16(mockRec, 12))
        Assert.Equal(0s, RecordReaders.ReadInt16(mockRec, -1))

        // ReadByte
        Assert.Equal(5uy, RecordReaders.ReadByte(mockRec, 1))
        Assert.Equal(123uy, RecordReaders.ReadByte(mockRec, 10))
        Assert.Equal(0uy, RecordReaders.ReadByte(mockRec, 12))
        Assert.Equal(0uy, RecordReaders.ReadByte(mockRec, -1))

        // ReadDecimal
        Assert.Equal(50.2M, RecordReaders.ReadDecimal(mockRec, 2))
        Assert.Equal(123M, RecordReaders.ReadDecimal(mockRec, 3))
        Assert.Equal(0M, RecordReaders.ReadDecimal(mockRec, 4))
        Assert.Equal(45M, RecordReaders.ReadDecimal(mockRec, 5))
        Assert.Equal(0M, RecordReaders.ReadDecimal(mockRec, 6))
        Assert.Equal(0M, RecordReaders.ReadDecimal(mockRec, 9))
        Assert.Equal(123M, RecordReaders.ReadDecimal(mockRec, 10))
        Assert.Equal(0M, RecordReaders.ReadDecimal(mockRec, 12))
        Assert.Equal(0M, RecordReaders.ReadDecimal(mockRec, -1))

        // ReadDouble
        Assert.Equal(123.0, RecordReaders.ReadDouble(mockRec, 3))
        Assert.Equal(0.0, RecordReaders.ReadDouble(mockRec, 4))
        Assert.Equal(45.0, RecordReaders.ReadDouble(mockRec, 5))
        Assert.Equal(0.0, RecordReaders.ReadDouble(mockRec, 6))
        Assert.Equal(50.2, Math.Round(RecordReaders.ReadDouble(mockRec, 2), 1))
        Assert.Equal(0.0, RecordReaders.ReadDouble(mockRec, 9))
        Assert.Equal(123.0, RecordReaders.ReadDouble(mockRec, 10))
        Assert.Equal(45.6, RecordReaders.ReadDouble(mockRec, 11))
        Assert.Equal(0.0, RecordReaders.ReadDouble(mockRec, 12))
        Assert.Equal(0.0, RecordReaders.ReadDouble(mockRec, -1))

        // ReadSingle
        Assert.Equal(45.0f, RecordReaders.ReadSingle(mockRec, 5))
        Assert.Equal(123.0f, RecordReaders.ReadSingle(mockRec, 3))
        Assert.Equal(0.0f, RecordReaders.ReadSingle(mockRec, -1))

        // ReadBool & ReadBoolean
        Assert.True(RecordReaders.ReadBool(mockRec, 7))
        Assert.False(RecordReaders.ReadBool(mockRec, 8))
        Assert.False(RecordReaders.ReadBool(mockRec, 9))
        Assert.False(RecordReaders.ReadBool(mockRec, 10))
        Assert.False(RecordReaders.ReadBool(mockRec, 12))
        Assert.True(RecordReaders.ReadBool(mockRec, 16))
        Assert.True(RecordReaders.ReadBool(mockRec, 17))
        Assert.True(RecordReaders.ReadBool(mockRec, 18))
        Assert.True(RecordReaders.ReadBoolean(mockRec, 7))
        Assert.False(RecordReaders.ReadBool(mockRec, -1))

        // ReadString
        Assert.Equal("123", RecordReaders.ReadString(mockRec, 10))
        Assert.Null(RecordReaders.ReadString(mockRec, 13))
        Assert.Null(RecordReaders.ReadString(mockRec, 14))
        Assert.Null(RecordReaders.ReadString(mockRec, -1))

        // ReadGuid
        let guidVal = unbox<Guid> vals.[19]
        Assert.Equal(guidVal, RecordReaders.ReadGuid(mockRec, 19))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(mockRec, 12))
        Assert.Equal(Guid.Empty, RecordReaders.ReadGuid(mockRec, -1))

        // ReadDateTime
        Assert.Equal(DateTime(2026, 11, 16, 10, 30, 0), RecordReaders.ReadDateTime(mockRec, 20))
        Assert.Equal(DateTime(2026, 11, 16), RecordReaders.ReadDateTime(mockRec, 21))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(mockRec, 9))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(mockRec, 12))
        Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(mockRec, -1))

        // ReadDateOnly
        Assert.Equal(DateOnly(2026, 11, 16), RecordReaders.ReadDateOnly(mockRec, 21))
        Assert.Equal(DateOnly(2026, 11, 16), RecordReaders.ReadDateOnly(mockRec, 20))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(mockRec, 9))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(mockRec, 12))
        Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(mockRec, -1))

        // ReadTimeOnly
        Assert.Equal(TimeOnly(10, 30, 0), RecordReaders.ReadTimeOnly(mockRec, 22))
        Assert.Equal(TimeOnly(10, 30, 0), RecordReaders.ReadTimeOnly(mockRec, 23))
        Assert.Equal(TimeOnly(10, 30, 0), RecordReaders.ReadTimeOnly(mockRec, 20))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(mockRec, 9))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(mockRec, 12))
        Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(mockRec, -1))

        // ReadByteArray
        Assert.Equal<byte[]>([| 1uy; 2uy; 3uy |], RecordReaders.ReadByteArray(mockRec, 24))
        Assert.Equal<byte[]>(System.Text.Encoding.UTF8.GetBytes("Hello"), RecordReaders.ReadByteArray(mockRec, 25))
        Assert.Empty(RecordReaders.ReadByteArray(mockRec, 9))
        Assert.Empty(RecordReaders.ReadByteArray(mockRec, -1))

        // ReadNullable*
        Assert.Equal(Nullable(12), RecordReaders.ReadNullableInt32(mockRec, 0))
        Assert.False((RecordReaders.ReadNullableInt32(mockRec, 9)).HasValue)
        Assert.False((RecordReaders.ReadNullableInt32(mockRec, 13)).HasValue)
        Assert.False((RecordReaders.ReadNullableInt32(mockRec, -1)).HasValue)

        Assert.Equal(Nullable(1000L), RecordReaders.ReadNullableInt64(mockRec, 15))
        Assert.False((RecordReaders.ReadNullableInt64(mockRec, 9)).HasValue)

        Assert.Equal(Nullable(50.2M), RecordReaders.ReadNullableDecimal(mockRec, 2))
        Assert.False((RecordReaders.ReadNullableDecimal(mockRec, 9)).HasValue)

        Assert.Equal(Nullable(123.0), RecordReaders.ReadNullableDouble(mockRec, 3))
        Assert.False((RecordReaders.ReadNullableDouble(mockRec, 9)).HasValue)

        Assert.Equal(Nullable(DateTime(2026, 11, 16, 10, 30, 0)), RecordReaders.ReadNullableDateTime(mockRec, 20))
        Assert.False((RecordReaders.ReadNullableDateTime(mockRec, 9)).HasValue)

        // DefaultForType
        Assert.Equal(box "", RecordReaders.DefaultForType typeof<string>)
        Assert.Equal(box DateTime.MinValue, RecordReaders.DefaultForType typeof<DateTime>)
        Assert.Equal(box DateOnly.MinValue, RecordReaders.DefaultForType typeof<DateOnly>)
        Assert.Equal(box TimeOnly.MinValue, RecordReaders.DefaultForType typeof<TimeOnly>)
        Assert.Equal(box Guid.Empty, RecordReaders.DefaultForType typeof<Guid>)
        Assert.Equal(box (Array.empty<byte>), RecordReaders.DefaultForType typeof<byte[]>)
        Assert.Equal(box 0, RecordReaders.DefaultForType typeof<int>)
        Assert.Null(RecordReaders.DefaultForType typeof<Nullable<int>>)
        Assert.Null(RecordReaders.DefaultForType typeof<obj>)

        // ReadOption
        Assert.Equal(Some 123, RecordReaders.ReadOption<int>(mockRec, 10))
        Assert.Equal(None, RecordReaders.ReadOption<int>(mockRec, 9))
        Assert.Equal(None, RecordReaders.ReadOption<int>(mockRec, 13))
        Assert.Equal(None, RecordReaders.ReadOption<int>(mockRec, -1))

        // ReadObject
        Assert.Equal(box 123, RecordReaders.ReadObject(mockRec, 10, typeof<int>))
        Assert.Equal(box 0, RecordReaders.ReadObject(mockRec, -1, typeof<int>))

    [<Fact>]
    let ``CompiledRecordMapper handles obj array, tuples, option, and scalar types`` () =
        let vals: obj array = [| box 42; box "Hello" |]
        let mockRec: IDataRecord = MockDataRecord(vals)

        // 1. obj array
        let arrayMapper =
            CompiledRecordMapper.getCompiledRecordMapper<obj array> [ "Col0"; "Col1" ]

        let arr = arrayMapper mockRec
        Assert.Equal(2, arr.Length)
        Assert.Equal(box 42, arr.[0])
        Assert.Equal(box "Hello", arr.[1])

        // 2. tuple (int * string)
        let tupleMapper =
            CompiledRecordMapper.getCompiledRecordMapper<int * string> [ "Col0"; "Col1" ]

        let (num, text) = tupleMapper mockRec
        Assert.Equal(42, num)
        Assert.Equal("Hello", text)

        // 3. option (int option)
        let optMapper = CompiledRecordMapper.getCompiledRecordMapper<int option> [ "Col0" ]
        let optVal = optMapper mockRec
        Assert.Equal(Some 42, optVal)

        // 4. scalar (int)
        let intMapper = CompiledRecordMapper.getCompiledRecordMapper<int> [ "Col0" ]
        let intVal = intMapper mockRec
        Assert.Equal(42, intVal)

    [<Fact>]
    let ``RecordReaders ConvertValue additional edge cases and fallbacks`` () =
        // String into int16 via ChangeType
        Assert.Equal(box 123s, RecordReaders.ConvertValue (box "123") typeof<int16>)

        // String into invalid DateTime / DateOnly / TimeOnly / Guid
        Assert.Equal(box DateTime.MinValue, RecordReaders.ConvertValue (box "not_a_date") typeof<DateTime>)
        Assert.Equal(box DateOnly.MinValue, RecordReaders.ConvertValue (box "not_a_date") typeof<DateOnly>)
        Assert.Equal(box TimeOnly.MinValue, RecordReaders.ConvertValue (box "not_a_time") typeof<TimeOnly>)
        Assert.Equal(box Guid.Empty, RecordReaders.ConvertValue (box "not_a_guid") typeof<Guid>)

        // String into invalid base64 (falls back to UTF8 bytes)
        let utf8Res = RecordReaders.ConvertValue (box "hello_world!?") typeof<byte[]>
        Assert.Equal<byte[]>(System.Text.Encoding.UTF8.GetBytes("hello_world!?"), unbox<byte[]> utf8Res)

        // Non-string into decimal
        Assert.Equal(box 123M, RecordReaders.ConvertValue (box 123L) typeof<decimal>)
        Assert.Equal(box 123M, RecordReaders.ConvertValue (box 123s) typeof<decimal>)

        // Non-string into int
        Assert.Equal(box 123, RecordReaders.ConvertValue (box 123s) typeof<int>)

        // Non-string into int64
        Assert.Equal(box 123L, RecordReaders.ConvertValue (box 123s) typeof<int64>)

        // Non-string into DateTime fallback
        Assert.Equal(box DateTime.MinValue, RecordReaders.ConvertValue (box (obj ())) typeof<DateTime>)

        // DateOnly conversions
        let dVal = DateOnly(2026, 11, 16)
        Assert.Equal(box dVal, RecordReaders.ConvertValue (box dVal) typeof<DateOnly>)

        // Non-string into TimeOnly fallback
        Assert.Equal(box TimeOnly.MinValue, RecordReaders.ConvertValue (box (obj ())) typeof<TimeOnly>)

        // Non-string into byte[] fallback
        Assert.Equal(box (Array.empty<byte>), RecordReaders.ConvertValue (box (obj ())) typeof<byte[]>)

        // Non-convertible type fallback
        Assert.Null(RecordReaders.ConvertValue (box (obj ())) typeof<Uri>)
        Assert.Null(RecordReaders.ConvertValue (box "http://localhost") typeof<Uri>)

        // ReadOption with invalid conversion returning None
        let recWithBadStr: IDataRecord = MockDataRecord([| box "invalid_uri" |])
        let optRes = RecordReaders.ReadOption<Uri>(recWithBadStr, 0)
        Assert.Equal(None, optRes)

        let recWithSentinel: IDataRecord = MockDataRecord([| box "N/A" |])
        let optSentinel = RecordReaders.ReadOption<int>(recWithSentinel, 0)
        Assert.Equal(None, optSentinel)

type CustomGuidContainer(g: Guid) =
    override _.ToString() = g.ToString()

type RecordWithOtherField = { Id: int; Other: Uri }

[<Fact>]
let ``RecordReaders Read primitives unexpected object and fallback branches`` () =
    // 1. Unexpected obj() causing Convert.* to throw and return fallback defaults
    let unconvertible = obj ()
    let recThrowing: IDataRecord = MockDataRecord([| unconvertible |])

    Assert.Equal(0, RecordReaders.ReadInt32(recThrowing, 0))
    Assert.Equal(0L, RecordReaders.ReadInt64(recThrowing, 0))
    Assert.Equal(0s, RecordReaders.ReadInt16(recThrowing, 0))
    Assert.Equal(0M, RecordReaders.ReadDecimal(recThrowing, 0))
    Assert.Equal(0.0, RecordReaders.ReadDouble(recThrowing, 0))
    Assert.False(RecordReaders.ReadBool(recThrowing, 0))
    Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(recThrowing, 0))
    Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(recThrowing, 0))
    Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(recThrowing, 0))
    Assert.Empty(RecordReaders.ReadByteArray(recThrowing, 0))

    // 2. Custom object with Guid string representation
    let g = Guid.NewGuid()
    let customG = CustomGuidContainer(g)
    let recGuid: IDataRecord = MockDataRecord([| box customG |])
    Assert.Equal(g, RecordReaders.ReadGuid(recGuid, 0))

    // 3. CompiledRecordMapper with non-primitive field (hits lines 712-714) and column count mismatch (hits line 670)
    let mapper =
        CompiledRecordMapper.getCompiledRecordMapper<RecordWithOtherField> [ "NonExistent" ]

    let recObj: IDataRecord = MockDataRecord([| box 10 |])
    let mapped = mapper recObj
    Assert.Equal(10, mapped.Id)
    Assert.Null(mapped.Other)

[<Fact>]
let ``RecordReaders Read primitives with null object values return type defaults`` () =
    let recNull: IDataRecord = MockDataRecord([| null |])
    Assert.Equal(0, RecordReaders.ReadInt32(recNull, 0))
    Assert.Equal(0L, RecordReaders.ReadInt64(recNull, 0))
    Assert.Equal(0s, RecordReaders.ReadInt16(recNull, 0))
    Assert.Equal(0M, RecordReaders.ReadDecimal(recNull, 0))
    Assert.Equal(0.0, RecordReaders.ReadDouble(recNull, 0))
    Assert.False(RecordReaders.ReadBool(recNull, 0))
    Assert.Equal(DateTime.MinValue, RecordReaders.ReadDateTime(recNull, 0))
    Assert.Equal(DateOnly.MinValue, RecordReaders.ReadDateOnly(recNull, 0))
    Assert.Equal(TimeOnly.MinValue, RecordReaders.ReadTimeOnly(recNull, 0))
    Assert.Empty(RecordReaders.ReadByteArray(recNull, 0))

[<Fact>]
let ``RecordReaders ReadDouble with int64 and float finite checks`` () =
    let recInt64: IDataRecord = MockDataRecord([| box 1234567890123L |])
    Assert.Equal(1234567890123.0, RecordReaders.ReadDouble(recInt64, 0))

    let recNanFloat: IDataRecord = MockDataRecord([| box Single.NaN |])
    Assert.Equal(0.0f, RecordReaders.ReadSingle(recNanFloat, 0))

    let recInfFloat: IDataRecord = MockDataRecord([| box Single.PositiveInfinity |])
    Assert.Equal(0.0f, RecordReaders.ReadSingle(recInfFloat, 0))

[<Fact>]
let ``RecordReaders ReadDateOnly and ReadTimeOnly with DateTimeOffset`` () =
    let dto = DateTimeOffset(2025, 6, 15, 14, 30, 0, TimeSpan.FromHours(-3.0))
    let recDto: IDataRecord = MockDataRecord([| box dto |])
    Assert.Equal(DateOnly(2025, 6, 15), RecordReaders.ReadDateOnly(recDto, 0))
    Assert.Equal(TimeOnly(14, 30, 0), RecordReaders.ReadTimeOnly(recDto, 0))

    // String date with time
    let recTimeStr: IDataRecord = MockDataRecord([| box "2025-06-15 14:30:00" |])
    Assert.Equal(TimeOnly(14, 30, 0), RecordReaders.ReadTimeOnly(recTimeStr, 0))

[<Fact>]
let ``RecordReaders ReadNullable functions with DBNull return Nullable without value`` () =
    let recDbNull: IDataRecord = MockDataRecord([| box DBNull.Value |])
    Assert.False(RecordReaders.ReadNullableInt32(recDbNull, 0).HasValue)
    Assert.False(RecordReaders.ReadNullableInt64(recDbNull, 0).HasValue)
    Assert.False(RecordReaders.ReadNullableDecimal(recDbNull, 0).HasValue)
    Assert.False(RecordReaders.ReadNullableDouble(recDbNull, 0).HasValue)
    Assert.False(RecordReaders.ReadNullableDateTime(recDbNull, 0).HasValue)

[<Fact>]
let ``RecordReaders ConvertValue converts string decimal to clamped int and int64`` () =
    let resInt = RecordReaders.ConvertValue "1234.56" typeof<int>
    Assert.Equal(box 1235, resInt)

    let resInt64 = RecordReaders.ConvertValue "9876543210.45" typeof<int64>
    Assert.Equal(box 9876543210L, resInt64)

    let resInt64Str = RecordReaders.ConvertValue "9876543210" typeof<int64>
    Assert.Equal(box 9876543210L, resInt64Str)

    let resInt64Invalid = RecordReaders.ConvertValue "not-a-number" typeof<int64>
    Assert.Equal(box 0L, resInt64Invalid)
