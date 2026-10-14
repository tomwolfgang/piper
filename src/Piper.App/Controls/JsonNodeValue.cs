namespace Piper.App.Controls;

/// <summary>A JSON tree node's key and value, kept beside its display text for the copy commands.</summary>
internal sealed record JsonNodeValue(string Key, string RawValue, string? StringValue);
