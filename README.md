# SimpleStacking

A mod for Planet Crafter that adds inventory stacking for player inventory, chests and some extractors.

## Building from Source

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) (if not already installed)

2. Create a `libs` folder next to the `.csproj` file and copy the following DLLs into it:

From `The Planet Crafter_Data/Managed/`:
- `Assembly-CSharp.dll`
- `UnityEngine.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.UI.dll`
- `UnityEngine.UIModule.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.TextRenderingModule.dll`
- `UnityEngine.InputLegacyModule.dll`
- `UnityEngine.PhysicsModule.dll`
- `Unity.Netcode.Runtime.dll`

From `BepInEx/core/`:
- `BepInEx.dll`
- `0Harmony.dll`

3. Open a command prompt in the project folder and run:
dotnet build -c Release

The completed file will appear at: bin/Release/SimpleStacking.dll

## Installation

1. Download the completed mod (or build it from source)
2. Copy the SimpleStacking.dll file to the BepInEx/plugins/ folder in the game
3. Launch the game

## Requirements

- BepInEx installed
- Planet Crafter (current version)

## License

MIT
