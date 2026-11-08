namespace RecordAuditTrails.Models;

/// <summary>The kind of operator action recorded in the audit trail (21 CFR 11.10(e): create, modify, delete).</summary>
public enum AuditAction
{
    Create = 1,
    Modify = 2,
    Delete = 3,
}

/// <summary>Non-modifying access that is still logged: viewing system documentation, distributing it, exporting records.</summary>
public enum AccessAction
{
    View = 1,
    Distribute = 2,
    Export = 3,
}
