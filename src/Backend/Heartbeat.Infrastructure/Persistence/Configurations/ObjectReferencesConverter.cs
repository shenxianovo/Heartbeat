using System.Text.Json;
using Heartbeat.Contracts;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Persistence.Configurations;

internal sealed class ObjectReferencesConverter() : ValueConverter<ObjectReference[], string>(
    value => JsonSerializer.Serialize(value, Options),
    value => JsonSerializer.Deserialize<ObjectReference[]>(value, Options)!)
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

internal sealed class ObjectReferencesComparer() : ValueComparer<ObjectReference[]>(
    (left, right) => left!.SequenceEqual(right!),
    value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
    value => value.ToArray());
