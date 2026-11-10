namespace FSharpQueryStudio.Connectors

open System
open System.Collections.Concurrent
open System.Data
open System.Globalization
open System.Linq.Expressions
open System.Reflection
open Microsoft.FSharp.Reflection

/// Static helper methods for reading primitive and complex types directly from IDataRecord without boxing.
type RecordReaders =
    static member IsSentinelString(s: string) : bool =
        if String.IsNullOrWhiteSpace s then true
        else
            let t = s.Trim().Trim('"', '\'')
            String.IsNullOrEmpty t
            || t = "-"
            || t = "--"
            || t = "?"
            || String.Equals(t, "n/a", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "na", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "null", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "none", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "nil", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "undefined", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "nan", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "infinity", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "-infinity", StringComparison.OrdinalIgnoreCase)
            || String.Equals(t, "tbd", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("#", StringComparison.Ordinal)

    static member TryParseNormalizedDecimal(clean: string) : bool * decimal =
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

    static member TryParseNormalizedDouble(clean: string) : bool * double =
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

    static member DefaultForType(targetType: Type) : obj =
        if targetType = typeof<string> then box ""
        elif targetType = typeof<DateTime> then box DateTime.MinValue
        elif targetType = typeof<DateOnly> then box DateOnly.MinValue
        elif targetType = typeof<Guid> then box Guid.Empty
        elif targetType = typeof<byte[]> then box (Array.empty<byte>)
        elif targetType.IsValueType && Nullable.GetUnderlyingType(targetType) = null then
            Activator.CreateInstance(targetType)
        else
            null

    static member internal MakeSome<'T>(v: 'T) : 'T option = Some v

    static member ConvertValue (raw: obj) (targetType: Type) : obj =
        try
            if isNull raw || Convert.IsDBNull(raw) then
                RecordReaders.DefaultForType targetType
            elif targetType = typeof<obj> then
                raw
            elif raw.GetType() = targetType then
                raw
            elif targetType.IsGenericType && targetType.GetGenericTypeDefinition() = typedefof<option<_>> then
                let innerType = targetType.GetGenericArguments().[0]
                if (raw :? string) && RecordReaders.IsSentinelString (raw :?> string) then
                    null
                else
                    let converted = RecordReaders.ConvertValue raw innerType
                    if isNull converted then null
                    else
                        let someMethod = typeof<RecordReaders>.GetMethod("MakeSome", BindingFlags.Static ||| BindingFlags.NonPublic).MakeGenericMethod([| innerType |])
                        someMethod.Invoke(null, [| converted |])
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
                    elif targetType.IsGenericType && targetType.GetGenericTypeDefinition() = typedefof<option<_>> then
                        let elemType = targetType.GetGenericArguments().[0]
                        let readOptMethod = typeof<RecordReaders>.GetMethod("ReadOption").MakeGenericMethod([| elemType |])
                        let recordParam = Expression.Parameter(typeof<IDataRecord>, "record")
                        let callExpr = Expression.Call(readOptMethod, recordParam, Expression.Constant(0))
                        let lambda = Expression.Lambda<Func<IDataRecord, 'T>>(callExpr, recordParam)
                        let compiled = lambda.Compile()
                        box (fun (record: IDataRecord) -> compiled.Invoke(record) :> obj)
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

