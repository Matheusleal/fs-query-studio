namespace FSharpQueryStudio.Connectors

open System
open System.Collections
open System.Collections.Generic
open System.Collections.Concurrent
open System.Data
open System.Globalization
open System.Linq
open System.Linq.Expressions
open System.Reflection
open Microsoft.FSharp.Reflection
open FSharpQueryStudio.Protocol
open Serilog

/// Identifies the SQL dialect for pushdown syntax generation (e.g. LIMIT vs TOP/OFFSET).
[<RequireQualifiedAccess>]
type SqlDialect =
    | Sqlite
    | PostgreSql
    | SqlServer

module SqlDialect =
    let fromConnectorKind (kind: ConnectorKind) =
        if kind = ConnectorKind("PostgreSql") then SqlDialect.PostgreSql
        elif kind = ConnectorKind("SqlServer") then SqlDialect.SqlServer
        else SqlDialect.Sqlite

/// Static helper methods for reading primitive types directly from IDataRecord without boxing.
type RecordReaders =
    static member private IsSentinelString(s: string) : bool =
        if String.IsNullOrWhiteSpace s then true
        else
            let t = s.Trim().Trim('"', '\'')
            String.IsNullOrEmpty t
            || t = "-"
            || String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "na", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "none", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "nan", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("#", StringComparison.Ordinal)

    static member private TryParseNormalizedDecimal(clean: string) : bool * decimal =
        if clean.Contains(",") && not (clean.Contains(".")) then
            let normalized = clean.Replace(",", ".")
            match Decimal.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, v -> true, v
            | false, _ -> false, 0M
        elif clean.Contains(".") && not (clean.Contains(",")) then
            match Decimal.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, v -> true, v
            | false, _ -> false, 0M
        elif clean.Contains(",") && clean.Contains(".") then
            let lastComma = clean.LastIndexOf(',')
            let lastDot = clean.LastIndexOf('.')
            let normalized =
                if lastDot > lastComma then clean.Replace(",", "")
                else clean.Replace(".", "").Replace(",", ".")
            match Decimal.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, v -> true, v
            | false, _ -> false, 0M
        else
            match Decimal.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowThousands ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, v -> true, v
            | false, _ ->
                match Decimal.TryParse(clean, NumberStyles.Number, CultureInfo.CurrentCulture) with
                | true, v -> true, v
                | false, _ -> false, 0M

    static member private TryParseNormalizedDouble(clean: string) : bool * double =
        let parsed =
            if clean.Contains(",") && not (clean.Contains(".")) then
                let normalized = clean.Replace(",", ".")
                match Double.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | false, _ -> None
            elif clean.Contains(".") && not (clean.Contains(",")) then
                match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | false, _ -> None
            elif clean.Contains(",") && clean.Contains(".") then
                let lastComma = clean.LastIndexOf(',')
                let lastDot = clean.LastIndexOf('.')
                let normalized =
                    if lastDot > lastComma then clean.Replace(",", "")
                    else clean.Replace(".", "").Replace(",", ".")
                match Double.TryParse(normalized, NumberStyles.Float ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | false, _ -> None
            else
                match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowThousands ||| NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
                | true, v -> Some v
                | false, _ ->
                    match Double.TryParse(clean, NumberStyles.Float ||| NumberStyles.AllowThousands, CultureInfo.CurrentCulture) with
                    | true, v -> Some v
                    | false, _ -> None
        match parsed with
        | Some v when Double.IsFinite v -> true, v
        | _ -> false, 0.0

    static member ReadInt32(record: IDataRecord, idx: int) : int =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> 0
                | :? int as i -> i
                | :? int64 as i64 -> int (Math.Clamp(i64, int64 Int32.MinValue, int64 Int32.MaxValue))
                | :? int16 as i16 -> int i16
                | :? byte as b -> int b
                | :? decimal as d -> int (Math.Clamp(Math.Round(double d), float Int32.MinValue, float Int32.MaxValue))
                | :? double as d ->
                    if Double.IsFinite d then int (Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue)) else 0
                | :? single as f ->
                    if Single.IsFinite f then int (Math.Clamp(Math.Round(double f), float Int32.MinValue, float Int32.MaxValue)) else 0
                | :? bool as b -> if b then 1 else 0
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then 0
                    else
                        match Int32.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                        | true, i -> i
                        | false, _ ->
                            match RecordReaders.TryParseNormalizedDouble rawStr with
                            | true, d -> int (Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue))
                            | false, _ -> 0
                | _ -> Convert.ToInt32(v, CultureInfo.InvariantCulture)
            with _ ->
                0

    static member ReadInt64(record: IDataRecord, idx: int) : int64 =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0L
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> 0L
                | :? int64 as i64 -> i64
                | :? int as i -> int64 i
                | :? int16 as i16 -> int64 i16
                | :? byte as b -> int64 b
                | :? decimal as d -> int64 (Math.Clamp(Math.Round(double d), float Int64.MinValue, float Int64.MaxValue))
                | :? double as d ->
                    if Double.IsFinite d then int64 (Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue)) else 0L
                | :? single as f ->
                    if Single.IsFinite f then int64 (Math.Clamp(Math.Round(double f), float Int64.MinValue, float Int64.MaxValue)) else 0L
                | :? bool as b -> if b then 1L else 0L
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then 0L
                    else
                        match Int64.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                        | true, i64 -> i64
                        | false, _ ->
                            match RecordReaders.TryParseNormalizedDouble rawStr with
                            | true, d -> int64 (Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue))
                            | false, _ -> 0L
                | _ -> Convert.ToInt64(v, CultureInfo.InvariantCulture)
            with _ ->
                0L

    static member ReadInt16(record: IDataRecord, idx: int) : int16 =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0s
        else
            try record.GetInt16(idx)
            with _ ->
                try
                    let v = record.GetValue(idx)
                    match v with
                    | null -> 0s
                    | :? int16 as i16 -> i16
                    | :? int as i -> int16 (Math.Clamp(i, int Int16.MinValue, int Int16.MaxValue))
                    | :? int64 as i64 -> int16 (Math.Clamp(i64, int64 Int16.MinValue, int64 Int16.MaxValue))
                    | :? byte as b -> int16 b
                    | :? string as s ->
                        let rawStr = s.Trim().Trim('"', '\'')
                        if RecordReaders.IsSentinelString rawStr then 0s
                        else
                            match Int16.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                            | true, i16 -> i16
                            | false, _ ->
                                match RecordReaders.TryParseNormalizedDouble rawStr with
                                | true, d -> int16 (Math.Clamp(Math.Round(d), float Int16.MinValue, float Int16.MaxValue))
                                | false, _ -> 0s
                    | _ -> Convert.ToInt16(v, CultureInfo.InvariantCulture)
                with _ ->
                    0s

    static member ReadByte(record: IDataRecord, idx: int) : byte =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0uy
        else
            try record.GetByte(idx)
            with _ ->
                try Convert.ToByte(record.GetValue(idx), CultureInfo.InvariantCulture)
                with _ -> 0uy

    static member ReadDecimal(record: IDataRecord, idx: int) : decimal =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0M
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> 0M
                | :? decimal as d -> d
                | :? double as d ->
                    if Double.IsFinite d then Convert.ToDecimal(d, CultureInfo.InvariantCulture) else 0M
                | :? single as f ->
                    if Single.IsFinite f then Convert.ToDecimal(f, CultureInfo.InvariantCulture) else 0M
                | :? int as i -> decimal i
                | :? int64 as i64 -> decimal i64
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then 0M
                    else
                        match RecordReaders.TryParseNormalizedDecimal rawStr with
                        | true, d -> d
                        | false, _ -> 0M
                | _ -> Convert.ToDecimal(v, CultureInfo.InvariantCulture)
            with _ ->
                0M

    static member ReadDouble(record: IDataRecord, idx: int) : double =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0.0
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> 0.0
                | :? double as d -> if Double.IsFinite d then d else 0.0
                | :? single as f -> if Single.IsFinite f then double f else 0.0
                | :? decimal as m -> double m
                | :? int as i -> double i
                | :? int64 as i64 -> double i64
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then 0.0
                    else
                        match RecordReaders.TryParseNormalizedDouble rawStr with
                        | true, d -> d
                        | false, _ -> 0.0
                | _ ->
                    let d = Convert.ToDouble(v, CultureInfo.InvariantCulture)
                    if Double.IsFinite d then d else 0.0
            with _ ->
                0.0

    static member ReadSingle(record: IDataRecord, idx: int) : single =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then 0.0f
        else
            try
                let d = RecordReaders.ReadDouble(record, idx)
                single d
            with _ ->
                0.0f

    static member ReadBool(record: IDataRecord, idx: int) : bool =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then false
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> false
                | :? bool as b -> b
                | :? int as i -> i <> 0
                | :? int64 as i64 -> i64 <> 0L
                | :? double as d -> Double.IsFinite d && d <> 0.0
                | :? decimal as m -> m <> 0M
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then false
                    else
                        match Boolean.TryParse(rawStr) with
                        | true, b -> b
                        | false, _ ->
                            rawStr = "1"
                            || rawStr.Equals("yes", StringComparison.OrdinalIgnoreCase)
                            || rawStr.Equals("y", StringComparison.OrdinalIgnoreCase)
                            || rawStr.Equals("true", StringComparison.OrdinalIgnoreCase)
                            || rawStr.Equals("t", StringComparison.OrdinalIgnoreCase)
                            || rawStr.Equals("sim", StringComparison.OrdinalIgnoreCase)
                | _ -> Convert.ToBoolean(v, CultureInfo.InvariantCulture)
            with _ ->
                false

    static member ReadString(record: IDataRecord, idx: int) : string =
        // Strings are reference types, so a genuine SQL NULL stays `null` (no unbox risk).
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then null
        else
            try record.GetString(idx)
            with _ ->
                try
                    let v = record.GetValue(idx)
                    if isNull v || Convert.IsDBNull(v) then null else string v
                with _ ->
                    null

    static member ReadGuid(record: IDataRecord, idx: int) : Guid =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then Guid.Empty
        else
            try record.GetGuid(idx)
            with _ ->
                try
                    let v = record.GetValue(idx)
                    match v with
                    | null -> Guid.Empty
                    | :? Guid as g -> g
                    | :? string as s ->
                        match Guid.TryParse(s.Trim().Trim('"', '\'')) with
                        | true, g -> g
                        | false, _ -> Guid.Empty
                    | :? (byte array) as b when b.Length = 16 -> Guid(b)
                    | _ ->
                        match Guid.TryParse(string v) with
                        | true, g -> g
                        | false, _ -> Guid.Empty
                with _ ->
                    Guid.Empty

    static member ReadDateTime(record: IDataRecord, idx: int) : DateTime =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then DateTime.MinValue
        else
            try record.GetDateTime(idx)
            with _ ->
                try
                    let v = record.GetValue(idx)
                    match v with
                    | null -> DateTime.MinValue
                    | :? DateTime as dt -> dt
                    | :? DateOnly as d -> d.ToDateTime(TimeOnly.MinValue)
                    | :? DateTimeOffset as dto -> dto.DateTime
                    | :? double as d when Double.IsFinite d && d > -657435.0 && d < 2958466.0 -> DateTime.FromOADate(d)
                    | :? int64 as ticks when ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks -> DateTime(ticks)
                    | :? string as s ->
                        let rawStr = s.Trim().Trim('"', '\'')
                        if RecordReaders.IsSentinelString rawStr then DateTime.MinValue
                        else
                            match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                            | true, dt -> dt
                            | false, _ ->
                                match DateTime.TryParse(rawStr, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                                | true, dt -> dt
                                | false, _ -> DateTime.MinValue
                    | _ -> Convert.ToDateTime(v, CultureInfo.InvariantCulture)
                with _ ->
                    DateTime.MinValue

    static member ReadDateOnly(record: IDataRecord, idx: int) : DateOnly =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then DateOnly.MinValue
        else
            try
                let v = record.GetValue(idx)
                match v with
                | null -> DateOnly.MinValue
                | :? DateOnly as d -> d
                | :? DateTime as dt -> DateOnly.FromDateTime(dt)
                | :? DateTimeOffset as dto -> DateOnly.FromDateTime(dto.DateTime)
                | :? string as s ->
                    let rawStr = s.Trim().Trim('"', '\'')
                    if RecordReaders.IsSentinelString rawStr then DateOnly.MinValue
                    else
                        match DateOnly.TryParse(rawStr, CultureInfo.InvariantCulture) with
                        | true, d -> d
                        | false, _ ->
                            match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                            | true, dt -> DateOnly.FromDateTime(dt)
                            | false, _ -> DateOnly.MinValue
                | _ -> DateOnly.FromDateTime(Convert.ToDateTime(v, CultureInfo.InvariantCulture))
            with _ ->
                DateOnly.MinValue

    static member private DefaultForType(targetType: Type) : obj =
        if targetType = typeof<string> then box ""
        elif targetType = typeof<DateTime> then box DateTime.MinValue
        elif targetType = typeof<DateOnly> then box DateOnly.MinValue
        elif targetType = typeof<Guid> then box Guid.Empty
        elif targetType = typeof<byte[]> then box (Array.empty<byte>)
        elif targetType.IsValueType && Nullable.GetUnderlyingType(targetType) = null then
            Activator.CreateInstance(targetType)
        else
            null

    static member ConvertValue (raw: obj) (targetType: Type) : obj =
        try
            if isNull raw || Convert.IsDBNull(raw) then
                RecordReaders.DefaultForType targetType
            elif targetType = typeof<obj> then
                raw
            elif raw.GetType() = targetType then
                raw
            elif targetType = typeof<string> then
                box (string raw)
            elif raw :? string then
                let rawStr = (raw :?> string).Trim().Trim('"', '\'')
                if RecordReaders.IsSentinelString rawStr then
                    RecordReaders.DefaultForType targetType
                elif targetType = typeof<bool> then
                    match Boolean.TryParse(rawStr) with
                    | true, v -> box v
                    | false, _ ->
                        box (
                            rawStr = "1"
                            || String.Equals(rawStr, "yes", StringComparison.OrdinalIgnoreCase)
                            || String.Equals(rawStr, "y", StringComparison.OrdinalIgnoreCase)
                            || String.Equals(rawStr, "sim", StringComparison.OrdinalIgnoreCase)
                        )
                elif targetType = typeof<int> then
                    match Int32.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                    | true, v -> box v
                    | false, _ ->
                        match RecordReaders.TryParseNormalizedDouble rawStr with
                        | true, d -> box (int (Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue)))
                        | false, _ -> box 0
                elif targetType = typeof<int64> then
                    match Int64.TryParse(rawStr, NumberStyles.Any, CultureInfo.InvariantCulture) with
                    | true, v -> box v
                    | false, _ ->
                        match RecordReaders.TryParseNormalizedDouble rawStr with
                        | true, d -> box (int64 (Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue)))
                        | false, _ -> box 0L
                elif targetType = typeof<decimal> then
                    match RecordReaders.TryParseNormalizedDecimal rawStr with
                    | true, v -> box v
                    | false, _ -> box 0M
                elif targetType = typeof<double> then
                    match RecordReaders.TryParseNormalizedDouble rawStr with
                    | true, v -> box v
                    | false, _ -> box 0.0
                elif targetType = typeof<DateTime> then
                    match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, v -> box v
                    | false, _ ->
                        match DateTime.TryParse(rawStr, CultureInfo.CurrentCulture, DateTimeStyles.None) with
                        | true, v -> box v
                        | false, _ -> box DateTime.MinValue
                elif targetType = typeof<DateOnly> then
                    match DateOnly.TryParse(rawStr, CultureInfo.InvariantCulture) with
                    | true, v -> box v
                    | false, _ ->
                        match DateTime.TryParse(rawStr, CultureInfo.InvariantCulture, DateTimeStyles.None) with
                        | true, dt -> box (DateOnly.FromDateTime(dt))
                        | false, _ -> box DateOnly.MinValue
                elif targetType = typeof<Guid> then
                    match Guid.TryParse(rawStr) with
                    | true, v -> box v
                    | false, _ -> box Guid.Empty
                else
                    Convert.ChangeType(rawStr, targetType, CultureInfo.InvariantCulture)
            else
                if targetType = typeof<decimal> then
                    match raw with
                    | :? double as d -> if Double.IsFinite d then box (Convert.ToDecimal(d, CultureInfo.InvariantCulture)) else box 0M
                    | :? single as f -> if Single.IsFinite f then box (Convert.ToDecimal(f, CultureInfo.InvariantCulture)) else box 0M
                    | :? int as i -> box (decimal i)
                    | :? int64 as i64 -> box (decimal i64)
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<int> then
                    match raw with
                    | :? double as d ->
                        if Double.IsFinite d then box (int (Math.Clamp(Math.Round(d), float Int32.MinValue, float Int32.MaxValue))) else box 0
                    | :? decimal as m ->
                        box (int (Math.Clamp(Math.Round(double m), float Int32.MinValue, float Int32.MaxValue)))
                    | :? int64 as i64 ->
                        box (int (Math.Clamp(i64, int64 Int32.MinValue, int64 Int32.MaxValue)))
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<int64> then
                    match raw with
                    | :? double as d ->
                        if Double.IsFinite d then box (int64 (Math.Clamp(Math.Round(d), float Int64.MinValue, float Int64.MaxValue))) else box 0L
                    | :? decimal as m ->
                        box (int64 (Math.Clamp(Math.Round(double m), float Int64.MinValue, float Int64.MaxValue)))
                    | :? int as i -> box (int64 i)
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<DateTime> then
                    match raw with
                    | :? DateOnly as d -> box (d.ToDateTime(TimeOnly.MinValue))
                    | :? DateTimeOffset as dto -> box dto.DateTime
                    | :? double as d when Double.IsFinite d && d > -657435.0 && d < 2958466.0 -> box (DateTime.FromOADate(d))
                    | _ -> Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
                elif targetType = typeof<DateOnly> then
                    match raw with
                    | :? DateTime as dt -> box (DateOnly.FromDateTime(dt))
                    | :? DateTimeOffset as dto -> box (DateOnly.FromDateTime(dto.DateTime))
                    | :? double as d when Double.IsFinite d && d > -657435.0 && d < 2958466.0 ->
                        box (DateOnly.FromDateTime(DateTime.FromOADate(d)))
                    | _ -> DateOnly.Parse(string raw, CultureInfo.InvariantCulture) |> box
                else
                    Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture)
        with _ ->
            RecordReaders.DefaultForType targetType

    static member ReadOption<'U>(record: IDataRecord, idx: int) : 'U option =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then None
        else
            try
                let raw = record.GetValue(idx)
                let isOptionNone =
                    isNull raw
                    || Convert.IsDBNull(raw)
                    || (match raw with
                        | :? string as s -> RecordReaders.IsSentinelString s
                        | _ -> false)

                if isOptionNone then None
                else
                    let converted = RecordReaders.ConvertValue raw typeof<'U>
                    if isNull converted then None
                    else Some (unbox<'U> converted)
            with _ ->
                None

    static member ReadObject(record: IDataRecord, idx: int, targetType: Type) : obj =
        if idx < 0 || idx >= record.FieldCount || record.IsDBNull(idx) then
            RecordReaders.DefaultForType targetType
        else
            RecordReaders.ConvertValue (record.GetValue(idx)) targetType

/// High-performance compiled mapper generator that caches Func<IDataRecord, 'T> delegates.
module CompiledRecordMapper =
    let private compiledMapperCache = ConcurrentDictionary<Type * string, obj>()

    let getCompiledRecordMapper<'T> (columns: string list) : (IDataRecord -> 'T) =
        let targetType = typeof<'T>
        let key = (targetType, String.concat "," columns)
        let raw =
            compiledMapperCache.GetOrAdd(
                key,
                Func<Type * string, obj>(fun _ ->
                    if targetType = typeof<obj array> then
                        box (fun (record: IDataRecord) ->
                            [| for i in 0 .. record.FieldCount - 1 -> record.GetValue(i) |] :> obj
                        )
                    elif FSharpType.IsRecord(targetType) then
                        let fields = FSharpType.GetRecordFields(targetType)
                        let colIndices =
                            fields
                            |> Array.mapi (fun defaultIdx f ->
                                let byName =
                                    columns
                                    |> List.tryFindIndex (fun c -> String.Equals(c, f.Name, StringComparison.OrdinalIgnoreCase))
                                match byName with
                                | Some idx -> idx
                                | None -> if defaultIdx < columns.Length then defaultIdx else 0
                            )

                        let recordParam = Expression.Parameter(typeof<IDataRecord>, "record")
                        let args: Expression list =
                            fields
                            |> Array.mapi (fun i f ->
                                let idx = colIndices.[i]
                                let ft = f.PropertyType
                                if ft = typeof<int> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt32"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<int64> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt64"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<int16> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadInt16"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<byte> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadByte"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<decimal> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDecimal"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<double> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDouble"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<single> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadSingle"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<bool> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadBool"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<string> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadString"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<Guid> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadGuid"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<DateTime> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDateTime"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft = typeof<DateOnly> then
                                    Expression.Call(typeof<RecordReaders>.GetMethod("ReadDateOnly"), recordParam, Expression.Constant(idx)) :> Expression
                                elif ft.IsGenericType && ft.GetGenericTypeDefinition() = typedefof<option<_>> then
                                    let innerType = ft.GetGenericArguments().[0]
                                    let readOpt = typeof<RecordReaders>.GetMethod("ReadOption").MakeGenericMethod([| innerType |])
                                    Expression.Call(readOpt, recordParam, Expression.Constant(idx)) :> Expression
                                else
                                    let readObj = typeof<RecordReaders>.GetMethod("ReadObject")
                                    let callExpr = Expression.Call(readObj, recordParam, Expression.Constant(idx), Expression.Constant(ft))
                                    Expression.Convert(callExpr, ft) :> Expression
                            )
                            |> List.ofArray

                        let ctor =
                            match targetType.GetConstructors() with
                            | [| c |] -> c
                            | ctors -> ctors |> Array.maxBy (fun c -> c.GetParameters().Length)
                        let newExpr = Expression.New(ctor, args :> seq<Expression>)
                        let lambda = Expression.Lambda<Func<IDataRecord, 'T>>(newExpr, recordParam)
                        let compiled = lambda.Compile()
                        box (fun (record: IDataRecord) -> compiled.Invoke(record) :> obj)
                    elif FSharpType.IsTuple(targetType) then
                        let elemTypes = FSharpType.GetTupleElements(targetType)
                        let tupleCtor = FSharpValue.PreComputeTupleConstructor(targetType)
                        box (fun (record: IDataRecord) ->
                            let values = Array.zeroCreate elemTypes.Length
                            for i in 0 .. elemTypes.Length - 1 do
                                let raw = if i < record.FieldCount && not (record.IsDBNull(i)) then record.GetValue(i) else null
                                values.[i] <- RecordReaders.ConvertValue raw elemTypes.[i]
                            tupleCtor values
                        )
                    else
                        box (fun (record: IDataRecord) ->
                            if record.FieldCount > 0 && not (record.IsDBNull(0)) then
                                RecordReaders.ConvertValue (record.GetValue(0)) targetType
                            else
                                null
                        )
                )
            )
        let typedFunc = raw :?> (IDataRecord -> obj)
        fun (record: IDataRecord) -> unbox<'T> (typedFunc record)

