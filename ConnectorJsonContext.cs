using System.Text.Json.Serialization;
using Thoth.Integration.Abstractions;

namespace Thoth.Connectors.Siecle;

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConnectorRequest))]
[JsonSerializable(typeof(ConnectorLogPayload))]
[JsonSerializable(typeof(ConnectorContext))]
[JsonSerializable(typeof(ConnectorSource))]
[JsonSerializable(typeof(List<ConnectorSource>))]
[JsonSerializable(typeof(MappingProfile))]
[JsonSerializable(typeof(InspectionResult))]
[JsonSerializable(typeof(MappingTargetDescriptor))]
[JsonSerializable(typeof(List<MappingTargetDescriptor>))]
[JsonSerializable(typeof(IngestionResult))]
[JsonSerializable(typeof(ThothRawIdentity))]
[JsonSerializable(typeof(ScopedAssignment))]
[JsonSerializable(typeof(List<ScopedAssignment>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<Dictionary<string, string>>))]
[JsonSerializable(typeof(UserRole))]
public partial class ConnectorJsonContext : JsonSerializerContext { }
