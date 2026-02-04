using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;

namespace AdminESP;

public partial class AdminESP : BasePlugin {
  private void SetupESP()
  {
    // Hook for player connect full: initialize ESP data only for human players with permissions (bots cannot use ESP commands)
    Core.GameEvent.HookPre<EventPlayerConnectFull>((@event) =>
    {
      try
      {
        var player = @event.UserIdPlayer;
        if (player != null && !player.IsFakeClient) // Only human players
        {
          var (hasFull, hasLimited) = CheckPermissions(player.SteamID);
          if (hasFull || hasLimited)
          {
            espEnabled.TryAdd(player.PlayerID, false); // Default to disabled
            Log($"Initialized ESP data for {player.SteamID} (PlayerID {player.PlayerID})", LogLevel.Info);
          }
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventPlayerConnectFull: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });

    // Hook for spawn: apply glow and update viewer visibility after spawning
    // Using HookPost to ensure everything is initialized after the spawn completes
    Core.GameEvent.HookPost<EventPlayerSpawn>((@event) =>
    {
      try
      {
        var player = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.PlayerID == @event.UserId);
        if (player == null)
        {
          Log($"Player not found for UserId {@event.UserId}", LogLevel.Warning);
          return HookResult.Continue;
        }
        if (player.PlayerPawn == null || !player.PlayerPawn.IsValid)
        {
          Log($"PlayerPawn null or invalid for {player.SteamID}", LogLevel.Warning);
          return HookResult.Continue;
        }

        // Use NextTick to ensure player model is fully loaded before checking/creating glow
        var capturedPlayer = player; // Capture for closure
        Core.Scheduler.NextTick(() =>
        {
          try
          {
            // Re-validate player and pawn are still valid
            if (!HasValidPawn(capturedPlayer))
            {
              Log($"[SPAWN] Player or pawn became invalid before NextTick for PlayerId={capturedPlayer?.PlayerID} (SteamId={capturedPlayer?.SteamID})", LogLevel.Warning);
              return;
            }

            // 1. TARGET Logic: Create/update glow for this player
            // Check if model changed and clean up old glow
            if (glowApplied.TryGetValue(capturedPlayer.PlayerID, out var existingGlow))
            {
              string currentModel = GetPlayerModelName(capturedPlayer);
              if (existingGlow.ModelName != currentModel)
              {
                Log($"[SPAWN] Model changed for PlayerId={capturedPlayer.PlayerID} (SteamId={capturedPlayer.SteamID}): {existingGlow.ModelName} -> {currentModel}", LogLevel.Info);
                DestroyGlow(capturedPlayer.PlayerID, "EventPlayerSpawn:ModelChange");
              }
              else
              {
                Log($"[SPAWN] PlayerId={capturedPlayer.PlayerID} already has glow with matching model: {currentModel}", LogLevel.Debug);
                return; // Model hasn't changed, no need to recreate
              }
            }

            // Apply glow if there is at least one viewer with ESP enabled (excluding the player themselves)
            if (HasActiveViewers(capturedPlayer.PlayerID))
            {
              Log($"[SPAWN] Creating glow for PlayerId={capturedPlayer.PlayerID} (SteamId={capturedPlayer.SteamID})", LogLevel.Info);
              SetGlow(capturedPlayer);
            }
            else
            {
              Log($"[SPAWN] No viewers with ESP enabled, skipping glow for PlayerId={capturedPlayer.PlayerID} (SteamId={capturedPlayer.SteamID})", LogLevel.Debug);
            }
          }
          catch (Exception ex)
          {
            Log($"Error in NextTick spawn logic: {ex.Message}", LogLevel.Error);
          }
        });

        // 2. VIEWER Logic: Update visibility if this player has ESP enabled
        // If they respawned and mode is DeadOnly, they should lose vision of glows
        if (espEnabled.TryGetValue(player.PlayerID, out bool viewerEnabled) && viewerEnabled)
        {
          UpdateViewerTransmits(player.SteamID);
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventPlayerSpawn: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });

    // Hook for disconnection: destroy glow and remove from dictionary
    Core.GameEvent.HookPre<EventPlayerDisconnect>((@event) =>
    {
      try
      {
        // Use UserId (PlayerID) instead of SteamID to correctly identify bots
        var player = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.PlayerID == @event.UserId);
        if (player != null)
        {
          DestroyGlow(player.PlayerID, "EventPlayerDisconnect");
          espEnabled.TryRemove(player.PlayerID, out _);
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventPlayerDisconnect: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });

    // Hook for death: 
    // 1. Target: destroy glow to prevent it from following ragdoll
    // 2. Viewer: if the viewer dies and has ESP (and mode is DeadOnly), they should start seeing the glows
    Core.GameEvent.HookPre<EventPlayerDeath>((@event) =>
    {
      try
      {
        // Use UserId (PlayerID) instead of SteamID to correctly identify bots
        var player = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.PlayerID == @event.UserId);
        if (player == null)
        {
          Log($"[DEATH] Player not found for UserId {@event.UserId}", LogLevel.Warning);
          return HookResult.Continue;
        }

        Log($"[DEATH] Processing death for PlayerId={player.PlayerID} (SteamId={player.SteamID})", LogLevel.Info);

        // 1. Logic for Target (Glow Victim)
        // Destroy glow to prevent it from following the ragdoll/corpse
        if (glowApplied.ContainsKey(player.PlayerID))
        {
          Log($"[DEATH] Destroying glow for PlayerId={player.PlayerID} (SteamId={player.SteamID})", LogLevel.Info);
          DestroyGlow(player.PlayerID, "EventPlayerDeath");
        }

        // 2. Logic for Viewer (ESP User)
        if (espEnabled.TryGetValue(player.PlayerID, out bool isEnabled) && isEnabled)
        {
             // If the admin dies, refresh their vision
             UpdateViewerTransmits(player.SteamID);
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventPlayerDeath: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });

    // Hook for round end: destroy all glows (but preserve espEnabled state)
    Core.GameEvent.HookPre<EventRoundEnd>((@event) =>
    {
      try
      {
        int playersWithESP = espEnabled.Count(kvp => kvp.Value);
        Log($"Round ending: Destroying {glowApplied.Count} glows, preserving ESP state for {playersWithESP}/{espEnabled.Count} players", LogLevel.Info);
        
        foreach (var steamId in glowApplied.Keys.ToList())
        {
          DestroyGlow(steamId, "EventRoundEnd");
        }
        
        // Verify state is preserved
        int stillEnabled = espEnabled.Count(kvp => kvp.Value);
        if (stillEnabled > 0)
        {
          Log($"ESP state preserved: {stillEnabled} players still have ESP enabled for next round", LogLevel.Info);
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventRoundEnd: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });
    
    // Hook for team change: reset glow to update color (target) AND update visibility (viewer)
    Core.GameEvent.HookPre<EventPlayerTeam>((@event) =>
    {
      try
      {
        // Use UserId (PlayerID) instead of SteamID to correctly identify bots
        var player = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.PlayerID == @event.UserId);
        if (player == null)
        {
          Log($"[TEAM_CHANGE] Player not found for UserId {@event.UserId}", LogLevel.Warning);
          return HookResult.Continue;
        }

        Log($"[TEAM_CHANGE] Processing team change for PlayerId={player.PlayerID} (SteamId={player.SteamID})", LogLevel.Info);

        // 1. If it's TARGET (has glow applied), destroy it so it recreates with the new team color
        if (glowApplied.ContainsKey(player.PlayerID))
        {
          Log($"[TEAM_CHANGE] Destroying glow for PlayerId={player.PlayerID} (SteamId={player.SteamID}) to recreate with new team color", LogLevel.Info);
          DestroyGlow(player.PlayerID, "EventPlayerTeam");
          
          // If player is alive and viewers exist, recreate glow with new color
          if (HasValidPawn(player) && HasActiveViewers(player.PlayerID))
          {
            // Use NextTick to ensure player model is updated with new team before applying glow
            var capturedPlayer = player; // Capture for closure
            Core.Scheduler.NextTick(() =>
            {
              try
              {
                // Re-validate player and pawn are still valid
                if (HasValidPawn(capturedPlayer))
                {
                  SetGlow(capturedPlayer);
                  Log($"Recreated glow for {capturedPlayer.SteamID} with new team color", LogLevel.Info);
                }
                else
                {
                  Log($"[TEAM_CHANGE] Player or pawn became invalid before NextTick for PlayerId={capturedPlayer?.PlayerID} (SteamId={capturedPlayer?.SteamID})", LogLevel.Warning);
                }
              }
              catch (Exception ex)
              {
                Log($"Error in NextTick SetGlow (team change): {ex.Message}", LogLevel.Error);
              }
            });
          }
        }

        // 2. If it's VIEWER (has ESP enabled), we need to refresh their visibility
        // If the mode is DeadOnly (1) or SpecOnly (2), changing team may gain/lose vision.
        if (espEnabled.TryGetValue(player.PlayerID, out bool viewerEnabled) && viewerEnabled)
        {
            // Update transmits based on new team/state
            UpdateViewerTransmits(player.SteamID);
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventPlayerTeam: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });


    // Hook for bot takeover: destroy glow on bot and update for player if needed
    Core.GameEvent.HookPost<EventBotTakeover>((@event) =>
    {
      try
      {
        var bot = Core.PlayerManager.GetAllPlayers().FirstOrDefault(p => p.PlayerID == @event.BotID);
        if (bot != null)
        {
          Log($"[BOT_TAKEOVER] Destroying glow for bot PlayerId={bot.PlayerID}", LogLevel.Info);
          DestroyGlow(bot.PlayerID, "EventBotTakeover");
        }


        var player = @event.UserIdPlayer;
        if (player != null)
        {
          // If the player has ESP enabled, update their view
          if (espEnabled.TryGetValue(player.PlayerID, out bool isEnabled) && isEnabled)
          {
            UpdateViewerTransmits(player.SteamID);
          }
          
          // No need for EnsureGlowHiddenFromOwner - targets are automatically excluded in SetGlow

          // Recreate glow for the player (now controlling the bot) if there are viewers with ESP enabled
          if (HasValidPawn(player) && HasActiveViewers(player.PlayerID))
          {
            Log($"Recreating glow for player {player.SteamID} after bot takeover", LogLevel.Info);
            
            // Use NextTick to ensure everything is settled
            var capturedPlayer = player;
            Core.Scheduler.NextTick(() =>
            {
              try
              {
                if (HasValidPawn(capturedPlayer))
                {
                  SetGlow(capturedPlayer);
                }
                else
                {
                  Log($"[BOT_TAKEOVER] Player or pawn became invalid after NextTick for PlayerId={capturedPlayer?.PlayerID} (SteamId={capturedPlayer?.SteamID})", LogLevel.Warning);
                }
              }
              catch (Exception ex)
              {
                Log($"Error in NextTick SetGlow after takeover: {ex.Message}", LogLevel.Error);
              }
            });
          }
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventBotTakeover: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });

    // Hook for round prestart: destroy all glows before round reset to handle transitions
    Core.GameEvent.HookPre<EventRoundPrestart>((@event) =>
    {
      try
      {
        Log("Round prestart, destroying all glows to prevent invalid entity access", LogLevel.Info);
        foreach (var kvp in glowApplied.ToList())
        {
          DestroyGlow(kvp.Key, "EventRoundPrestart");
        }
      }
      catch (Exception ex)
      {
        Log($"Error in EventRoundPrestart: {ex.Message}", LogLevel.Error);
      }
      return HookResult.Continue;
    });
  }
}