type OrderDirection =
    | Ascending
    | Descending

type OrderClause = {
    ColumnName: string
    Direction: OrderDirection
}

type JoinPlan = {
    JoinType: string
    SourceTable: string
    Alias: string
    OnClause: string
}

module SqlPushdownFormatting =
    let formatSqlAndParameters (sql: string) (parameters: (string * obj) list) : string =
        if List.isEmpty parameters then
            sql
        else
            let paramLines =
                parameters
                |> List.map (fun (name, value) ->
                    let formattedVal =
                        if isNull value || Convert.IsDBNull(value) then "NULL"
                        else
                            match value with
                            | :? string as s -> sprintf "'%s'" s
                            | :? DateTime as dt -> sprintf "'%s'" (dt.ToString("yyyy-MM-dd HH:mm:ss"))
                            | :? bool as b -> if b then "1" else "0"
                            | other -> string other
                    sprintf "--   %s = %s" name formattedVal)
                |> String.concat "\n"
            $"-- Parameters:\n{paramLines}\n{sql}"

/// Represents the planned SQL pushdown query and any in-memory fallback predicates.
type PushdownQueryPlan = {
    BaseSql: string
    BaseParameters: (string * obj) list
    BaseAlias: string
    Joins: ResizeArray<JoinPlan>
    WhereClauses: ResizeArray<string>
    Parameters: ResizeArray<string * obj>
    OrderBy: ResizeArray<OrderClause>
    mutable Skip: int option
    mutable Take: int option
    mutable Projection: string option
    FallbackPredicates: ResizeArray<obj -> bool>
} with
    static member Create(baseSql: string, baseParameters: (string * obj) list, ?baseAlias: string) =
        {
            BaseSql = baseSql
            BaseParameters = baseParameters
            BaseAlias = defaultArg baseAlias "_t0"
            Joins = ResizeArray<JoinPlan>()
            WhereClauses = ResizeArray<string>()
            Parameters = ResizeArray<string * obj>()
            OrderBy = ResizeArray<OrderClause>()
            Skip = None
            Take = None
            Projection = None
            FallbackPredicates = ResizeArray<obj -> bool>()
        }

    member this.Clone() =
        let p = PushdownQueryPlan.Create(this.BaseSql, this.BaseParameters, this.BaseAlias)
        p.Joins.AddRange(this.Joins)
        p.WhereClauses.AddRange(this.WhereClauses)
        p.Parameters.AddRange(this.Parameters)
        p.OrderBy.AddRange(this.OrderBy)
        p.Skip <- this.Skip
        p.Take <- this.Take
        p.Projection <- this.Projection
        p.FallbackPredicates.AddRange(this.FallbackPredicates)
        p

    member this.BuildRawSql(dialect: SqlDialect) : string =
        let sql = this.BuildSql(dialect)
        let allParams = (this.BaseParameters @ (this.Parameters |> List.ofSeq))
        SqlPushdownFormatting.formatSqlAndParameters sql allParams

    member this.BuildSql(dialect: SqlDialect) : string =
        let isTableOnly =
            let trimmed = this.BaseSql.Trim()
            not (trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            && not (trimmed.Contains(" from ", StringComparison.OrdinalIgnoreCase))

        let fromSource =
            if this.Joins.Count > 0 then
                let baseFrom =
                    if isTableOnly then $"{this.BaseSql.Trim()} {this.BaseAlias}"
                    else $"({this.BaseSql}) AS {this.BaseAlias}"
                let joinsSql =
                    this.Joins
                    |> Seq.map (fun j ->
                        let tbl =
                            let t = j.SourceTable.Trim()
                            if t.StartsWith("select", StringComparison.OrdinalIgnoreCase) || t.Contains(" ") then $"({t}) AS {j.Alias}"
                            else $"{t} {j.Alias}"
                        $"{j.JoinType} {tbl} ON {j.OnClause}"
                    )
                    |> String.concat " "
                $"{baseFrom} {joinsSql}"
            else
                if isTableOnly then
                    this.BaseSql.Trim()
                else
                    $"({this.BaseSql}) AS _src"

        let whereSql =
            if this.WhereClauses.Count > 0 then
                " WHERE " + String.concat " AND " this.WhereClauses
            else
                ""

        let orderSql =
            if this.OrderBy.Count > 0 then
                let parts =
                    this.OrderBy
                    |> Seq.map (fun o ->
                        let dir = if o.Direction = Ascending then "ASC" else "DESC"
                        $"{o.ColumnName} {dir}"
                    )
                " ORDER BY " + String.concat ", " parts
            else
                ""

        let selectCols =
            match this.Projection with
            | Some proj -> proj
            | None ->
                if this.Joins.Count > 0 then
                    let joinedCols = this.Joins |> Seq.map (fun j -> $"{j.Alias}.*") |> String.concat ", "
                    $"{this.BaseAlias}.*, {joinedCols}"
                else
                    "*"

        match dialect with
        | SqlDialect.SqlServer ->
            match this.Skip, this.Take with
            | Some skip, Some take ->
                let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                $"SELECT {selectCols} FROM {fromSource}{whereSql}{ord} OFFSET {skip} ROWS FETCH NEXT {take} ROWS ONLY"
            | Some skip, None ->
                let ord = if orderSql = "" then " ORDER BY (SELECT NULL)" else orderSql
                $"SELECT {selectCols} FROM {fromSource}{whereSql}{ord} OFFSET {skip} ROWS"
            | None, Some take ->
                if orderSql <> "" then
                    $"SELECT {selectCols} FROM {fromSource}{whereSql}{orderSql} OFFSET 0 ROWS FETCH NEXT {take} ROWS ONLY"
                else
                    $"SELECT TOP ({take}) {selectCols} FROM {fromSource}{whereSql}"
            | None, None ->
                $"SELECT {selectCols} FROM {fromSource}{whereSql}{orderSql}"
        | _ ->
            let limitSql =
                match this.Take, this.Skip with
                | Some take, Some skip -> $" LIMIT {take} OFFSET {skip}"
                | Some take, None -> $" LIMIT {take}"
                | None, Some skip -> $" OFFSET {skip}"
                | None, None -> ""

            $"SELECT {selectCols} FROM {fromSource}{whereSql}{orderSql}{limitSql}"

type ParameterMemberReplacer(param: ParameterExpression, memberMap: Map<string, Expression>) =
    inherit ExpressionVisitor()
    override this.VisitMember(m: MemberExpression) =
        if m.Expression = (param :> Expression) && memberMap.ContainsKey m.Member.Name then
            memberMap.[m.Member.Name]
        else
            base.VisitMember(m)

module ExpressionPushdownTranslator =
    let rec collectBlockAssignments (expr: Expression) (acc: (string * Expression) list) : Expression * (string * Expression) list =
        match expr with
        | :? BlockExpression as be when be.Expressions.Count > 0 ->
            let mutable currentAcc = acc
            for i in 0 .. be.Expressions.Count - 2 do
                match be.Expressions.[i] with
                | :? BinaryExpression as b when b.NodeType = ExpressionType.Assign ->
                    match b.Left with
                    | :? ParameterExpression as p when not (isNull p.Name) ->
                        currentAcc <- (p.Name, b.Right) :: currentAcc
                    | _ -> ()
                | _ -> ()
            let last = be.Expressions.[be.Expressions.Count - 1]
            match last with
            | :? BlockExpression ->
                collectBlockAssignments last currentAcc
            | _ ->
                last, currentAcc
        | other ->
            other, acc

    let unwrapBlock (expr: Expression) : Expression =
        let targetExpr, assignments = collectBlockAssignments expr []
        if List.isEmpty assignments then targetExpr
        else
            let varMap = assignments |> Map.ofList
            let replacer =
                { new ExpressionVisitor() with
                    override this.VisitParameter(p: ParameterExpression) =
                        if not (isNull p) && not (isNull p.Name) && varMap.ContainsKey p.Name then
                            varMap.[p.Name]
                        else
                            base.VisitParameter(p)
                }
            replacer.Visit(targetExpr)

    let extractMemberMap (body: Expression) : Map<string, Expression> =
        let unwrapped = unwrapBlock body
        match unwrapped with
        | :? NewExpression as ne ->
            let ctor = ne.Constructor
            let paramInfos = if isNull ctor then [||] else ctor.GetParameters()
            let mutable m = Map.empty
            for i in 0 .. ne.Arguments.Count - 1 do
                let arg = ne.Arguments.[i]
                let itemKey = sprintf "Item%d" (i + 1)
                m <- m.Add(itemKey, arg)
                if not (isNull ne.Members) && i < ne.Members.Count then
                    m <- m.Add(ne.Members.[i].Name, arg)
                elif i < paramInfos.Length then
                    m <- m.Add(paramInfos.[i].Name, arg)
            m
        | :? MemberInitExpression as mie ->
            let mutable m = Map.empty
            for b in mie.Bindings do
                match b with
                | :? MemberAssignment as ma ->
                    m <- m.Add(ma.Member.Name, ma.Expression)
                | _ -> ()
            m
        | _ ->
            Map.empty

    let rec dependsOnParameter (param: ParameterExpression) (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? ParameterExpression as p -> p = param
        | :? MemberExpression as m -> dependsOnParameter param m.Expression
        | :? BinaryExpression as b -> dependsOnParameter param b.Left || dependsOnParameter param b.Right
        | :? UnaryExpression as u -> dependsOnParameter param u.Operand
        | :? MethodCallExpression as mc ->
            dependsOnParameter param mc.Object || (mc.Arguments |> Seq.exists (dependsOnParameter param))
        | _ -> false

    let evaluateClientValue (expr: Expression) : obj =
        match expr with
        | :? ConstantExpression as c -> c.Value
        | _ ->
            let lambda = Expression.Lambda(expr)
            let fn = lambda.Compile()
            fn.DynamicInvoke()

    let rec unwrapQuote (expr: Expression) : Expression =
        match expr with
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote -> unwrapQuote u.Operand
        | _ -> expr

    let rec private getTupleDepth (t: Type) : int =
        if not (isNull t) && t.IsGenericType && (t.Name.StartsWith("Tuple`") || t.Name.StartsWith("ValueTuple`")) then
            let args = t.GetGenericArguments()
            if args.Length = 2 then
                1 + getTupleDepth args.[0]
            else
                args.Length - 1
        else
            0

    /// Resolves a member expression (possibly navigating a nested tuple hierarchy from multi-table joins)
    /// into an optional table alias and the column name.
    let resolveMemberRef (plan: PushdownQueryPlan) (expr: Expression) : (string option * string) =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MemberExpression as m ->
            let path = ResizeArray<string>()
            let mutable curr: Expression = m.Expression
            let colName = m.Member.Name
            let mutable rootParam: ParameterExpression option = None

            let mutable keepWalking = true
            while keepWalking && not (isNull curr) do
                match curr with
                | :? MemberExpression as parentM ->
                    path.Add(parentM.Member.Name)
                    curr <- parentM.Expression
                | :? ParameterExpression as p ->
                    rootParam <- Some p
                    keepWalking <- false
                | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
                    curr <- u.Operand
                | _ ->
                    keepWalking <- false

            path.Reverse()

            if path.Count = 0 then
                (None, colName)
            else
                let tupleItems = path |> Seq.filter (fun s -> s.StartsWith("Item")) |> Seq.toList
                if not (List.isEmpty tupleItems) then
                    if tupleItems.Length = 1 then
                        // Flat tuple / AnonymousObject: root.Item{X}
                        let itemStr = tupleItems.[0].Substring(4)
                        match Int32.TryParse(itemStr) with
                        | true, n when n >= 1 ->
                            let tableIdx = n - 1
                            let alias = if tableIdx = 0 then plan.BaseAlias else sprintf "_t%d" tableIdx
                            (Some alias, colName)
                        | _ ->
                            (None, colName)
                    else
                        // Nested left-associative tuple tree: root.Item1.Item2...
                        let k =
                            match rootParam with
                            | Some p ->
                                let d = getTupleDepth p.Type
                                if d > 0 then d else plan.Joins.Count
                            | None -> plan.Joins.Count

                        let leadingOnes =
                            let mutable count = 0
                            let mutable stopped = false
                            for item in tupleItems do
                                if not stopped then
                                    if item = "Item1" then count <- count + 1
                                    else stopped <- true
                            count

                        let hasItem2 = tupleItems |> List.exists (fun s -> s = "Item2")
                        let tableIdx =
                            if not hasItem2 then 0
                            else max 0 (k - leadingOnes)

                        let alias = if tableIdx = 0 then plan.BaseAlias else sprintf "_t%d" tableIdx
                        (Some alias, colName)
                else
                    (None, colName)
        | _ ->
            (None, unwrapped.ToString())

    let rec extractColumnNameWithAlias (plan: PushdownQueryPlan) (expr: Expression) : string =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? LambdaExpression as l -> extractColumnNameWithAlias plan l.Body
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias -> $"{alias}.{colName}"
            | None ->
                if plan.Joins.Count > 0 && not (colName.Contains ".") then
                    $"{plan.BaseAlias}.{colName}"
                else
                    colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractColumnNameWithAlias plan u.Operand
        | _ ->
            unwrapped.ToString()

    let extractColumnName (param: ParameterExpression) (expr: Expression) : string =
        match expr with
        | :? MemberExpression as m when m.Expression = (param :> Expression) ->
            m.Member.Name
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            match u.Operand with
            | :? MemberExpression as m when m.Expression = (param :> Expression) -> m.Member.Name
            | other -> other.ToString()
        | _ ->
            expr.ToString()

    let rec extractKeyColumns (plan: PushdownQueryPlan) (fallbackAlias: string) (param: ParameterExpression) (expr: Expression) : string list =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            let alias = defaultArg aliasOpt fallbackAlias
            [ $"{alias}.{colName}" ]
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            extractKeyColumns plan fallbackAlias param u.Operand
        | :? NewExpression as ne ->
            [ for arg in ne.Arguments do
                yield! extractKeyColumns plan fallbackAlias param arg ]
        | :? MemberInitExpression as mie ->
            [ for binding in mie.Bindings do
                match binding with
                | :? MemberAssignment as ma ->
                    yield! extractKeyColumns plan fallbackAlias param ma.Expression
                | _ -> () ]
        | :? MethodCallExpression as mc when mc.Method.Name = "Create" && (mc.Method.DeclaringType = typeof<Tuple> || mc.Method.DeclaringType.Name.StartsWith("Tuple")) ->
            [ for arg in mc.Arguments do
                yield! extractKeyColumns plan fallbackAlias param arg ]
        | _ ->
            let single = extractColumnName param unwrapped
            if not (String.IsNullOrWhiteSpace single) && not (single.Contains "(") && not (single.Contains " ") then
                let ref = if single.Contains(".") then single else $"{fallbackAlias}.{single}"
                [ ref ]
            else []

    let rec translatePredicate (param: ParameterExpression) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        match expr with
        | null -> None
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.AndAlso ->
                match translatePredicate param plan b.Left, translatePredicate param plan b.Right with
                | Some l, Some r -> Some $"({l} AND {r})"
                | Some l, None -> Some l
                | None, Some r -> Some r
                | None, None -> None
            | ExpressionType.OrElse ->
                match translatePredicate param plan b.Left, translatePredicate param plan b.Right with
                | Some l, Some r -> Some $"({l} OR {r})"
                | _ -> None
            | ExpressionType.Equal
            | ExpressionType.NotEqual
            | ExpressionType.GreaterThan
            | ExpressionType.GreaterThanOrEqual
            | ExpressionType.LessThan
            | ExpressionType.LessThanOrEqual ->
                let op =
                    match b.NodeType with
                    | ExpressionType.Equal -> "="
                    | ExpressionType.NotEqual -> "<>"
                    | ExpressionType.GreaterThan -> ">"
                    | ExpressionType.GreaterThanOrEqual -> ">="
                    | ExpressionType.LessThan -> "<"
                    | ExpressionType.LessThanOrEqual -> "<="
                    | _ -> "="

                let leftHasParam = dependsOnParameter param b.Left
                let rightHasParam = dependsOnParameter param b.Right

                if leftHasParam && not rightHasParam then
                    let col = extractColumnNameWithAlias plan b.Left
                    let clientVal = evaluateClientValue b.Right
                    if isNull clientVal then
                        if op = "=" then Some $"({col} IS NULL)"
                        elif op = "<>" then Some $"({col} IS NOT NULL)"
                        else None
                    else
                        let pName = $"@p{plan.Parameters.Count}"
                        plan.Parameters.Add(pName, clientVal)
                        Some $"({col} {op} {pName})"
                elif not leftHasParam && rightHasParam then
                    let col = extractColumnNameWithAlias plan b.Right
                    let clientVal = evaluateClientValue b.Left
                    let invOp =
                        match op with
                        | ">" -> "<"
                        | ">=" -> "<="
                        | "<" -> ">"
                        | "<=" -> ">="
                        | other -> other
                    if isNull clientVal then
                        if invOp = "=" then Some $"({col} IS NULL)"
                        elif invOp = "<>" then Some $"({col} IS NOT NULL)"
                        else None
                    else
                        let pName = $"@p{plan.Parameters.Count}"
                        plan.Parameters.Add(pName, clientVal)
                        Some $"({col} {invOp} {pName})"
                elif leftHasParam && rightHasParam then
                    let leftCol = extractColumnNameWithAlias plan b.Left
                    let rightCol = extractColumnNameWithAlias plan b.Right
                    if not (String.IsNullOrWhiteSpace leftCol) && not (String.IsNullOrWhiteSpace rightCol) then
                        Some $"({leftCol} {op} {rightCol})"
                    else
                        None
                else
                    None
            | _ -> None
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Not ->
            match translatePredicate param plan u.Operand with
            | Some inner -> Some $"(NOT {inner})"
            | None -> None
        | :? MethodCallExpression as mc ->
            if mc.Method.DeclaringType = typeof<string> && mc.Arguments.Count = 1 && dependsOnParameter param mc.Object && not (dependsOnParameter param mc.Arguments.[0]) then
                let col = extractColumnNameWithAlias plan mc.Object
                let rawVal = evaluateClientValue mc.Arguments.[0]
                if isNull rawVal then None
                else
                    let sVal = string rawVal
                    let pName = $"@p{plan.Parameters.Count}"
                    match mc.Method.Name with
                    | "Contains" ->
                        plan.Parameters.Add(pName, box ("%" + sVal + "%"))
                        Some $"({col} LIKE {pName})"
                    | "StartsWith" ->
                        plan.Parameters.Add(pName, box (sVal + "%"))
                        Some $"({col} LIKE {pName})"
                    | "EndsWith" ->
                        plan.Parameters.Add(pName, box ("%" + sVal))
                        Some $"({col} LIKE {pName})"
                    | _ -> None
            else
                None
        | _ -> None

    let rec translateScalarExpr (dialect: SqlDialect) (plan: PushdownQueryPlan) (expr: Expression) : string option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | null -> None
        | :? ConstantExpression as c ->
            if isNull c.Value then Some "NULL"
            elif c.Type = typeof<string> then
                let s = string c.Value
                let escaped = s.Replace("'", "''")
                Some $"'{escaped}'"
            elif c.Type = typeof<bool> then
                let b = c.Value :?> bool
                if dialect = SqlDialect.PostgreSql then
                    Some (if b then "TRUE" else "FALSE")
                else
                    Some (if b then "1" else "0")
            elif c.Type = typeof<DateTime> then
                let dt = c.Value :?> DateTime
                let dtStr = dt.ToString("yyyy-MM-dd HH:mm:ss.fff")
                Some $"'{dtStr}'"
            else
                Some (Convert.ToString(c.Value, CultureInfo.InvariantCulture))
        | :? MemberExpression as m ->
            let aliasOpt, colName = resolveMemberRef plan m
            match aliasOpt with
            | Some alias -> Some $"{alias}.{colName}"
            | None ->
                match m.Expression with
                | null ->
                    let v = evaluateClientValue expr
                    translateScalarExpr dialect plan (Expression.Constant(v, expr.Type))
                | _ ->
                    if plan.Joins.Count > 0 && not (colName.Contains ".") then
                        Some $"{plan.BaseAlias}.{colName}"
                    else
                        Some colName
        | :? UnaryExpression as u when u.NodeType = ExpressionType.Convert ->
            translateScalarExpr dialect plan u.Operand
        | :? BinaryExpression as b ->
            match b.NodeType with
            | ExpressionType.Add ->
                if b.Type = typeof<string> then
                    let op = if dialect = SqlDialect.SqlServer then " + " else " || "
                    match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                    | Some l, Some r -> Some $"({l}{op}{r})"
                    | _ -> None
                else
                    match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                    | Some l, Some r -> Some $"({l} + {r})"
                    | _ -> None
            | ExpressionType.Subtract ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} - {r})"
                | _ -> None
            | ExpressionType.Multiply ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} * {r})"
                | _ -> None
            | ExpressionType.Divide ->
                match translateScalarExpr dialect plan b.Left, translateScalarExpr dialect plan b.Right with
                | Some l, Some r -> Some $"({l} / {r})"
                | _ -> None
            | _ -> None
        | :? MethodCallExpression as mc ->
            if mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Concat" then
                let op = if dialect = SqlDialect.SqlServer then " + " else " || "
                let argParts = mc.Arguments |> Seq.map (translateScalarExpr dialect plan) |> Seq.toList
                if argParts |> List.forall Option.isSome then
                    let joined = argParts |> List.map Option.get |> String.concat op
                    Some $"({joined})"
                else None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToUpper" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"UPPER({o})"
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "ToLower" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"LOWER({o})"
                | None -> None
            elif mc.Method.DeclaringType = typeof<string> && mc.Method.Name = "Trim" && mc.Arguments.Count = 0 then
                match translateScalarExpr dialect plan mc.Object with
                | Some o -> Some $"TRIM({o})"
                | None -> None
            else
                None
        | _ -> None

    let rec tryExtractFromValue (value: obj) : (string * IQueryableConnector) option =
        if isNull value then None
        else
            let valType = value.GetType()
            let isPushdown = valType.Name.StartsWith("SqlPushdownQueryable")
            if isPushdown then
                let planProp = valType.GetProperty("Plan")
                let connProp = valType.GetProperty("Connector")
                if not (isNull planProp) && not (isNull connProp) then
                    let p = planProp.GetValue(value) :?> PushdownQueryPlan
                    let conn = connProp.GetValue(value) :?> IQueryableConnector
                    Some (p.BaseSql, conn)
                else
                    None
            elif typeof<IQueryable>.IsAssignableFrom(valType) then
                let q = value :?> IQueryable
                if not (isNull q.Expression) then
                    match q.Expression with
                    | :? ConstantExpression as ce when obj.ReferenceEquals(ce.Value, value) ->
                        None
                    | otherExpr ->
                        tryExtractQueryableInfo otherExpr
                else
                    None
            else
                let props = valType.GetProperties()
                let qProp = props |> Array.tryFind (fun p -> typeof<IQueryable>.IsAssignableFrom(p.PropertyType))
                match qProp with
                | Some p ->
                    try
                        let propVal = p.GetValue(value)
                        tryExtractFromValue propVal
                    with _ -> None
                | None -> None

    and tryExtractQueryableInfo (expr: Expression) : (string * IQueryableConnector) option =
        let unwrapped = unwrapQuote expr
        match unwrapped with
        | :? ConstantExpression as c when not (isNull c.Value) ->
            tryExtractFromValue c.Value
        | :? MemberExpression as m ->
            try
                let target =
                    if isNull m.Expression then null
                    else
                        match m.Expression with
                        | :? ConstantExpression as ce -> ce.Value
                        | other ->
                            let lambda = Expression.Lambda(other)
                            lambda.Compile().DynamicInvoke()
                let value =
                    match m.Member with
                    | :? System.Reflection.FieldInfo as fi -> fi.GetValue(target)
                    | :? System.Reflection.PropertyInfo as pi -> pi.GetValue(target)
                    | _ -> null
                tryExtractFromValue value
            with _ -> None
        | :? MethodCallExpression as mc ->
            if typeof<IQueryable>.IsAssignableFrom(mc.Type) then
                try
                    let lambda = Expression.Lambda(mc)
                    let res = lambda.Compile().DynamicInvoke()
                    match tryExtractFromValue res with
                    | Some info -> Some info
                    | None ->
                        if mc.Arguments.Count > 0 then
                            tryExtractQueryableInfo mc.Arguments.[0]
                        else None
                with _ ->
                    if mc.Arguments.Count > 0 then
                        tryExtractQueryableInfo mc.Arguments.[0]
                    else None
            elif mc.Arguments.Count > 0 then
                tryExtractQueryableInfo mc.Arguments.[0]
            else
                None
        | _ ->
            None

    let canPushdownAllJoins (rootExpr: Expression) (outerConn: IQueryableConnector) : bool =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let rec collect (e: Expression) =
            match e with
            | null -> ()
            | :? MethodCallExpression as mc ->
                if mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin" || mc.Method.Name = "SelectMany" then
                    methodCalls.Add(mc)
                for a in mc.Arguments do collect a
            | :? LambdaExpression as l -> collect l.Body
            | :? UnaryExpression as u -> collect u.Operand
            | :? BinaryExpression as b -> collect b.Left; collect b.Right
            | _ -> ()
        collect rootExpr
        if methodCalls.Count = 0 then true
        else
            methodCalls |> Seq.forall (fun mc ->
                if mc.Method.Name = "Join" && mc.Arguments.Count >= 5 then
                    match tryExtractQueryableInfo mc.Arguments.[1] with
                    | Some (_, innerConn) ->
                        obj.ReferenceEquals(outerConn, innerConn)
                        || (
                            (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                            && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        )
                    | None -> false
                elif mc.Method.Name = "SelectMany" && mc.Arguments.Count >= 2 then
                    let innerArg = unwrapQuote mc.Arguments.[1]
                    let innerExpr =
                        match innerArg with
                        | :? LambdaExpression as l -> l.Body
                        | other -> other
                    match tryExtractQueryableInfo innerExpr with
                    | Some (_, innerConn) ->
                        obj.ReferenceEquals(outerConn, innerConn)
                        || (
                            (outerConn :> IConnector).Kind = (innerConn :> IConnector).Kind
                            && String.Equals(outerConn.ConnectionString, innerConn.ConnectionString, StringComparison.OrdinalIgnoreCase)
                        )
                    | None -> false
                else false
            )

    let mergeExpressionIntoPlan (rootExpr: Expression) (plan: PushdownQueryPlan) (dialect: SqlDialect) : unit =
        let methodCalls = ResizeArray<MethodCallExpression>()
        let rec collectCalls (e: Expression) =
            match e with
            | :? MethodCallExpression as mc ->
                methodCalls.Add(mc)
                if mc.Arguments.Count > 0 then
                    collectCalls mc.Arguments.[0]
            | _ -> ()

        collectCalls rootExpr

        // Process calls from inner to outer so Join -> Where -> OrderBy -> Select build naturally
        let reversed = methodCalls |> Seq.toArray |> Array.rev
        let mutable currentProjectionBody: Expression option = None

        for mc in reversed do
            match mc.Method.Name with
            | "Join" when mc.Arguments.Count >= 5 ->
                let innerArg = mc.Arguments.[1]
                match tryExtractQueryableInfo innerArg with
                | Some (innerTable, _) ->
                    let nextIdx = plan.Joins.Count + 1
                    let innerAlias = sprintf "_t%d" nextIdx
                    let defaultOuterAlias = if plan.Joins.Count = 0 then plan.BaseAlias else sprintf "_t%d" plan.Joins.Count
                    let outerKeyL = unwrapQuote mc.Arguments.[2] :?> LambdaExpression
                    let innerKeyL = unwrapQuote mc.Arguments.[3] :?> LambdaExpression
                    let outerCols = extractKeyColumns plan defaultOuterAlias outerKeyL.Parameters.[0] outerKeyL.Body
                    let innerCols = extractKeyColumns plan innerAlias innerKeyL.Parameters.[0] innerKeyL.Body
                    let onClause =
                        if outerCols.Length > 0 && outerCols.Length = innerCols.Length then
                            List.zip outerCols innerCols
                            |> List.map (fun (oRef, iRef) -> $"{oRef} = {iRef}")
                            |> String.concat " AND "
                        else
                            let outerCol = extractColumnName outerKeyL.Parameters.[0] outerKeyL.Body
                            let innerCol = extractColumnName innerKeyL.Parameters.[0] innerKeyL.Body
                            let outerColRef = if outerCol.Contains(".") then outerCol else $"{defaultOuterAlias}.{outerCol}"
                            let innerColRef = if innerCol.Contains(".") then innerCol else $"{innerAlias}.{innerCol}"
                            $"{outerColRef} = {innerColRef}"

                    plan.Joins.Add({
                        JoinType = "INNER JOIN"
                        SourceTable = innerTable
                        Alias = innerAlias
                        OnClause = onClause
                    })
                | None -> ()
            | "SelectMany" when mc.Arguments.Count >= 2 ->
                let innerArg = unwrapQuote mc.Arguments.[1]
                let innerExpr =
                    match innerArg with
                    | :? LambdaExpression as l -> l.Body
                    | other -> other
                match tryExtractQueryableInfo innerExpr with
                | Some (innerTable, _) ->
                    let nextIdx = plan.Joins.Count + 1
                    let innerAlias = sprintf "_t%d" nextIdx
                    plan.Joins.Add({
                        JoinType = "INNER JOIN"
                        SourceTable = innerTable
                        Alias = innerAlias
                        OnClause = "1=1"
                    })
                | None -> ()
            | "Where" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let param = l.Parameters.[0]
                    match translatePredicate param plan l.Body with
                    | Some sqlPred ->
                        plan.WhereClauses.Add(sqlPred)
                    | None ->
                        let compiled = l.Compile()
                        plan.FallbackPredicates.Add(fun item -> unbox<bool> (compiled.DynamicInvoke(item)))
                | _ -> ()
            | "OrderBy" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "OrderByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "ThenBy" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Ascending })
            | "ThenByDescending" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.OrderBy.Add({ ColumnName = col; Direction = Descending })
            | "Take" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Take <- Some count
            | "Skip" when mc.Arguments.Count >= 2 ->
                let count = evaluateClientValue mc.Arguments.[1] :?> int
                plan.Skip <- Some count
            | "Count" ->
                plan.Projection <- Some "COUNT(*)"
            | "Sum" when mc.Arguments.Count >= 2 ->
                let col = extractColumnNameWithAlias plan mc.Arguments.[1]
                plan.Projection <- Some $"SUM({col})"
            | "Select" when mc.Arguments.Count >= 2 ->
                let arg1 = unwrapQuote mc.Arguments.[1]
                match arg1 with
                | :? LambdaExpression as l ->
                    let unwrappedBody = unwrapBlock l.Body
                    match currentProjectionBody with
                    | Some prevBody ->
                        let prevMap = extractMemberMap prevBody
                        let replacer = ParameterMemberReplacer(l.Parameters.[0], prevMap)
                        let inlined = replacer.Visit(unwrappedBody)
                        currentProjectionBody <- Some inlined
                    | None ->
                        currentProjectionBody <- Some unwrappedBody
                | _ -> ()
            | _ -> ()

        match currentProjectionBody with
        | Some rawBody ->
            let body = unwrapBlock rawBody
            match body with
            | :? NewExpression as newExpr ->
                let ctor = newExpr.Constructor
                let paramInfos = if isNull ctor then [||] else ctor.GetParameters()
                let colParts = ResizeArray<string>()
                let mutable ok = true
                for idx in 0 .. newExpr.Arguments.Count - 1 do
                    let arg = newExpr.Arguments.[idx]
                    let itemNum = idx + 1
                    let alias =
                        if not (isNull newExpr.Members) && idx < newExpr.Members.Count then
                            newExpr.Members.[idx].Name
                        elif idx < paramInfos.Length then
                            paramInfos.[idx].Name
                        else
                            sprintf "Item%d" itemNum
                    match translateScalarExpr dialect plan arg with
                    | Some sqlExpr ->
                        colParts.Add($"{sqlExpr} AS {alias}")
                    | None ->
                        ok <- false
                if ok && colParts.Count > 0 then
                    plan.Projection <- Some (String.concat ", " colParts)
            | :? MemberExpression as m ->
                match translateScalarExpr dialect plan m with
                | Some sqlExpr ->
                    let propName = m.Member.Name
                    plan.Projection <- Some $"{sqlExpr} AS {propName}"
                | None -> ()
            | :? BinaryExpression as b ->
                match translateScalarExpr dialect plan b with
                | Some sqlExpr ->
                    plan.Projection <- Some $"{sqlExpr} AS Value"
                | None -> ()
            | :? MethodCallExpression as mcExpr ->
                match translateScalarExpr dialect plan mcExpr with
                | Some sqlExpr ->
                    plan.Projection <- Some $"{sqlExpr} AS Value"
                | None -> ()
            | _ -> ()
        | None -> ()

    let rec hasJoin (expr: Expression) : bool =
        match expr with
        | null -> false
        | :? MethodCallExpression as mc ->
            if mc.Method.Name = "Join" || mc.Method.Name = "GroupJoin" || mc.Method.Name = "SelectMany" then true
            else
                (if isNull mc.Object then false else hasJoin mc.Object)
                || (mc.Arguments |> Seq.exists hasJoin)
        | :? LambdaExpression as l -> hasJoin l.Body
        | :? UnaryExpression as u -> hasJoin u.Operand
        | :? BinaryExpression as b -> hasJoin b.Left || hasJoin b.Right
        | _ -> false

    let extractPlanFromExpression (rootExpr: Expression) (baseSql: string) (baseParameters: (string * obj) list) (dialect: SqlDialect) : PushdownQueryPlan =
        let plan = PushdownQueryPlan.Create(baseSql, baseParameters)
        mergeExpressionIntoPlan rootExpr plan dialect
        plan

module LinqInMemoryFallback =
    let rec evaluate (expr: Expression) : obj =
        match expr with
        | null -> null
        | :? ConstantExpression as c -> c.Value
        | :? MethodCallExpression as mc when mc.Method.DeclaringType = typeof<Queryable> ->
            let evaluatedArgs = [|
                for a in mc.Arguments do
                    match a with
                    | :? UnaryExpression as u when u.NodeType = ExpressionType.Quote ->
                        match u.Operand with
                        | :? LambdaExpression as l ->
                            box (l.Compile())
                        | other ->
                            evaluate other
                    | :? LambdaExpression as l ->
                        box (l.Compile())
                    | other ->
                        evaluate other
            |]
            let methodGenericArgs = mc.Method.GetGenericArguments()
            let enumMethods = typeof<Enumerable>.GetMethods()
            let targetMethod =
                enumMethods
                |> Array.find (fun m ->
                    m.Name = mc.Method.Name
                    && m.IsGenericMethod
                    && m.GetGenericArguments().Length = methodGenericArgs.Length
                    && m.GetParameters().Length = evaluatedArgs.Length
                )
            let specialized = targetMethod.MakeGenericMethod(methodGenericArgs)
            specialized.Invoke(null, evaluatedArgs)
        | _ ->
            let lambda = Expression.Lambda(expr)
            let fn = lambda.Compile()
            fn.DynamicInvoke()

/// LINQ Query Provider that inspects expression trees and executes parameterized SQL pushdown queries.
type SqlPushdownQueryProvider(connector: IQueryableConnector, baseSql: string, baseParameters: (string * obj) list) =
    let dialect = SqlDialect.fromConnectorKind (connector :> IConnector).Kind

    member _.Connector = connector
    member _.BaseSql = baseSql
    member _.BaseParameters = baseParameters
    member _.Dialect = dialect

    member this.ExtractPlan(expression: Expression, ?seedPlan: PushdownQueryPlan) : PushdownQueryPlan =
        match seedPlan with
        | Some sp ->
            let cloned = sp.Clone()
            ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
            cloned
        | None ->
            ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect

    member this.ToSqlWithParameters(expression: Expression, ?seedPlan: PushdownQueryPlan) : string * (string * obj) list =
        let plan = this.ExtractPlan(expression, ?seedPlan = seedPlan)
        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        sql, allParams

    member this.ToRawSql(expression: Expression, ?seedPlan: PushdownQueryPlan) : string =
        let sql, allParams = this.ToSqlWithParameters(expression, ?seedPlan = seedPlan)
        SqlPushdownFormatting.formatSqlAndParameters sql allParams

    member this.ExecutePlanOrStream<'T>(expression: Expression, ?seedPlan: PushdownQueryPlan) : 'T seq =
        let isJoin = ExpressionPushdownTranslator.hasJoin expression
        let canPushdown = if isJoin then ExpressionPushdownTranslator.canPushdownAllJoins expression connector else true
        if isJoin && not canPushdown then
            let res = LinqInMemoryFallback.evaluate expression
            res :?> IEnumerable<'T>
        else
            let plan =
                match seedPlan with
                | Some sp ->
                    let cloned = sp.Clone()
                    ExpressionPushdownTranslator.mergeExpressionIntoPlan expression cloned dialect
                    cloned
                | None ->
                    ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect
            let sql = plan.BuildSql(dialect)
            let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))

            let rows: 'T seq =
                match connector with
                | :? IStreamingQueryableConnector as streaming ->
                    streaming.ExecuteReaderStreamWithColumns<'T> sql allParams CompiledRecordMapper.getCompiledRecordMapper<'T>
                | _ ->
                    let res = connector.ExecuteQuery sql allParams
                    seq {
                        for row in res.Rows do
                            let targetType = typeof<'T>
                            if FSharpType.IsRecord(targetType) then
                                let fields = FSharpType.GetRecordFields(targetType)
                                let ctor = FSharpValue.PreComputeRecordConstructor(targetType)
                                let args = Array.zeroCreate fields.Length
                                for i in 0 .. fields.Length - 1 do
                                    let v = if i < row.Length then row.[i] else null
                                    args.[i] <- RecordReaders.ConvertValue v fields.[i].PropertyType
                                yield unbox<'T> (ctor args)
                            else
                                if row.Length > 0 then
                                    yield unbox<'T> (RecordReaders.ConvertValue row.[0] targetType)
                    }

            if plan.FallbackPredicates.Count > 0 then
                rows |> Seq.filter (fun item -> plan.FallbackPredicates |> Seq.forall (fun p -> p (box item)))
            else
                rows

    member this.ExecuteStream<'T>(expression: Expression) : 'T seq =
        this.ExecutePlanOrStream<'T>(expression)

    member this.ExecuteScalar<'TResult>(expression: Expression) : 'TResult =
        let plan = ExpressionPushdownTranslator.extractPlanFromExpression expression baseSql baseParameters dialect
        let sql = plan.BuildSql(dialect)
        let allParams = (plan.BaseParameters @ (plan.Parameters |> List.ofSeq))
        let res = connector.ExecuteQuery sql allParams
        let firstRow = res.Rows |> Seq.tryHead
        match firstRow with
        | Some row when row.Length > 0 && not (isNull row.[0]) && not (Convert.IsDBNull(row.[0])) ->
            unbox<'TResult> (RecordReaders.ConvertValue row.[0] typeof<'TResult>)
        | _ ->
            Unchecked.defaultof<'TResult>

    interface IQueryProvider with
        member this.CreateQuery(expression: Expression) : IQueryable =
            let elemType =
                let t = expression.Type
                if t.IsGenericType && typeof<IEnumerable>.IsAssignableFrom(t) then
                    t.GetGenericArguments().[0]
                else
                    t
            Activator.CreateInstance(
                typedefof<SqlPushdownQueryable<_>>.MakeGenericType(elemType),
                this, expression, connector, baseSql, baseParameters, PushdownQueryPlan.Create(baseSql, baseParameters)
            ) :?> IQueryable

        member this.CreateQuery<'TElement>(expression: Expression) : IQueryable<'TElement> =
            SqlPushdownQueryable<'TElement>(this, expression, connector, baseSql, baseParameters, PushdownQueryPlan.Create(baseSql, baseParameters)) :> IQueryable<'TElement>

        member this.Execute(expression: Expression) : obj =
            this.ExecuteScalar<obj>(expression)

        member this.Execute<'TResult>(expression: Expression) : 'TResult =
            let targetType = typeof<'TResult>
            if typeof<IEnumerable>.IsAssignableFrom(targetType) && targetType <> typeof<string> then
                let elemType =
                    if targetType.IsGenericType then targetType.GetGenericArguments().[0]
                    elif targetType.IsArray then targetType.GetElementType()
                    else typeof<obj>
                let method = typeof<SqlPushdownQueryProvider>.GetMethod("ExecuteStream").MakeGenericMethod([| elemType |])
                method.Invoke(this, [| expression |]) :?> 'TResult
            else
                this.ExecuteScalar<'TResult>(expression)

