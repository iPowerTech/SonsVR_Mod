# Sons of the Forest VR Mod

VR mod for Sons of the Forest using OpenVR/SteamVR.

**Status**: Early alpha - expect crashes and performance issues.

## Requirements

- Sons of the Forest (Steam)
- SteamVR-compatible headset
- [MelonLoader](https://melonwiki.xyz/) v0.6.0+
- .NET 6+

## Installation

1. Install MelonLoader to your Sons of the Forest directory
2. Copy `SonsVR_Mod.dll` to `SonsOfTheForest/Mods/`
3. Copy `openvr_api.dll` and `SonsSteamVR_IL2CPP.dll` to `SonsOfTheForest/UserLibs/`
4. Launch game through SteamVR

## Controls

| Action | VR Input |
|--------|----------|
| Move | Left stick |
| Sprint | Right stick up |
| Turn | Right stick left/right (30° snap) |
| Crouch | Right stick down |
| Jump | Raise both controllers |
| Interact | Grip (either hand) |
| Inventory | Y button (left) |
| Lighter | B button (right) |
| Reload | A button (right) |
| Primary action | Right trigger |
| Secondary action | Left trigger |
| Melee | Swing right controller |

## Known Issues

- Performance problems and crashes
- UI renders to screen instead of VR space
- No hand/weapon tracking
- Camera breaks in vehicles and cutscenes
- All settings hardcoded

## License

GPL-2.0 - Copyright (C) 2025 Antonio Mauriello

Sons of the Forest is a trademark of Endnight Games Ltd. This mod is not affiliated with Endnight Games or Valve Corporation.
