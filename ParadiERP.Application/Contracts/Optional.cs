using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParadiERP.Application.Contracts;

/// <summary>
/// A request field that tells "not sent" apart from "sent as null".
///
/// <para>
/// The convention elsewhere in these contracts is that an omitted field keeps the current
/// value (<c>UpsertStoreRequest</c>'s loyalty fields, <c>UpdateUserRequest.Permissions</c>),
/// which works while null itself means nothing. It stops working the moment null is a value a
/// client needs to write — a cashier's branch binding, where null is "any terminal" — because
/// System.Text.Json hands a nullable parameter the same null whether the property was absent
/// or explicitly null. This wrapper keeps the two apart: <see cref="IsSet"/> is true only when
/// the property was present in the body, whatever it held.
/// </para>
/// <para>
/// On the wire it is invisible: the JSON property is the plain value or null, so a TypeScript
/// client sends <c>branchId: id | null</c> and omits the key to leave the binding alone.
/// </para>
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly record struct Optional<T>(T Value, bool IsSet)
{
    public static Optional<T> Of(T value) => new(value, true);
    public static Optional<T> Unset => default;
    public static implicit operator Optional<T>(T value) => Of(value);
}

public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        // Without this the serializer short-circuits a JSON null to default(Optional<T>) and the
        // whole point — that an explicit null was sent — is lost before Read is called.
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null
                ? new Optional<T>(default!, true)
                : new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options)!, true);

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            if (value.IsSet && value.Value is not null) JsonSerializer.Serialize(writer, value.Value, options);
            else writer.WriteNullValue();
        }
    }
}
