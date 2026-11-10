# Delta Specification: Release v0.5.2

## MODIFIED Requirements

Where extension and worker packages are versioned,
the build metadata shall define version 0.5.2 across manifest configurations and compatibility constants.

Where release documentation is maintained,
the project changelogs and readmes shall document all v0.5.2 enhancements:
- Scalar option projection mapping in `SqlPushdownQueryable` and `CompiledRecordMapper`.
- F# query expression keywords syntax highlighting (`query`, `select`, `where`, etc.).
- Interpolated string expressions TextMate scoping and string literal color preservation.
- Module whitelist scoping preventing chained properties from false-positive module coloring.
- Standardized function and method highlighting (`Seq.length`, `Seq.Length`, `Seq.filter`, `.rawQuery(...)`).
- Verbatim string and comment protection with `-comment -string` injection selector.

