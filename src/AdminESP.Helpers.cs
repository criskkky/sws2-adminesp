using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Natives;
using System.Collections.Concurrent;

namespace AdminESP;

public partial class AdminESP : BasePlugin {
  
  // ============================================================================
  // Enums
  // ============================================================================
  
  // Logging levels
  private enum LogLevel { Debug, Info, Warning, Error }
  
  // ============================================================================
  // Fields
  // ============================================================================
  
  // Thread-safe dictionaries for concurrent access from multiple events
  // glowApplied: Tracks active glow entities per player (PlayerID -> glow state)
  // - 'applied': whether glow is currently active
  // - 'glow':the visible glow entity
  // - 'relay': invisible entity that follows the player (glow follows relay)
  // - 'modelName': cached model name to detect model changes
  private ConcurrentDictionary<int, (bool applied, CDynamicProp? glow, CDynamicProp? relay, string modelName)> glowApplied = new();
  
  // espEnabled: Tracks which players have ESP enabled (PlayerID -> enabled state)
  private ConcurrentDictionary<int, bool> espEnabled = new();
  
  // ============================================================================
  // Public Methods
  // ============================================================================
  
  // Applies glow effect to a target player with team-based color
  // Team 2 (T) = Red (255,0,0), Team 3 (CT) = Blue (0,0,255)
  public void SetGlow(IPlayer target)
  {
    if (target.Pawn == null) return;
    if (glowApplied.ContainsKey(target.PlayerID)) return; // Already applied (ContainsKey is safe for existence check)
    
    // Set color based on team: T=Red, CT=Blue
    int r = target.Controller.TeamNum == 2 ? 255 : 0;
    int g = 0;
    int b = target.Controller.TeamNum == 2 ? 0 : 255;
    int a = 255;
    SetGlow(target.Pawn, r, g, b, a, target.PlayerID);
  }
  
  // ============================================================================
  // Private Methods
  // ============================================================================
  
  // Centralized permission checking to avoid code duplication
  // Returns tuple: (hasFull, hasLimited)
  // - hasFull: Can see ESP all the time (no restrictions)
  // - hasLimited: Can only see ESP when dead or spectating
  // Note: Empty permission strings in config mean "allow all"
  private (bool hasFull, bool hasLimited) CheckPermissions(ulong steamId)
  {
    bool hasFull = string.IsNullOrEmpty(Config.FullPermission) || 
                   Core.Permission.PlayerHasPermission(steamId, Config.FullPermission);
    bool hasLimited = string.IsNullOrEmpty(Config.LimitedPermission) || 
                      Core.Permission.PlayerHasPermission(steamId, Config.LimitedPermission);
    return (hasFull, hasLimited);
  }

  // Determines if a viewer can see ESP glows based on their permissions and current state
  // - Full permission: always returns true
  // - Limited permission: returns true only if dead or spectating (TeamNum == 1)
  // - No permission: returns false
  private bool CanViewerSeeGlow(IPlayer viewer)
  {
    var (hasFull, hasLimited) = CheckPermissions(viewer.SteamID);
    if (hasFull) return true;
    if (hasLimited) return viewer.Controller.TeamNum == 1 || !viewer.Controller.PawnIsAlive;
    return false;
  }

  // Improved logging with levels
  private void Log(string message, LogLevel level = LogLevel.Debug)
  {
    if (!Config.DebugMode) return; // Hide all logs when DebugMode is false
    Console.WriteLine($"[AdminESP:{level}] {message}");
  }

  // Gets the player's current character model path
  // Returns the actual model if available, otherwise returns team-based fallback
  // This is used to detect model changes (e.g., skin changes) that require glow recreation
  private string GetPlayerModelName(IPlayer player)
  {
    // Fallback models: Team 2 (T) = Phoenix, Team 3 (CT) = SAS
    string fallback = (player.Controller.TeamNum == 2) ? "characters/models/tm_phoenix/tm_phoenix.vmdl" : "characters/models/ctm_sas/ctm_sas.vmdl";

    var pawn = player.PlayerPawn;
    var modelNode = pawn?.CBodyComponent?.SceneNode;
    if (modelNode != null)
    {
      var skeleton = modelNode.GetSkeletonInstance();
      var mName = skeleton?.ModelState?.ModelName;
      if (!string.IsNullOrEmpty(mName))
      {
        return mName;
      }
    }
    return fallback;
  }

  // Method to apply glow to a player
  private void SetGlow(CBaseEntity entity, int ColorR, int ColorG, int ColorB, int ColorA, int playerId)
  {
    try
    {
      Log($"[GLOW_CREATE] Starting SetGlow for PlayerId={playerId}, Color=({ColorR},{ColorG},{ColorB})", LogLevel.Info);
      
      if (entity == null || !entity.IsValid || entity.CBodyComponent?.SceneNode?.GetSkeletonInstance()?.ModelState == null)
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: Entity validation failed (entity={entity != null}, valid={entity?.IsValid}, component={entity?.CBodyComponent != null})", LogLevel.Warning);
        return;
      }

      CDynamicProp modelGlow = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic");
      CDynamicProp modelRelay = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic");

      if (modelGlow == null || modelRelay == null)
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: Entity creation failed (glow={modelGlow != null}, relay={modelRelay != null})", LogLevel.Error);
        return;
      }

