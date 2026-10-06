using Heartbeat.Core;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Heartbeat.Infrastructure.Database.Converters;

public sealed class EntityIdValueConverter : ValueConverter<EntityId, Guid>
{
    public EntityIdValueConverter()
        : base(id => id.Value, value => new EntityId(value))
    {
    }
}
