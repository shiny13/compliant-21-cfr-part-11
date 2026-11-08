using Microsoft.Extensions.Logging;
using RecordSignatures.Core.Abstractions;

namespace RecordSignatures.Core.Services;

/// <summary>
/// An audit record is created before its entity is saved (the record's id becomes the entity's id). If the save then
/// fails, the audit record is marked void so the audit trail never claims something that did not happen.
/// </summary>
internal static class AuditCompensation
{
    public static async Task VoidAsync(
        IAuditTrailClient audit, ILogger logger, Guid recordId, Dictionary<string, string?> fields, string accessToken, string why)
    {
        try
        {
            fields["status"] = "NotPersisted";
            await audit.UpdateRecordAsync(recordId, fields, $"Marked void: {why}", accessToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark audit record {RecordId} as void after a failed save", recordId);
        }
    }
}
