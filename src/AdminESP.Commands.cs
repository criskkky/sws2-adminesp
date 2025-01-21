using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared;

namespace AdminESP;

public partial class AdminESP : BasePlugin {

  // ESP toggle command: /esp or /wh
  // Two permission levels:
  // 1. Full: Can see ESP anytime (alive/dead/spectator)
  // 2. Limited: Can only see ESP when dead or in spectator mode
  [Command("esp")]
  [CommandAlias("wh")]
  public void OnEspCommand(ICommandContext context)
  {
    var sender = context.Sender;
    if (sender == null) return;
    
    // Check player's permission level
    var (hasFull, hasLimited) = CheckPermissions(sender.SteamID);
    
    // Deny access if no permissions
    if (!hasFull && !hasLimited)
    {
      sender.SendChat($"{Helper.ChatColors.Red}{Core.Localizer["adminesp.no_permission"]}");
      return;
    }
    
    // Limited permission users can only use ESP when dead or spectating
    // Full permission users can use ESP anytime (skip this check)
    if (!hasFull && hasLimited && sender.Controller != null && sender.Controller.PawnIsAlive && sender.Controller.TeamNum != 1)
    {
      sender.SendChat($"{Helper.ChatColors.Red}{Core.Localizer["adminesp.unsupported_mode"]}");
      return;
    }
    
    Log($"ESP command executed by SteamID: {sender.SteamID}", LogLevel.Info);
    ToggleESP(sender.SteamID);
    
    bool isEnabled = espEnabled.TryGetValue(sender.PlayerID, out bool enabled) && enabled;
    // Audit log to console for server owner (if enabled in config)
    if (Config.EnableAuditLog)
    {
        Console.WriteLine($"[AdminESP] {DateTime.Now:yyyy-MM-dd HH:mm:ss} SteamID: {sender.SteamID} - Player {sender.Controller?.PlayerName ?? "Unknown"} toggled ESP to {(isEnabled ? "ON" : "OFF")}");
    }
    sender.SendChat($"{Helper.ChatColors.Red}{Core.Localizer["adminesp.prefix"]} {Helper.ChatColors.Default}{Core.Localizer[isEnabled ? "adminesp.enabled" : "adminesp.disabled"]}");
  }
}