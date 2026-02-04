using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdminESP;

public partial class AdminESP : BasePlugin {
  // Configuración de Debug
  private AdminESPConfig Config = new();

  public AdminESP(ISwiftlyCore core) : base(core)
  {
  }

  public override void Load(bool hotReload) {
    // Initialize configuration
    Core.Configuration
        .InitializeWithTemplate("config.jsonc", "config.template.jsonc")
        .Configure(builder => {
            builder.AddJsonFile("config.jsonc", optional: false, reloadOnChange: true);
        });

    ServiceCollection services = new();
    services.AddSwiftly(Core)
        .AddOptionsWithValidateOnStart<AdminESPConfig>()
        .BindConfiguration("AdminESP");

    var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptionsMonitor<AdminESPConfig>>();
    
    // Initial load
    Config = options.CurrentValue;

    // Hot Reload
    options.OnChange(newConfig => {
        Config = newConfig;
        Core.Logger.LogInformation($"[AdminESP] Configuration updated.");
        Log("DebugMode: " + Config.DebugMode);
        RefreshGlowsOnConfigChange();
    });

    SetupESP();
    Core.Event.OnMapUnload += OnMapUnload;
    
    // Handle hot reload: recreate ESP state for already-connected players
    if (hotReload)
    {
      Log("Hot reload detected, reinitializing ESP for connected players", LogLevel.Info);
      var allPlayers = Core.PlayerManager.GetAllPlayers();
      
      foreach (var player in allPlayers)
      {
        if (player == null) continue;
        
        // Reinitialize ESP data for human players with permissions (bots cannot use ESP commands)
        if (!player.IsFakeClient) // Human player
        {
          var (hasFull, hasLimited) = CheckPermissions(player.SteamID);
          if (hasFull || hasLimited)
          {
            espEnabled.TryAdd(player.PlayerID, false);
            Log($"Reinitialized ESP data for {player.SteamID} (PlayerID {player.PlayerID})");
          }
        }
        
        // If player is alive with valid pawn, recreate glow if someone has ESP enabled
        // Note: We default ESP to disabled after reload, so glows won't be created
        // Players need to toggle their ESP again with /esp command
        // This is safer than trying to guess who had ESP enabled before
      }
      
      Log($"Hot reload complete: Initialized {espEnabled.Count} players", LogLevel.Info);
    }
  }

  public override void Unload() {
    foreach (var playerId in glowApplied.Keys.ToList())
    {
       DestroyGlow(playerId, "PluginUnload");
    }
  }

  private void OnMapUnload(IOnMapUnloadEvent @event)
  {
    // Clean up all glows and states on map unload
    foreach (var playerId in glowApplied.Keys.ToList())
    {
      DestroyGlow(playerId, "OnMapUnload");
    }
    espEnabled.Clear();
    Log($"Map {@event.MapName} unloaded, all glows and states cleared", LogLevel.Info);
  }
} 
