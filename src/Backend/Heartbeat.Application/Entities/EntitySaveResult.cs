namespace Heartbeat.Application.Entities;

public enum EntitySaveResult
{
    Created,
    SavedExisting,
    CategoryConflict,
    InvalidContent,
}
