using System;

namespace MrWhoOidc.Auth.Observability;

/// <summary>
/// Audit sink interface for emitting structured audit events.
/// Provides hashing for PII-safe actor/subject identifiers.
/// </summary>
public interface IAuditSink
{
    /// <summary>
    /// Emit a structured audit event of the given type with a payload object.
    /// </summary>
    void Emit(string type, object payload);

    /// <summary>
    /// Returns a hashed (SHA-256) version of the input value, or null if empty.
    /// Used for PII-safe actor/subject identifiers in audit events.
    /// </summary>
    string? HashValue(string? value);
}

/// <summary>
/// No-op audit sink that silently swallows all events.
/// Used when audit emission is disabled or not configured.
/// </summary>
public sealed class NoopAuditSink : IAuditSink
{
    public void Emit(string type, object payload) { }
    public string? HashValue(string? value) => null;
}