/// Strongly-typed IQueryable that captures LINQ expressions and provides pushdown filtering, paging, and sorting.
and SqlPushdownQueryable<'T>(
    provider: IQueryProvider,
    expression: Expression,
    connector: IQueryableConnector,
    baseSql: string,
    baseParameters: (string * obj) list,
    plan: PushdownQueryPlan
) =
    new(connector: IQueryableConnector, baseSql: string, ?baseParameters: (string * obj) list) =
        let p = defaultArg baseParameters []
        let provider = SqlPushdownQueryProvider(connector, baseSql, p)
        let plan = PushdownQueryPlan.Create(baseSql, p)
        SqlPushdownQueryable<'T>(provider, Expression.Constant(null, typeof<IQueryable<'T>>), connector, baseSql, p, plan)

    member _.Plan = plan
    member _.Connector = connector
    member _.BaseSql = baseSql
    member _.BaseParameters = baseParameters

    /// Appends a raw parameterized WHERE clause directly to the pushdown query.
    member this.Where(filterSql: string, ?parameters: (string * obj) list) : SqlPushdownQueryable<'T> =
        let p = defaultArg parameters []
        let newPlan = plan.Clone()
        newPlan.WhereClauses.Add($"({filterSql})")
        for (k, v) in p do
            newPlan.Parameters.Add(k, v)
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Configures 1-based pagination directly pushed down to database LIMIT/OFFSET or SQL Server OFFSET/FETCH.
    member this.Page(page: int, pageSize: int) : SqlPushdownQueryable<'T> =
        let validPage = if page < 1 then 1 else page
        let validSize = if pageSize < 1 then 10 else pageSize
        let skip = (validPage - 1) * validSize
        let newPlan = plan.Clone()
        newPlan.Skip <- Some skip
        newPlan.Take <- Some validSize
        SqlPushdownQueryable<'T>(provider, (this :> IQueryable<'T>).Expression, connector, baseSql, baseParameters, newPlan)

    /// Extracts the raw SQL query generated for this queryable without executing it against the database.
    member this.ToRawSql() : string =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToRawSql(expr, plan)

    /// Extracts the raw SQL query and parameter bindings for this queryable without executing it against the database.
    member this.ToSqlWithParameters() : string * (string * obj) list =
        let p = provider :?> SqlPushdownQueryProvider
        let expr = (this :> IQueryable<'T>).Expression
        p.ToSqlWithParameters(expr, plan)

    interface IQueryable<'T> with
        member _.Provider = provider
        member this.Expression = if isNull expression || (match expression with :? ConstantExpression as c -> isNull c.Value | _ -> false) then Expression.Constant(this, typeof<IQueryable<'T>>) :> Expression else expression
        member _.ElementType = typeof<'T>

    interface IOrderedQueryable<'T>

    interface IEnumerable<'T> with
        member this.GetEnumerator() : IEnumerator<'T> =
            let p = provider :?> SqlPushdownQueryProvider
            let expr = (this :> IQueryable<'T>).Expression
            let seqResult = p.ExecutePlanOrStream<'T>(expr, plan)
            seqResult.GetEnumerator()

    interface IEnumerable with
        member this.GetEnumerator() =
            (this :> IEnumerable<'T>).GetEnumerator() :> IEnumerator

module SqlPushdownQuery =
    open SqlPushdownFormatting

    let rec tryFindPushdownSource (expr: Expression) : (IQueryable * PushdownQueryPlan option) option =
        match expr with
        | null -> None
        | :? ConstantExpression as c when not (isNull c.Value) ->
            match c.Value with
            | :? IQueryable as iq when not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> ->
                let t = c.Value.GetType()
                let planProp = t.GetProperty("Plan")
                let plan = if isNull planProp then None else Some (planProp.GetValue(c.Value) :?> PushdownQueryPlan)
                Some (iq, plan)
            | _ -> None
        | :? MethodCallExpression as m ->
            m.Arguments
            |> Seq.tryPick tryFindPushdownSource
            |> Option.orElseWith (fun () -> tryFindPushdownSource m.Object)
        | :? UnaryExpression as u ->
            tryFindPushdownSource u.Operand
        | _ -> None

    let extractSqlWithParameters (query: obj) : string * (string * obj) list =
        match query with
        | null -> ("", [])
        | :? string as s -> (s, [])
        | :? PushdownQueryPlan as plan ->
            let sql = plan.BuildSql(SqlDialect.Sqlite)
            let parms = plan.BaseParameters @ (plan.Parameters |> List.ofSeq)
            (sql, parms)
        | :? IQueryable as iq ->
            if not (isNull iq.Provider) && iq.Provider.GetType() = typeof<SqlPushdownQueryProvider> then
                let provider = iq.Provider :?> SqlPushdownQueryProvider
                let planProp = iq.GetType().GetProperty("Plan")
                let seedPlan = if isNull planProp then None else Some (planProp.GetValue(iq) :?> PushdownQueryPlan)
                provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
            else
                match tryFindPushdownSource iq.Expression with
                | Some (sourceIq, seedPlan) ->
                    let provider = sourceIq.Provider :?> SqlPushdownQueryProvider
                    provider.ToSqlWithParameters(iq.Expression, ?seedPlan = seedPlan)
                | None ->
                    (string iq, [])
        | other ->
            let t = other.GetType()
            let toRawSqlMethod = t.GetMethod("ToRawSql", Type.EmptyTypes)
            if not (isNull toRawSqlMethod) then
                let res = toRawSqlMethod.Invoke(other, [||]) :?> string
                (res, [])
            else
                (string other, [])

    let extractRawSql (query: obj) : string =
        let sql, parms = extractSqlWithParameters query
        formatSqlAndParameters sql parms

    do
        ResultKind.RegisterRawSqlExtractor(extractRawSql, extractSqlWithParameters)



