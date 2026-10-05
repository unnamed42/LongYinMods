# MelonMCP

A [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) server that runs inside Unity games via [MelonLoader](https://github.com/LavaGang/MelonLoader), enabling powerful runtime inspection, manipulation, and automation of Unity games.

## Overview

MelonMCP bridges the gap between external tools and running Unity games by exposing a comprehensive set of operations through the MCP protocol. It allows you to:

- **Inspect** game objects, components, materials, and scene hierarchies in real-time
- **Execute** C# code directly in the game's runtime environment
- **Manipulate** transforms, properties, and behaviors programmatically
- **Query** type information, assemblies, and game state
- **Automate** game interactions through a standardized protocol

Works with both **Mono** and **IL2CPP** Unity games.

## Features

### Runtime Code Execution
- Execute arbitrary C# code in the game context
- Evaluate expressions and access any game API
- Support for multi-statement code blocks with variables
- Generic method invocation (`GetComponent<T>()`, `FindObjectsOfType<T>()`)

### Scene Inspection
- List all GameObjects in the scene hierarchy
- Find objects by name, path, or instance ID
- Inspect components attached to any GameObject
- View detailed component properties and fields

### Object Manipulation
- Create primitive GameObjects (Cube, Sphere, etc.)
- Clone/instantiate existing objects
- Modify transform (position, rotation, scale)
- Enable/disable behaviours
- Set properties and invoke methods
- Destroy objects and components

### Material & Rendering
- Inspect materials on renderers
- View shader properties, textures, and colors
- Access render queue and shader keywords

### Type System Exploration
- List all loaded assemblies
- Browse types within assemblies
- Get detailed type information (methods, properties, fields)
- Filter by namespace, base type, or type kind

### Game State Control
- Pause/unpause via time scale manipulation
- Cursor visibility and lock state control
- Scene loading
- Screenshot capture

### Logging & Diagnostics
- Capture game logs (messages, warnings, errors)
- Real-time log streaming
- Filtered log queries

### Knowledge Persistence
- Store discoveries about game APIs and commands
- Query stored knowledge across sessions
- Categorize findings (console commands, static accessors, cheats, etc.)

### Source Code Integration
- Set path to decompiled/pseudocode source
- Search through game source code
- Read specific source files

## Installation

### Prerequisites
- [MelonLoader](https://github.com/LavaGang/MelonLoader) installed on your Unity game
- .NET 6.0 runtime
- Any MCP client that speaks the Streamable HTTP transport (no Python or bridge script required)

### Steps

1. **Build the mod** (or download from releases):
   ```bash
   cd MelonMCP
   dotnet build -c Release
   ```

2. **Copy to game**:
   ```bash
   cp MelonMCP/bin/Release/MelonMCP.dll <GamePath>/Mods/
   ```

3. **Configure MCP client** (see Configuration section)

4. **Launch the game** - MelonMCP will start automatically on port 27015

## Configuration

### Streaming HTTP Setup

MelonMCP speaks the MCP **Streamable HTTP** transport on `http://127.0.0.1:27015/mcp`.
**No bridge script is needed** - point the client at a URL:

```json
{
  "mcpServers": {
    "melonmcp": {
      "type": "streamable-http",
      "url": "http://127.0.0.1:27015/mcp"
    }
  }
}
```

> The previous TCP socket + `mcp-bridge.*` scripts have been **removed**. A raw TCP listener can
> never be connected to directly: the stdio binding requires the *client* to spawn the server
> process, and this server lives inside the game process - so an intermediate translator was
> always required. The protocol's HTTP transport removes that layer entirely.

### Custom Port

Set the `MELONMCP_PORT` environment variable or modify the default in the source.

## Available Tools

| Tool | Description |
|------|-------------|
| `read_logs` | Read recent game log messages |
| `clear_logs` | Clear the log buffer |
| `execute_csharp` | Execute C# code at runtime |
| `evaluate_expression` | Evaluate a simple C# expression |
| `get_scene_info` | Get current scene information |
| `list_game_objects` | List GameObjects in scene hierarchy |
| `find_game_object` | Find a GameObject by path or name |
| `list_components` | List components on a GameObject |
| `inspect_component` | Inspect component properties and methods |
| `toggle_behaviour` | Enable/disable a MonoBehaviour |
| `set_property` | Set a property or field value |
| `invoke_method` | Invoke a method on an object |
| `get_game_info` | Get game and Unity version info |
| `take_screenshot` | Capture a screenshot |
| `get_time_info` | Get Unity time information |
| `list_assemblies` | List loaded assemblies |
| `list_types` | List types in an assembly |
| `get_type_info` | Get detailed type information |
| `find_objects_of_type` | Find all objects of a specific type |
| `set_time_scale` | Set Unity time scale (pause/slow-mo) |
| `cursor_control` | Control cursor visibility and lock |
| `load_scene` | Load a Unity scene |
| `instantiate_object` | Clone a GameObject |
| `create_primitive` | Create a primitive GameObject |
| `set_transform` | Set position/rotation/scale |
| `inspect_material` | Inspect materials on a renderer |
| `destroy_object` | Destroy a GameObject or component |
| `add_game_knowledge` | Store a discovery in the knowledge base |
| `get_game_knowledge` | Query the knowledge base |
| `get_game_summary` | Get summary of stored knowledge |
| `set_pseudocode_path` | Set path to decompiled source |
| `search_pseudocode` | Search decompiled source code |
| `read_pseudocode_file` | Read a decompiled source file |

## Usage Examples

### Execute C# Code
```json
{
  "tool": "execute_csharp",
  "arguments": {
    "code": "var player = GameObject.Find(\"Player\"); return player.transform.position.ToString();"
  }
}
```

### Find All Cameras
```json
{
  "tool": "find_objects_of_type",
  "arguments": {
    "typeName": "Camera",
    "limit": 10
  }
}
```

### Inspect a Component
```json
{
  "tool": "inspect_component",
  "arguments": {
    "gameObjectPath": "Player",
    "componentType": "CharacterController"
  }
}
```

### Create a Debug Cube
```json
{
  "tool": "create_primitive",
  "arguments": {
    "type": "Cube",
    "name": "DebugMarker",
    "position": "10, 5, 0",
    "scale": "0.5, 0.5, 0.5"
  }
}
```

### Pause the Game
```json
{
  "tool": "set_time_scale",
  "arguments": {
    "timeScale": 0
  }
}
```

## Technical Details

### Architecture

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│   MCP Client    │────▶│   MCP Bridge    │────▶│   MelonMCP      │
│  (Any MCP app)  │ TCP │   (Python)      │ TCP │   (In-Game)     │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                                                        │
                                                        ▼
                                                ┌─────────────────┐
                                                │  Unity Runtime  │
                                                │  (Game Process) │
                                                └─────────────────┘
```

### Threading Model

MelonMCP handles threading carefully:
- MCP server runs on a background thread for responsiveness
- Unity API calls are marshalled to the main thread via a dispatcher
- Results are safely returned to the calling thread

### IL2CPP Support

Full support for IL2CPP games including:
- Runtime type resolution across IL2CPP assemblies
- Generic method invocation via reflection
- Proper handling of IL2CPP wrapped types
- Component enumeration fallbacks

### Focus/Pause Handling

MelonMCP includes Harmony patches to keep games running when unfocused:
- Patches `Application.runInBackground` to stay true
- Bypasses `GameManager.OnApplicationFocus` pause logic (for supported games)
- Allows continuous operation while switching between windows

## Building from Source

```bash
# Clone the repository
git clone https://github.com/yourusername/MelonMCP.git
cd MelonMCP

# Restore dependencies (requires MelonLoader libs)
# Place MelonLoader references in a 'libs' folder or update the .csproj

# Build
dotnet build -c Release
```

## Dependencies

- MelonLoader 0.6.0+
- Newtonsoft.Json
- HarmonyX (included with MelonLoader)

## Security Considerations

MelonMCP provides powerful capabilities including arbitrary code execution. By default, it only listens on localhost. Be cautious when:
- Using in multiplayer games (anti-cheat systems may flag this)
- Exposing the port to non-localhost addresses
- Running untrusted code through the execute_csharp tool

## Troubleshooting

### Server not starting
- Check the MelonLoader console for error messages
- Ensure port 27015 is not in use by another application
- Verify the DLL is in the correct folder

### Connection refused
- Make sure the game is running with MelonMCP loaded
- Check if a firewall is blocking the connection
- Verify you're connecting to the correct port

### Tools timing out
- The game window may need to be focused
- Some games pause when unfocused; MelonMCP patches this but it may not work for all games
- Ensure the game is fully loaded (not in loading screen)

### Unity objects not found
- Scene may not be fully loaded yet
- GameObject path may be incorrect (use `list_game_objects` first)
- Object may be inactive (use `activeOnly: false`)

## Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

## License

MIT License - See [LICENSE](LICENSE) for details.

## Acknowledgments

- [MelonLoader](https://github.com/LavaGang/MelonLoader) - The mod loader that makes this possible
- [Model Context Protocol](https://modelcontextprotocol.io/) - The protocol specification
- [HarmonyX](https://github.com/BepInEx/HarmonyX) - Runtime patching library