      string modelName = entity.CBodyComponent.SceneNode.GetSkeletonInstance().ModelState.ModelName;
      if (string.IsNullOrEmpty(modelName))
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: Model name is null or empty", LogLevel.Warning);
        return;
      }
      
      Log($"[GLOW_CREATE] PlayerId={playerId} using model: {modelName}", LogLevel.Info);

      // Clear EF_NODRAW flag (bit 2) to make entity visible for rendering
      modelRelay.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= unchecked((uint)~(1 << 2));

      // Configure relay entity (invisible proxy that follows the player)
      modelRelay.SetModel(modelName);
      modelRelay.Spawnflags = 256u; // Bone Merge: Sync animation with player
      modelRelay.RenderMode = RenderMode_t.kRenderNone; // Make relay invisible
      modelRelay.DispatchSpawn();

      // Clear EF_NODRAW flag for glow entity
      modelGlow.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= unchecked((uint)~(1 << 2));

      // Configure glow entity (visible glow effect)
      modelGlow.SetModel(modelName);
      modelGlow.Spawnflags = 256u; // Bone Merge: Sync animation with player
      modelGlow.DispatchSpawn();

      // Configure glow appearance
      modelGlow.Render = new Color(0, 0, 0, 1); // Alpha 1 (almost transparent) to match snippet
      modelGlow.Glow.GlowColorOverride = new Color(ColorR, ColorG, ColorB, ColorA);
      modelGlow.Glow.GlowRange = 5000; // Maximum visibility range
      modelGlow.Glow.GlowTeam = -1; // Visible to all teams
      modelGlow.Glow.GlowType = 3; // Glow type (3 = outline)
      modelGlow.Glow.GlowRangeMin = 0; // Minimum range (always visible when in range)

      // Setup entity hierarchy: Player <- Relay <- Glow
      // Relay follows player, glow follows relay (double indirection for stability)
      modelRelay.AcceptInput("FollowEntity", "!activator", entity, modelRelay);
      modelGlow.AcceptInput("FollowEntity", "!activator", modelRelay, modelGlow);

      // Save the entities in the dictionary
      glowApplied[playerId] = (true, modelGlow, modelRelay, modelName);
      
      // Configure transmit states for all viewers with ESP enabled
      UpdateTransmitsForNewGlow(playerId, modelGlow, modelRelay);
      
      Log($"[GLOW_CREATE] SUCCESS for PlayerId={playerId}: Glow entity created and configured", LogLevel.Info);
    }
    catch (Exception ex)
    {
      Log($"Error applying glow to {playerId}: {ex.Message}", LogLevel.Error);
    }
  }

  // Method to destroy glow of a player
  private void DestroyGlow(int playerId, string caller = "Unknown")
  {
    if (glowApplied.TryRemove(playerId, out var glowData))
    {
      var (applied, glow, relay, modelName) = glowData;
      
      bool glowValid = glow != null && glow.IsValid;
      bool relayValid = relay != null && relay.IsValid;
      
      Log($"[GLOW_DESTROY] PlayerId={playerId}, Caller={caller}, GlowValid={glowValid}, RelayValid={relayValid}", LogLevel.Info);
      
      if (glow != null && glow.IsValid) glow.Despawn();
      if (relay != null && relay.IsValid) relay.Despawn();
    }
    else
    {
      Log($"[GLOW_DESTROY] SKIPPED for PlayerId={playerId}, Caller={caller}: Glow not in dictionary", LogLevel.Warning);
    }
  }

  // Method to toggle ESP for a user
  private void ToggleESP(ulong steamId)
  {
    var viewer = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.SteamID == steamId);
    if (viewer == null)
    {
      Log($"Viewer not found for SteamID {steamId}", LogLevel.Warning);
      return;
    }

    int viewerId = viewer.PlayerID;
    bool newState = !espEnabled.GetOrAdd(viewerId, false);
    espEnabled[viewerId] = newState;

    Log($"Toggling ESP for SteamID {steamId} (PlayerID {viewerId}) to {newState}", LogLevel.Info);

    if (newState)
    {
      // ESP enabled: create missing glows and update transmits
      bool transmit = CanViewerSeeGlow(viewer);
      var allPlayers = Core.PlayerManager.GetAllPlayers();
      int created = 0;
      int updated = 0;
      int skipped = 0;

      foreach (var target in allPlayers)
      {
        // Skip self
        if (target.PlayerID == viewerId) continue;

        // Check if target has valid pawn
        if (transmit && target.Pawn == null)
        {
          Log($"Skipping target {target.PlayerID}: No pawn", LogLevel.Warning);
          skipped++;
          continue;
        }

        if (glowApplied.TryGetValue(target.PlayerID, out var glowData))
        {
          // Glow exists, enable transmit
          var (applied, glow, relay, modelName) = glowData;
          if (glow != null && glow.IsValid) glow.SetTransmitState(transmit, viewerId);
          if (relay != null && relay.IsValid) relay.SetTransmitState(transmit, viewerId);
          updated++;
        }
        else if (transmit && target.Pawn != null)
        {
          // Create new glow
          SetGlow(target);
          created++;
        }
      }
      Log($"Enabled ESP for {steamId}: created {created} glows, updated {updated} glows, skipped {skipped} players", LogLevel.Info);
    }
    else
    {
      // ESP disabled: just update transmits (disable all)
      UpdateViewerTransmits(steamId);
    }
  }
  // Method to refresh all glows when config changes
  private void RefreshGlowsOnConfigChange()
  {
    var allPlayers = Core.PlayerManager.GetAllPlayers();
    foreach (var viewer in allPlayers)
    {
      if (espEnabled.TryGetValue(viewer.PlayerID, out bool isEnabled) && isEnabled)
      {
         UpdateViewerTransmits(viewer.SteamID);
      }
    }
  }

  // Method to update transmit states for a specific viewer based on current permissions and state
  // This method ONLY updates existing glows, it does NOT create new ones
  // Used for: config refresh, player state changes (death/spawn/team change)
  private void UpdateViewerTransmits(ulong steamId)
  {
    var viewer = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.SteamID == steamId);
    if (viewer == null)
    {
      Log($"Viewer not found for SteamID {steamId}", LogLevel.Warning);
      return;
    }

    int viewerId = viewer.PlayerID;
    if (!espEnabled.TryGetValue(viewerId, out bool isEnabled) || !isEnabled)
    {
      // If ESP is disabled for this viewer, ensure no transmits
      foreach (var kvp in glowApplied)
      {
        var (applied, glow, relay, modelName) = kvp.Value;
        if (glow != null && glow.IsValid) glow.SetTransmitState(false, viewerId);
        if (relay != null && relay.IsValid) relay.SetTransmitState(false, viewerId);
      }
      return;
    }

    // Calculate transmit based on permissions and current state using centralized helper
    bool transmit = CanViewerSeeGlow(viewer);

    // Update transmit for all existing glows (do NOT create new ones)
    int count = 0;
    foreach (var kvp in glowApplied)
    {
      if (kvp.Key != viewerId) // Exclude self
      {
        var (applied, glow, relay, modelName) = kvp.Value;
        if (glow != null && glow.IsValid) glow.SetTransmitState(transmit, viewerId);
        if (relay != null && relay.IsValid) relay.SetTransmitState(transmit, viewerId);
        count++;
      }
      else
      {
        // Explicitly ensure viewer never sees their own glow
        var (applied, glow, relay, modelName) = kvp.Value;
        if (glow != null && glow.IsValid) glow.SetTransmitState(false, viewerId);
        if (relay != null && relay.IsValid) relay.SetTransmitState(false, viewerId);
      }
    }
    Log($"Updated transmits for {steamId} (transmit={transmit}), adjusted {count} glows", LogLevel.Debug);
  }

  // Helper method to configure transmit states for a newly created glow
  // Called after glow entities are created and added to the dictionary
  private void UpdateTransmitsForNewGlow(int targetPlayerId, CDynamicProp? glow, CDynamicProp? relay)
  {
    foreach (var viewer in Core.PlayerManager.GetAllPlayers())
    {
      // Explicitly hide glow from the target player (themselves)
      if (viewer.PlayerID == targetPlayerId)
      {
        if (glow != null && glow.IsValid) glow.SetTransmitState(false, viewer.PlayerID);
        if (relay != null && relay.IsValid) relay.SetTransmitState(false, viewer.PlayerID);
        Log($"[GLOW_CREATE] Explicitly hiding glow from target PlayerId={targetPlayerId} (themselves)", LogLevel.Debug);
        continue;
      }
      
      // Skip viewers without ESP enabled
      if (!espEnabled.TryGetValue(viewer.PlayerID, out bool isEnabled) || !isEnabled)
      {
        if (glow != null && glow.IsValid) glow.SetTransmitState(false, viewer.PlayerID);
        if (relay != null && relay.IsValid) relay.SetTransmitState(false, viewer.PlayerID);
        continue;
      }

      // Use centralized permission check
      bool transmit = CanViewerSeeGlow(viewer);
      Log($"Setting transmit {transmit} for player {viewer.PlayerID} (SteamID {viewer.SteamID}) on glow of {targetPlayerId}", LogLevel.Debug);
      if (glow != null && glow.IsValid) glow.SetTransmitState(transmit, viewer.PlayerID);
      if (relay != null && relay.IsValid) relay.SetTransmitState(transmit, viewer.PlayerID);
    }
  }
}