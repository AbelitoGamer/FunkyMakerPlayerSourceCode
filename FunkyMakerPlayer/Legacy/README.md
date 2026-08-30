# Legacy Code

This folder contains code that is no longer actively maintained or used in FunkyMakerPlayer.

## Contents

### Psych Engine Support (Deprecated)
- `PsychEngineModLoader.cs` - Loader for Psych Engine mods
- `PsychModels.cs` - Data models for Psych Engine charts

**Status**: Removed from main codebase as of version 0.2.0

**Reason**: FunkyMakerPlayer is now focused exclusively on Funky Maker (.FNMM) format support. Psych Engine support added complexity and maintenance burden without significant user benefit.

**Note**: These files are kept for reference only and are excluded from compilation. They may be removed entirely in future versions.

## Restoration

If you need to restore Psych Engine support:
1. Move files from `Legacy/` back to `FunkyMakerPlayer/`
2. Remove the `<Compile Remove="Legacy\**" />` exclusion from `FunkyMakerPlayer.csproj`
3. Restore the Psych Engine integration code in `Game1.cs` (check git history)

However, this is **not recommended** as the code may be outdated and incompatible with current FunkyMakerPlayer architecture.
