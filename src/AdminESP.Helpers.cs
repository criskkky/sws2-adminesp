using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Natives;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AdminESP;

public partial class AdminESP : BasePlugin
{

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
  // Using CHandle for safe entity references that auto-validate on access
  private record GlowData(
    CHandle<CDynamicProp> GlowHandle,
    CHandle<CDynamicProp> RelayHandle,
    string ModelName
  );

  private ConcurrentDictionary<int, GlowData> glowApplied = new();

  // espEnabled: Tracks which players have ESP enabled (PlayerID -> enabled state)
  private ConcurrentDictionary<int, bool> espEnabled = new();

  // ============================================================================
  // Public Methods
  // ============================================================================

  // Applies glow effect to a target player with team-based color
  // Team 2 (T) = Red (255,0,0), Team 3 (CT) = Blue (0,0,255)
  public void SetGlow(IPlayer target)
  {
    if (target.PlayerPawn == null || !target.PlayerPawn.IsValid) return;
    if (glowApplied.ContainsKey(target.PlayerID)) return; // Already applied (ContainsKey is safe for existence check)

    // Set color based on team: T=Red, CT=Blue
    int r = target.Controller.TeamNum == 2 ? 255 : 0;
    int g = 0;
    int b = target.Controller.TeamNum == 2 ? 0 : 255;
    int a = 255;
    SetGlow(target.PlayerPawn, r, g, b, a, target.PlayerID);
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

  // Validates if a player has a valid PlayerPawn
  private bool HasValidPawn(IPlayer? player)
  {
    return player?.PlayerPawn != null && player.PlayerPawn.IsValid;
  }

  // Checks if there are any viewers with ESP enabled, excluding a specific player
  private bool HasActiveViewers(int excludePlayerId)
  {
    foreach (var kvp in espEnabled)
    {
      if (kvp.Key != excludePlayerId && kvp.Value)
      {
        return true;
      }
    }
    return false;
  }

  // Improved logging with levels
  private void Log(string message, LogLevel level = LogLevel.Debug)
  {
    if (!Config.DebugMode) return; // Hide all logs when DebugMode is false
    
    switch (level)
    {
      case LogLevel.Error:
        Core.Logger.LogError($"[AdminESP] {message}");
        break;
      case LogLevel.Warning:
        Core.Logger.LogWarning($"[AdminESP] {message}");
        break;
      case LogLevel.Info:
        Core.Logger.LogInformation($"[AdminESP] {message}");
        break;
      case LogLevel.Debug:
      default:
        Core.Logger.LogDebug($"[AdminESP] {message}");
        break;
    }
  }

  // Multi-layer validation for safe entity access (prevents animation system crashes)
  private bool IsSafeToAccessEntity(CBaseEntity? entity)
  {
    if (entity == null || !entity.IsValid) return false;
    if (entity.CBodyComponent?.SceneNode == null) return false;
    var skeleton = entity.CBodyComponent.SceneNode.GetSkeletonInstance();
    if (skeleton?.ModelState == null) return false;
    return true;
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

      if (!IsSafeToAccessEntity(entity))
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: Entity validation failed", LogLevel.Warning);
        return;
      }

      string modelName = entity.CBodyComponent!.SceneNode!.GetSkeletonInstance()!.ModelState!.ModelName;
      if (string.IsNullOrEmpty(modelName))
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: Model name is null or empty", LogLevel.Warning);
        return;
      }

      Log($"[GLOW_CREATE] PlayerId={playerId} using model: {modelName}", LogLevel.Info);

      // Create entities using helper method
      var modelRelay = SafeSpawnGlowEntity(modelName, isGlow: false);
      var modelGlow = SafeSpawnGlowEntity(modelName, isGlow: true);

      if (modelRelay == null || modelGlow == null)
      {
        Log($"[GLOW_CREATE] FAILED for PlayerId={playerId}: SafeSpawnGlowEntity returned null", LogLevel.Error);
        return;
      }

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

      // Save entity handles (safer than direct references)
      var glowHandle = Core.EntitySystem.GetRefEHandle(modelGlow);
      var relayHandle = Core.EntitySystem.GetRefEHandle(modelRelay);
      glowApplied[playerId] = new GlowData(glowHandle, relayHandle, modelName);

      // Configure transmit states for all viewers with ESP enabled
      UpdateTransmitsForNewGlow(playerId, modelGlow, modelRelay);

