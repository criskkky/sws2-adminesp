namespace AdminESP;

public class AdminESPConfig
{
    public bool DebugMode { get; set; } = false;
    public bool EnableAuditLog { get; set; } = true;
    public string FullPermission { get; set; } = "adminesp.full";
    public string LimitedPermission { get; set; } = "adminesp.limited";
}
