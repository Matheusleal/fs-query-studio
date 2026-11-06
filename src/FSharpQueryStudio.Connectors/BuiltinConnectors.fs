namespace FSharpQueryStudio.Connectors

/// Single source of truth in the backend for registering all connector modules.
/// Adding support for a new connector module in the backend only requires adding its
/// registration entry in `BuiltinConnectors.all` below.
module BuiltinConnectors =

    let private sqlGen = SqlEntityGenerator() :> IConnectorEntityGenerator
    let private csvGen = CsvEntityGenerator() :> IConnectorEntityGenerator
    let private excelGen = ExcelEntityGenerator() :> IConnectorEntityGenerator
    let private jsonGen = JsonEntityGenerator() :> IConnectorEntityGenerator
    let private xmlGen = XmlEntityGenerator() :> IConnectorEntityGenerator
    let private restGen = RestEntityGenerator() :> IConnectorEntityGenerator

    /// Central catalog of all built-in connector descriptors and their entity generators.
    let all () : IConnectorDescriptor list =
        [ CustomDescriptorWithGenerator(SqliteDescriptor.Instance, sqlGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(SqlServerDescriptor.Instance, sqlGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(PostgreSqlDescriptor.Instance, sqlGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(CsvDescriptor.Instance, csvGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(ExcelDescriptor.Instance, excelGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(JsonDescriptor.Instance, jsonGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(XmlDescriptor.Instance, xmlGen) :> IConnectorDescriptor
          CustomDescriptorWithGenerator(RestDescriptor.Instance, restGen) :> IConnectorDescriptor ]

    let registerDefaults () =
        let r = ConnectorRegistry.Instance
        for descriptor in all () do
            r.Register(descriptor)

    do registerDefaults ()

