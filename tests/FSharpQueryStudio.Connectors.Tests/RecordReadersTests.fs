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
        member _.GetDataTypeName(i) = if isNull colValues.[i] then "System.Object" else colValues.[i].GetType().Name
        member _.GetFieldType(i) = if isNull colValues.[i] then typeof<obj> else colValues.[i].GetType()
        member _.GetValue(i) = colValues.[i]
        member _.GetValues(valuesArr) =
            let count = min valuesArr.Length colValues.Length
            Array.Copy(colValues, valuesArr, count)
            count
        member _.GetOrdinal(name) =
            match colNames |> Array.tryFindIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) with
            | Some idx -> idx
            | None -> raise (IndexOutOfRangeException($"Field '{name}' not found"))
        member _.GetBoolean(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToBoolean(colValues.[i])
        member _.GetByte(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToByte(colValues.[i])
        member _.GetBytes(i, fieldOffset, buffer, bufferoffset, length) = 0L
        member _.GetChar(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToChar(colValues.[i])
        member _.GetChars(i, fieldOffset, buffer, bufferoffset, length) = 0L
        member _.GetGuid(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            match colValues.[i] with
            | :? Guid as g -> g
            | :? string as s -> Guid.Parse(s)
            | :? (byte array) as b -> Guid(b)
            | _ -> raise (InvalidCastException())
        member _.GetInt16(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToInt16(colValues.[i])
        member _.GetInt32(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToInt32(colValues.[i])
        member _.GetInt64(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToInt64(colValues.[i])
        member _.GetFloat(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToSingle(colValues.[i])
        member _.GetDouble(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToDouble(colValues.[i])
        member _.GetString(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToString(colValues.[i])
        member _.GetDecimal(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToDecimal(colValues.[i])
        member _.GetDateTime(i) =
            if shouldThrowOnTyped then raise (InvalidCastException())
            Convert.ToDateTime(colValues.[i])
        member _.GetData(i) = raise (NotSupportedException())
        member _.IsDBNull(i) =
            if i < 0 || i >= colValues.Length then true
            else
                let v = colValues.[i]
                isNull v || Convert.IsDBNull(v)
        member _.Item with get (i: int) = colValues.[i]
        member _.Item with get (name: string) =
            let idx = (colNames |> Array.findIndex (fun n -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            colValues.[idx]

// Test records for CompiledRecordMapper tests
type SampleRecord = {
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

type SimpleScalarRecord = {
    Count: int
}

// =========================================================================
// 1. TryParseNormalizedDecimal & TryParseNormalizedDouble tests
// =========================================================================

module DecimalNormalizationTests =

    [<Theory>]
    [<InlineData("1.234,56", 1234.56)>]
    [<InlineData("-1.234,56", -1234.56)>]
    [<InlineData("1.234.567,89", 1234567.89)>]
    [<InlineData("-1.234.567,89", -1234567.89)>]
    let ``TryParseNormalizedDecimal handles European and Brazilian formats with thousand dots and decimal comma`` (input: string, expected: double) =
        let success, result = RecordReaders.TryParseNormalizedDecimal input
        Assert.True(success)
        Assert.Equal(decimal expected, result)

    [<Theory>]
    [<InlineData("1,234.56", 1234.56)>]
    [<InlineData("-1,234.56", -1234.56)>]
    [<InlineData("1,234,567.89", 1234567.89)>]
    [<InlineData("-1,234,567.89", -1234567.89)>]
    let ``TryParseNormalizedDecimal handles US and Invariant formats with thousand commas and decimal dot`` (input: string, expected: double) =
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
        let record: IDataRecord = MockDataRecord([|
            box (int64 Int32.MaxValue + 500L)
            box (int64 Int32.MinValue - 500L)
            box Int64.MaxValue
            box Int64.MinValue
        |])
        Assert.Equal(Int32.MaxValue, RecordReaders.ReadInt32(record, 0))
        Assert.Equal(Int32.MinValue, RecordReaders.ReadInt32(record, 1))
        Assert.Equal(Int32.MaxValue, RecordReaders.ReadInt32(record, 2))
        Assert.Equal(Int32.MinValue, RecordReaders.ReadInt32(record, 3))

    [<Fact>]
    let ``ReadInt32 converts single double decimal with rounding and clamping`` () =
        let record: IDataRecord = MockDataRecord([|
            box 42.7f
            box 42.2
            box 99.6M
            box 1e12
            box -1e12
            box Double.PositiveInfinity
            box Double.NaN
        |])
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
        let record: IDataRecord = MockDataRecord([|
            box " \" 1,234.56 \" "
            box " '42' "
            box " -500 "
            box "123"
        |])
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
        let record: IDataRecord = MockDataRecord([|
            box 9876543210123L
            box 42
            box 100s
            box 12uy
        |])
        Assert.Equal(9876543210123L, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(42L, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(100L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(12L, RecordReaders.ReadInt64(record, 3))

    [<Fact>]
    let ``ReadInt64 clamps decimal double and single`` () =
        let record: IDataRecord = MockDataRecord([|
            box 1e25
            box -1e25
            box 9876543210.4M
            box 123.7f
            box Double.PositiveInfinity
            box Double.NaN
        |])
        Assert.Equal(Int64.MaxValue, RecordReaders.ReadInt64(record, 0))
        Assert.Equal(Int64.MinValue, RecordReaders.ReadInt64(record, 1))
        Assert.Equal(9876543210L, RecordReaders.ReadInt64(record, 2))
        Assert.Equal(124L, RecordReaders.ReadInt64(record, 3))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 4))
        Assert.Equal(0L, RecordReaders.ReadInt64(record, 5))

    [<Fact>]
    let ``ReadInt64 handles bool and strings with formatting`` () =
        let record: IDataRecord = MockDataRecord([|
            box true
            box false
            box " 9876543210 "
            box " \"1,234,567.89\" "
        |])
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
        let record: IDataRecord = MockDataRecord([|
            box 1234s
            box 50000
            box -50000
            box 1000000L
            box -1000000L
            box 200uy
            box " 300 "
            box "1,200.4"
        |], throwOnTypedGet = true)

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
        let record: IDataRecord = MockDataRecord([|
            box 128uy
            box 255
            box "200"
            box "invalid"
            box DBNull.Value
        |], throwOnTypedGet = true)

        Assert.Equal(128uy, RecordReaders.ReadByte(record, 0))
        Assert.Equal(255uy, RecordReaders.ReadByte(record, 1))
        Assert.Equal(200uy, RecordReaders.ReadByte(record, 2))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, 3))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, 4))
        Assert.Equal(0uy, RecordReaders.ReadByte(record, -1))


module ReadDecimalDoubleSingleTests =

    [<Fact>]
    let ``ReadDecimal converts various types and formats accurately`` () =
        let record: IDataRecord = MockDataRecord([|
            box 1234.56M
            box 1234.56
            box 42.5f
            box 100
            box 5000L
            box " 1.234,56 "
            box " 1,234.56 "
            box "NULL"
            box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box 1234.56
            box 42.5f
            box 100M
            box 1000
            box " 1.234,56 "
            box Double.PositiveInfinity
            box Double.NaN
            box "NaN"
            box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box 42.5f
            box 123.45
            box "12.34"
            box "N/A"
            box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box 1; box 0; box -1
            box 1L; box 0L
            box 1.0; box 0.0
            box 1M; box 0M
            box "NULL"; box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box "hello"
            box 123
            box DBNull.Value
            null
        |], throwOnTypedGet = true)

        Assert.Equal("hello", RecordReaders.ReadString(record, 0))
        Assert.Equal("123", RecordReaders.ReadString(record, 1))
        Assert.Null(RecordReaders.ReadString(record, 2))
        Assert.Null(RecordReaders.ReadString(record, 3))
        Assert.Null(RecordReaders.ReadString(record, -1))

    [<Fact>]
    let ``ReadGuid handles Guid, formatted string, byte array and invalid values`` () =
        let g = Guid.NewGuid()
        let gBytes = g.ToByteArray()
        let record: IDataRecord = MockDataRecord([|
            box g
            box (g.ToString())
            box (g.ToString("N"))
            box ("{" + g.ToString() + "}")
            box gBytes
            box "not-a-guid"
            box "NULL"
            box DBNull.Value
        |], throwOnTypedGet = true)

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

        let record: IDataRecord = MockDataRecord([|
            box dt
            box d
            box dto
            box oa
            box "2026-11-16T14:30:00Z"
            box "2026-11-16"
            box "NULL"
            box DBNull.Value
        |], throwOnTypedGet = true)

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
        let record: IDataRecord = MockDataRecord([|
            box d
            box dt
            box "2026-11-16"
            box "2026-11-16T10:00:00Z"
            box "N/A"
            box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box t
            box ts
            box dt
            box "15:45:30"
            box "NULL"
            box DBNull.Value
        |])

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
        let record: IDataRecord = MockDataRecord([|
            box bytes
            box base64
            box "hello"
            box "NULL"
            box DBNull.Value
        |])

        Assert.Equal<byte seq>(bytes, RecordReaders.ReadByteArray(record, 0))
        Assert.Equal<byte seq>(bytes, RecordReaders.ReadByteArray(record, 1))
        Assert.Equal<byte seq>(System.Text.Encoding.UTF8.GetBytes("hello"), RecordReaders.ReadByteArray(record, 2))
        Assert.Empty(RecordReaders.ReadByteArray(record, 3))
        Assert.Empty(RecordReaders.ReadByteArray(record, 4))
        Assert.Empty(RecordReaders.ReadByteArray(record, -1))


module NullableReadersTests =

    [<Fact>]
    let ``ReadNullableInt32 returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord = MockDataRecord([|
            box 42
            box "100"
            box "NULL"
            box DBNull.Value
            null
        |])

        Assert.Equal(Nullable 42, RecordReaders.ReadNullableInt32(record, 0))
        Assert.Equal(Nullable 100, RecordReaders.ReadNullableInt32(record, 1))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 2))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 3))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, 4))
        Assert.Equal(Nullable<int>(), RecordReaders.ReadNullableInt32(record, -1))

    [<Fact>]
    let ``ReadNullableInt64 returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord = MockDataRecord([|
            box 9876543210L
            box "NULL"
            box DBNull.Value
        |])

        Assert.Equal(Nullable 9876543210L, RecordReaders.ReadNullableInt64(record, 0))
        Assert.Equal(Nullable<int64>(), RecordReaders.ReadNullableInt64(record, 1))
        Assert.Equal(Nullable<int64>(), RecordReaders.ReadNullableInt64(record, 2))

    [<Fact>]
    let ``ReadNullableDecimal returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord = MockDataRecord([|
            box 123.45M
            box "1.234,56"
            box "N/A"
            box DBNull.Value
        |])

        Assert.Equal(Nullable 123.45M, RecordReaders.ReadNullableDecimal(record, 0))
        Assert.Equal(Nullable 1234.56M, RecordReaders.ReadNullableDecimal(record, 1))
        Assert.Equal(Nullable<decimal>(), RecordReaders.ReadNullableDecimal(record, 2))
        Assert.Equal(Nullable<decimal>(), RecordReaders.ReadNullableDecimal(record, 3))

    [<Fact>]
    let ``ReadNullableDouble returns valid Nullable or empty when null or sentinel`` () =
        let record: IDataRecord = MockDataRecord([|
            box 45.67
            box "45,67"
            box "NaN"
            box DBNull.Value
        |])

        Assert.Equal(Nullable 45.67, RecordReaders.ReadNullableDouble(record, 0))
        Assert.Equal(Nullable 45.67, RecordReaders.ReadNullableDouble(record, 1))
        Assert.Equal(Nullable<double>(), RecordReaders.ReadNullableDouble(record, 2))
        Assert.Equal(Nullable<double>(), RecordReaders.ReadNullableDouble(record, 3))

    [<Fact>]
    let ``ReadNullableDateTime returns valid Nullable or empty when null or sentinel`` () =
        let dt = DateTime(2026, 11, 16, 12, 0, 0)
        let record: IDataRecord = MockDataRecord([|
            box dt
            box "2026-11-16T12:00:00Z"
            box "NULL"
            box DBNull.Value
        |])

        Assert.Equal(Nullable dt, RecordReaders.ReadNullableDateTime(record, 0))
        Assert.True(RecordReaders.ReadNullableDateTime(record, 1).HasValue)
        Assert.Equal(Nullable<DateTime>(), RecordReaders.ReadNullableDateTime(record, 2))
        Assert.Equal(Nullable<DateTime>(), RecordReaders.ReadNullableDateTime(record, 3))


module OptionAndObjectReadersTests =

    [<Fact>]
    let ``ReadOption returns Some or None for primitive and string types`` () =
        let record: IDataRecord = MockDataRecord([|
            box 42
            box "NULL"
            box DBNull.Value
            box "hello"
            box ""
        |])

        Assert.Equal(Some 42, RecordReaders.ReadOption<int>(record, 0))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, 1))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, 2))
        Assert.Equal(Some "hello", RecordReaders.ReadOption<string>(record, 3))
        Assert.Equal(None, RecordReaders.ReadOption<string>(record, 4))
        Assert.Equal(None, RecordReaders.ReadOption<int>(record, -1))

    [<Fact>]
    let ``ReadObject returns converted object or default value on DBNull`` () =
        let record: IDataRecord = MockDataRecord([|
            box "123"
            box DBNull.Value
        |])

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
        let mapper = CompiledRecordMapper.getCompiledRecordMapper<SampleRecord> [
            "Id"; "Name"; "Amount"; "Score"; "IsActive"; "CreatedDate"; "EffectiveDate"; "EventTime"; "Notes"; "Data"
        ]

        let dt = DateTime(2026, 11, 16, 10, 0, 0)
        let d = DateOnly(2026, 11, 16)
        let t = TimeOnly(10, 0, 0)
        let dataBytes = [| 1uy; 2uy; 3uy |]

        let record: IDataRecord = MockDataRecord([|
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
        |])

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
        let mapper = CompiledRecordMapper.getCompiledRecordMapper<SampleRecord> [
            "Id"; "Name"; "Amount"; "Score"; "IsActive"; "CreatedDate"; "EffectiveDate"; "EventTime"; "Notes"; "Data"
        ]

        let record: IDataRecord = MockDataRecord([|
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
        |])

        let result = mapper record
        Assert.Equal(None, result.Notes)

    [<Fact>]
    let ``CompiledRecordMapper maps tuples directly`` () =
        let mapper = CompiledRecordMapper.getCompiledRecordMapper<int * string * double> [ "A"; "B"; "C" ]
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
        let arrayMapper = CompiledRecordMapper.getCompiledRecordMapper<obj array> [ "X"; "Y" ]
        let record: IDataRecord = MockDataRecord([| box 1; box "text" |])
        let arr = arrayMapper record
        Assert.Equal(2, arr.Length)
        Assert.Equal(box 1, arr.[0])
        Assert.Equal(box "text", arr.[1])

