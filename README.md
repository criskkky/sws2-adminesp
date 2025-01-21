<div align="center">

# [SwiftlyS2] AdminESP

[![GitHub Release](https://img.shields.io/github/v/release/criskkky/sws2-adminesp?color=FFFFFF&style=flat-square)](https://github.com/criskkky/sws2-adminesp/releases/latest)
[![GitHub Issues](https://img.shields.io/github/issues/criskkky/sws2-adminesp?color=FF0000&style=flat-square)](https://github.com/criskkky/sws2-adminesp/issues)
[![GitHub Downloads](https://img.shields.io/github/downloads/criskkky/sws2-adminesp/total?color=blue&style=flat-square)](https://github.com/criskkky/sws2-adminesp/releases)
[![GitHub Stars](https://img.shields.io/github/stars/criskkky/sws2-adminesp?style=social)](https://github.com/criskkky/sws2-adminesp/stargazers)<br/>
  <sub>Made with ❤️ by <a href="https://github.com/criskkky" rel="noopener noreferrer" target="_blank">criskkky</a></sub>
  <br/>
</div>

## Overview

AdminESP is a plugin for SwiftlyS2 that provides ESP (Extra Sensory Perception) functionality for admins in Counter-Strike 2. It allows administrators to see glowing outlines around players, making it easier to monitor the game. The plugin includes permission-based visibility controls to prevent abuse, with team-based colors and real-time updates.

## Download Shortcuts
<ul>
  <li>
    <code>📦</code>
    <strong>&nbspDownload Latest Plugin Version</strong> ⇢
    <a href="https://github.com/criskkky/sws2-adminesp/releases/latest" target="_blank" rel="noopener noreferrer">Click Here</a>
  </li>
  <li>
    <code>⚙️</code>
    <strong>&nbspDownload Latest SwiftlyS2 Version</strong> ⇢
    <a href="https://github.com/swiftly-solution/swiftlys2/releases/latest" target="_blank" rel="noopener noreferrer">Click Here</a>
  </li>
</ul>

## Features
- **ESP Toggle**: Admins can toggle ESP on/off using the `!esp` or `!wh` command (requires permission by default).
- **Hierarchical Permissions**: Two permission levels for access and visibility:
  - `adminesp.full`: Full access - can use the command and see ESP at all times.
  - `adminesp.limited`: Limited access - can use the command but only see ESP when spectating or dead.
- **Team-Based Colors**: Automatic coloring based on player teams (red for terrorists, blue for counter-terrorists).
- **Real-time Updates**: ESP visibility updates on player events like death, spawn, team change.
- **Debug Mode**: Optional debug logging for troubleshooting.
- **Multi-language Support**: English and Spanish translations included.

## Screenshots

![Screenshot 1](assets/Screenshot_1.png)

![Screenshot 2](assets/Screenshot_2.png)

## Plugin Setup
> [!WARNING]
> Make sure you **have installed SwiftlyS2 Framework** before proceeding.

1. Download and extract the latest plugin version into your `swiftlys2/plugins` folder.
2. Perform an initial run in order to allow file generation.
3. Generated file will be located at: `swiftlys2/configs/plugins/AdminESP/config.jsonc`
4. Edit the configuration file as needed.
5. Configure permissions in your server's `permissions.jsonc` file.
6. Enjoy!

## Configuration Guide

| Option | Type | Example | Description |
|--------|------|---------|-------------|
| DebugMode | boolean | `false` | Set to `true` to enable debug logging in the console (recommended: `false`). |
| EnableAuditLog | boolean | `true` | Set to `true` to log to the server console when an admin toggles ESP (default: true). |
| FullPermission | string | `"adminesp.full"` | Custom permission string for full ESP access (can be existing or new). Default: `"adminesp.full"`. Leave empty to remove permission requirement (grants full access to all players). |
| LimitedPermission | string | `"adminesp.limited"` | Custom permission string for limited ESP access (can be existing or new). Default: `"adminesp.limited"`. Leave empty to remove permission requirement (grants limited access to all players).

## Permissions

AdminESP uses a hierarchical permission system to control access and visibility:

- If `FullPermission` is empty, all players have full ESP access.
- If `LimitedPermission` is empty, all players have limited ESP access.
- If both permissions are set and not empty, only players with the corresponding permissions will have access.

`FullPermission` always takes priority over `LimitedPermission`.

For more details on configuring permissions, see the [SwiftlyS2 permissions documentation](https://swiftlys2.net/docs/development/permissions/#configuration) and make sure to update your server's `permissions.jsonc` file.

## Backend Logic (How It Works)
1. When a player uses ESP command, the plugin creates glowing entities that fit players silhouettes.
2. Glow colors are determined by team: red for terrorists, blue for counter-terrorists.
3. Visibility is controlled by transmit states based on viewer permissions and status.
4. The plugin hooks into game events (spawn, death, team change) to update glows dynamically.
5. Permission checks ensure only authorized players can use and see ESP appropriately.

## Support and Feedback
Feel free to [open an issue](https://github.com/criskkky/sws2-adminesp/issues/new/choose) for any bugs or feature requests. If it's all working fine, consider starring the repository to show your support!

## Contribution Guidelines
Contributions are welcome only if they align with the plugin's purpose. For major changes, please open an issue first to discuss what you would like to change.