      Log($"[GLOW_CREATE] SUCCESS for PlayerId={playerId}: Glow entity created and configured", LogLevel.Info);
    }
    catch (Exception ex)
    {
      Log($"Error applying glow to {playerId}: {ex.Message}", LogLevel.Error);
    }
  }

  // Thread-safe spawn and despawn helpers for glow entities
  private CDynamicProp? SafeSpawnGlowEntity(string modelName, bool isGlow = false)
  {
    var entity = Core.EntitySystem.CreateEntityByDesignerName<CDynamicProp>("prop_dynamic");
    if (entity == null) return null;

    // Clear EF_NODRAW flag (bit 2) to make entity visible for rendering
    entity.CBodyComponent!.SceneNode!.Owner!.Entity!.Flags &= unchecked((uint)~(1 << 2));

    // Configure entity
    entity.SetModel(modelName);
    entity.Spawnflags = 256u; // Bone Merge: Sync animation with player

    if (!isGlow)
    {
      entity.RenderMode = RenderMode_t.kRenderNone; // Make relay invisible
    }

    entity.DispatchSpawn();
    return entity;
  }

  // Thread-safe despawn helper for glow entities
  // Revalidates entity handle inside callback to prevent crashes from mapchange/entity deletion
  private void SafeDespawnGlow(CHandle<CDynamicProp> handle)
  {
    Core.Scheduler.NextWorldUpdate(() =>
    {
      if (!handle.IsValid) return;
      handle.Value?.Despawn();
    });
  }

  // Method to destroy glow of a player
  private void DestroyGlow(int playerId, string caller = "Unknown")
  {
    if (glowApplied.TryRemove(playerId, out var glowData))
    {
      bool glowValid = glowData.GlowHandle.IsValid;
      bool relayValid = glowData.RelayHandle.IsValid;

      Log($"[GLOW_DESTROY] PlayerId={playerId}, Caller={caller}, GlowValid={glowValid}, RelayValid={relayValid}", LogLevel.Info);

      // Pass handles directly to SafeDespawnGlow for proper revalidation inside callback
      SafeDespawnGlow(glowData.GlowHandle);
      SafeDespawnGlow(glowData.RelayHandle);
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
        if (transmit && !HasValidPawn(target))
        {
          Log($"Skipping target {target.PlayerID}: No valid pawn", LogLevel.Warning);
          skipped++;
          continue;
        }

        if (glowApplied.TryGetValue(target.PlayerID, out var glowData))
        {
          // Glow exists, enable transmit
          if (glowData.GlowHandle.IsValid)
          {
            glowData.GlowHandle.Value!.SetTransmitState(transmit, viewerId);
          }
          if (glowData.RelayHandle.IsValid)
          {
            glowData.RelayHandle.Value!.SetTransmitState(transmit, viewerId);
          }
          updated++;
        }
        else if (transmit && HasValidPawn(target))
        {
          // Create new glow using NextTick for consistency with spawn events
          var capturedTarget = target;
          Core.Scheduler.NextTick(() =>
          {
            try
            {
              if (HasValidPawn(capturedTarget))
              {
                SetGlow(capturedTarget);
              }
            }
            catch (Exception ex)
            {
              Log($"Error in NextTick SetGlow (ToggleESP): {ex.Message}", LogLevel.Error);
            }
          });
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
        var glowData = kvp.Value;
        if (glowData.GlowHandle.IsValid)
        {
          glowData.GlowHandle.Value!.SetTransmitState(false, viewerId);
        }
        if (glowData.RelayHandle.IsValid)
        {
          glowData.RelayHandle.Value!.SetTransmitState(false, viewerId);
        }
      }
      return;
    }

    // Calculate transmit based on permissions and current state using centralized helper
    bool transmit = CanViewerSeeGlow(viewer);

    // Update transmit for all existing glows (do NOT create new ones)
    int count = 0;
    foreach (var kvp in glowApplied)
    {
      var glowData = kvp.Value;
      bool isViewerSelf = kvp.Key == viewerId;
      bool shouldTransmit = !isViewerSelf && transmit;

      if (glowData.GlowHandle.IsValid)
      {
        glowData.GlowHandle.Value!.SetTransmitState(shouldTransmit, viewerId);
      }
      if (glowData.RelayHandle.IsValid)
      {
        glowData.RelayHandle.Value!.SetTransmitState(shouldTransmit, viewerId);
      }

      if (!isViewerSelf) count++;
    }
    Log($"Updated transmits for {steamId} (transmit={transmit}), adjusted {count} glows", LogLevel.Debug);
  }

  // Helper method to configure transmit states for a newly created glow
  // Called after glow entities are created and added to the dictionary
  private void UpdateTransmitsForNewGlow(int targetPlayerId, CDynamicProp glow, CDynamicProp relay)
  {
    foreach (var viewer in Core.PlayerManager.GetAllPlayers())
    {
      // Explicitly hide glow from the target player (themselves)
      if (viewer.PlayerID == targetPlayerId)
      {
        glow.SetTransmitState(false, viewer.PlayerID);
        relay.SetTransmitState(false, viewer.PlayerID);
        Log($"[GLOW_CREATE] Explicitly hiding glow from target PlayerId={targetPlayerId} (themselves)", LogLevel.Debug);
        continue;
      }

      // Skip viewers without ESP enabled
      if (!espEnabled.TryGetValue(viewer.PlayerID, out bool isEnabled) || !isEnabled)
      {
        glow.SetTransmitState(false, viewer.PlayerID);
        relay.SetTransmitState(false, viewer.PlayerID);
        continue;
      }

      // Use centralized permission check
      bool transmit = CanViewerSeeGlow(viewer);
      Log($"Setting transmit {transmit} for player {viewer.PlayerID} (SteamID {viewer.SteamID}) on glow of {targetPlayerId}", LogLevel.Debug);
      glow.SetTransmitState(transmit, viewer.PlayerID);
      relay.SetTransmitState(transmit, viewer.PlayerID);
    }
  }
}