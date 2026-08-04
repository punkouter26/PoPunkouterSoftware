using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoPunkouterSoftware.Shared;

// ============================================================================
//  STRONGLY-TYPED IDs (NET_RULES §1 "Eradicate primitive obsession")
// ============================================================================
//
// These are the C#-side representation of identifiers that used to flow through
// the codebase as bare `string`. The wire/storage format stays plain strings —
// changing it would silently reinterpret every persisted gzipped report and
// every JSON payload — but the moment a value crosses an internal boundary, it
// is one of these structs. The implicit operators make the conversion cheap
// at the boundary and the type system does the rest.
//
// Pattern: `readonly record struct` + `JsonConverter` for trimmable WASM
// serialization without reflection. Source-generated JSON is governed by
// AppJsonContext in the client project.

[JsonConverter(typeof(SubscriptionIdConverter))]
public readonly record struct SubscriptionId(string Value)
{
    public static SubscriptionId Empty => default;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator string(SubscriptionId id) => id.Value;
}

[JsonConverter(typeof(TenantIdConverter))]
public readonly record struct TenantId(string Value)
{
    public static TenantId Empty => default;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator string(TenantId id) => id.Value;
}

[JsonConverter(typeof(ResourceGroupNameConverter))]
public readonly record struct ResourceGroupName(string Value)
{
    public static ResourceGroupName Empty => default;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator string(ResourceGroupName rg) => rg.Value;
}

[JsonConverter(typeof(AppNameConverter))]
public readonly record struct AppName(string Value)
{
    public static AppName Empty => default;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator string(AppName name) => name.Value;
}

[JsonConverter(typeof(UrlConverter))]
public readonly record struct Url(string Value)
{
    public static Url Empty => default;
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);
    public override string ToString() => Value;
    public static implicit operator string(Url url) => url.Value;
}

// ============================================================================
//  ENUMS (NET_RULES §1 "Zero magic strings")
// ============================================================================
//
// The on-the-wire / on-disk format stays a string (the existing constant
// groups in DomainVocabulary preserves the storage contract). These enums
// are the C#-side vocabulary: producers parse constant → enum at the edge,
// consumers operate on the enum, and a bidirection Map() pair keeps the
// boundary open.

public enum DeploymentEnvironment
{
    Development,
    Testing,
    Production,
    Unknown,
}

public static class DeploymentEnvironmentExtensions
{
    public const string DevelopmentName = "Development";
    public const string TestingName = "Testing";
    public const string ProductionName = "Production";

    public static DeploymentEnvironment AsEnvironment(string? name) => name switch
    {
        "Development" => DeploymentEnvironment.Development,
        "Testing" => DeploymentEnvironment.Testing,
        "Production" => DeploymentEnvironment.Production,
        _ => DeploymentEnvironment.Unknown,
    };

    public static string ToWireString(this DeploymentEnvironment env) => env switch
    {
        DeploymentEnvironment.Development => DevelopmentName,
        DeploymentEnvironment.Testing => TestingName,
        DeploymentEnvironment.Production => ProductionName,
        _ => "Unknown",
    };
}

public enum AppStatus
{
    Active,
    Inactive,
    Unknown,
}

public static class AppStatusExtensions
{
    public static AppStatus AsStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" => AppStatus.Active,
        "inactive" => AppStatus.Inactive,
        _ => AppStatus.Unknown,
    };

    public static string ToWireString(this AppStatus status) => status switch
    {
        AppStatus.Active => "active",
        AppStatus.Inactive => "inactive",
        _ => "unknown",
    };
}

public enum TlsVersion
{
    Tls1_0,
    Tls1_1,
    Tls1_2,
    Tls1_3,
    Unknown,
}

public static class TlsVersionExtensions
{
    public static TlsVersion AsTlsVersion(string? raw) => raw?.ToUpperInvariant() switch
    {
        "TLS1_0" or "TLS10" or "TLS1.0" => TlsVersion.Tls1_0,
        "TLS1_1" or "TLS11" or "TLS1.1" => TlsVersion.Tls1_1,
        "TLS1_2" or "TLS12" or "TLS1.2" => TlsVersion.Tls1_2,
        "TLS1_3" or "TLS13" or "TLS1.3" => TlsVersion.Tls1_3,
        _ => TlsVersion.Unknown,
    };

    public static string ToWireString(this TlsVersion v) => v switch
    {
        TlsVersion.Tls1_0 => "TLS1_0",
        TlsVersion.Tls1_1 => "TLS1_1",
        TlsVersion.Tls1_2 => "TLS1_2",
        TlsVersion.Tls1_3 => "TLS1_3",
        _ => "Unknown",
    };
}

public enum IncidentStatus
{
    New,
    Recovery,
}

public static class IncidentStatusExtensions
{
    public static IncidentStatus AsIncidentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "new-incident" or "new" => IncidentStatus.New,
        "recovery" => IncidentStatus.Recovery,
        _ => IncidentStatus.New,
    };

    public static string ToWireString(this IncidentStatus s) => s switch
    {
        IncidentStatus.New => "new-incident",
        IncidentStatus.Recovery => "recovery",
        _ => "new-incident",
    };
}

public enum Severity
{
    Critical,
    High,
    Medium,
    Low,
    Info,
}

public static class SeverityExtensions
{
    public static Severity AsSeverity(string? raw) => raw?.ToLowerInvariant() switch
    {
        "critical" => Severity.Critical,
        "high" => Severity.High,
        "medium" => Severity.Medium,
        "low" => Severity.Low,
        "info" => Severity.Info,
        _ => Severity.Info,
    };

    public static int Rank(this Severity s) => s switch
    {
        Severity.Critical => 0,
        Severity.High => 1,
        Severity.Medium => 2,
        Severity.Low => 3,
        Severity.Info => 4,
        _ => 5,
    };

    public static string ToWireString(this Severity s) => s switch
    {
        Severity.Critical => "critical",
        Severity.High => "high",
        Severity.Medium => "medium",
        Severity.Low => "low",
        _ => "info",
    };
}

public enum ResourceRisk
{
    Cleanup,
    Cost,
    Watch,
    Ok,
    Unknown,
}

public static class ResourceRiskExtensions
{
    public static ResourceRisk AsResourceRisk(string? raw) => raw?.ToLowerInvariant() switch
    {
        "cleanup" => ResourceRisk.Cleanup,
        "cost" => ResourceRisk.Cost,
        "watch" => ResourceRisk.Watch,
        "ok" => ResourceRisk.Ok,
        _ => ResourceRisk.Unknown,
    };

    public static int Rank(this ResourceRisk r) => r switch
    {
        ResourceRisk.Cleanup => 0,
        ResourceRisk.Cost => 1,
        ResourceRisk.Watch => 2,
        ResourceRisk.Ok => 3,
        _ => 4,
    };

    public static string ToWireString(this ResourceRisk r) => r switch
    {
        ResourceRisk.Cleanup => "cleanup",
        ResourceRisk.Cost => "cost",
        ResourceRisk.Watch => "watch",
        ResourceRisk.Ok => "ok",
        _ => "unknown",
    };
}

// ============================================================================
//  Trimmable, source-generated JSON converters
// ============================================================================

internal sealed class SubscriptionIdConverter : JsonConverter<SubscriptionId>
{
    public override SubscriptionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, SubscriptionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class TenantIdConverter : JsonConverter<TenantId>
{
    public override TenantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, TenantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class ResourceGroupNameConverter : JsonConverter<ResourceGroupName>
{
    public override ResourceGroupName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, ResourceGroupName value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class AppNameConverter : JsonConverter<AppName>
{
    public override AppName Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, AppName value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

internal sealed class UrlConverter : JsonConverter<Url>
{
    public override Url Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, Url value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
