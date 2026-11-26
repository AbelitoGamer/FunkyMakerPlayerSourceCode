# FunkyMakerPlayer — Project Context

> **Maintenance contract**
>
> **Anyone who changes this project must update this file in the same change.**
> Update the relevant section when you change: architecture/state flow, file or
> directory layout, FNMM/mod/asset formats, controls, settings, platform
> behavior, build/publish configuration, or known caveats. If you discover a
> mismatch between code and docs, fix the docs or the code — never leave both
> stale. When in doubt, add a short note rather than silently dropping detail.
>
> This document is intentionally source-of-truth style: prefer it over guessing
> from memory. Verify command output/paths before copying claims into it.

---

## 1. What this project is

FunkyMakerPlayer is a **MonoGame level player** for levels made with
FunkyMaker Mobile. It loads `.FNMM` archives (ZIP) and older `.rar` exports,
plays the chart, renders the scene/events, and supports mods, a chart editor,
multiplayer, and desktop (Windows/Linux) + Android builds.

- Current version: **0.5.0** (`FunkyMakerPlayer/version.txt`, csproj `<Version>` /
  `<AssemblyVersion>` / Android `ApplicationDisplayVersion` (+ version code 8; upstream
  used 7 for 0.4.9)). The
  updater compares the assembly version with the latest GitHub release, so bump
  all of them together.
- Fork: `origin` = `AbelitoGamer/FunkyMakerPlayerSourceCode`, `upstream` =
  `Thisisfenix/FunkyMakerPlayerSourceCode`. After syncing with upstream, re-check
  this document against the code (last full re-sync: upstream **0.4.9**, 2026-11-18:
  fork-fixes rebased onto upstream/master, fork master fast-forwarded to it).
  2026-11-19: fork-fixes fast-forwarded to upstream `5d0f9e0` ("mas arreglos":
  menu selections only applied on confirm via `SelectionConfirmed`,
  `ApplyNoteSplashStyle()`, miss-tint timer in `SpriteAnimator`, default mods also
  copied to `Assets/DefaultMods/`); no conflicts.
  2026-11-22: the fork's uncommitted work was committed on fork-fixes in 12 feature commits, then
  upstream `20a4360` merged (merge `5c3d045`): SharpCompress 0.48.1 (`ArchiveFactory.OpenArchive`),
  SignalR 10.0.15, `System.Windows.Forms` package removed, `**/mods/` no longer gitignored,
  `mods/notestyles/README.txt`, `FunkyMakerServerRepo` → `a066122`. Upstream's private-room fix
  (`CreatePrivateRoom` / `CheckPrivateRoom` / `LeavePrivateRoom` for the old 6-char code flow) was
  **not** taken: conflicts in `MultiplayerMenu.cs` / `NetworkManager.cs` kept the fork's room system.
  Fork `master` was not moved (still `5d0f9e0`).
- Main project: `FunkyMakerPlayer/FunkyMakerPlayer.csproj`
- Target frameworks: `net10.0;net10.0-android`
- Tested SDK at time of writing: **.NET SDK 10.0.404** (`dotnet --version`).
- Rendering: MonoGame 3.8 (`DesktopGL` on desktop, `Android` on Android).
- Game world coordinates are designed against a 2160×1920 landscape /
  1920×2160 portrait "room"; gameplay is laid out in a logical 1280×720
  (landscape) or 720×1280 (portrait) space, rendered (fork) at the window's real
  resolution by default (§5.4), and letterboxed.

---

## 2. Quick start

Run from the repository root (`C:\Users\abeli\Documents\FunkyMakerPlayerSourceCode`).

**Easiest: `.\build` (fork, 2026-11-22).** `build.cmd` -> `build.ps1` (repo root) asks step by
step: platform (win-x64 / linux-x64 / Android 64-bit / Android arm64 only / Android 32-bit Mono),
Debug/Release, test mode (`FunkyTestBuild`), signing (Android Release only: official keystore,
prompts for the passwords if `FUNKY_STORE_PASS`/`FUNKY_KEY_PASS` aren't set and clears them
afterwards; otherwise `AndroidKeyStore=false`), and zip (desktop). It shows the `dotnet publish`
command, asks to confirm, publishes to `Builds\FunkyMakerPlayer-<ver>-<platform>-<config>[-test]
[-signed|-unsigned]\` (deleted first) and copies the APK to `Builds\<same name>.apk`. Every
question can be passed as a parameter (`-Platform -Configuration -Test yes|no -Signed yes|no
-Zip yes|no -Yes`) for non-interactive builds. Verified: win-x64 Release + zip, android-arm64
Release test unsigned (26 MB). The manual commands it wraps:

```powershell
# Build desktop Debug (net10.0 only; project is multi-targeted)
dotnet build FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Debug -f net10.0 /p:TargetFrameworks=net10.0

# Run desktop Debug
dotnet run --project FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0 /p:TargetFrameworks=net10.0

# Publish self-contained single-file desktop builds
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Release -r win-x64 -f net10.0 /p:TargetFrameworks=net10.0
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Release -r linux-x64 -f net10.0 /p:TargetFrameworks=net10.0

# Build/publish Android (Release requires signing secrets)
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0-android -c Release

# Android test APK without the signing secrets (debug key, arm64 only, ~26 MB)
# (dev machine since 2026-11-19: Android workload installed; SDK in %LOCALAPPDATA%\Android\Sdk,
#  JDK in %LOCALAPPDATA%\Android\jdk, both installed via -t:InstallAndroidDependencies)
dotnet build FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0-android -c Release -p:RuntimeIdentifiers=android-arm64 -p:AndroidSupportedAbis=arm64-v8a -p:AndroidKeyStore=false "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Android\jdk"

# Android 32-bit (armeabi-v7a) APK (verified 2026-11-20). CoreCLR has NO android-arm runtime
# (NETSDK1181 "Microsoft.Android.Runtime.CoreCLR.36.android-arm"), so it must use Mono.
# /p:TargetFrameworks=net10.0-android is required, otherwise UseMonoRuntime=true also hits the
# desktop target and restore fails (NU1102 Microsoft.NETCore.App.Runtime.Mono.win-x64).
# PublishTrimmed=false because Mono Release trims by default (CoreCLR build is untrimmed).
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0-android /p:TargetFrameworks=net10.0-android -c Release -p:UseMonoRuntime=true -p:PublishTrimmed=false -p:RuntimeIdentifiers=android-arm -p:AndroidKeyStore=false "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Android\jdk" -o FunkyMakerPlayer\bin\Release\android-arm32-publish
```

Notes:
- A debug-signed APK can't install over a release-signed one (signature mismatch): uninstall first.
- The project multi-targets `net10.0` and `net10.0-android`. On a machine without
  the .NET Android workload installed, plain `-f net10.0` still fails with
  `NETSDK1147` because restore evaluates the Android target. Passing
  `/p:TargetFrameworks=net10.0` (or running `dotnet workload restore` first)
  restricts desktop builds to `net10.0`. The commands above were verified with
  `/p:TargetFrameworks=net10.0`.
- The root `FunkyMakerPlayer.sln` and the project-local
  `FunkyMakerPlayer\FunkyMakerPlayer.sln` both contain only the game project.
  Upstream has re-added the missing `_inspect_tool` reference before; if a sync
  brings it back, remove it again (see §18.1).
- Put custom levels in `%AppData%\FunkyMakerPlayer\mods\levels\` (the default), or
  in `mods\levels\` next to the executable after Settings → AUDIO & VIDEO → Move Mods
  Folder → Next to game (§13). Freeplay
  scans `.fnmm`, `.zip`, and `.rar` recursively (case-insensitive), skips any
  `Backups\` folder, and also lists top-level folders containing `meta.json` or
  `data.json` (unpacked levels).

---

## 3. Repository layout

```
/                                repo root
├── Context.md                   this document (gitignored, local only)
├── FunkyMakerPlayer.sln         root solution (game project only)
├── FunkyMakerPlayer/            main game project
│   ├── FunkyMakerPlayer.csproj  build/package/source config (read this first)
│   ├── FunkyMakerPlayer.sln     working single-project solution
│   ├── Game1.cs                 central game class: fields, Initialize / LoadContent,
│   │                            UpdateInternal (menu dispatch + gameplay), DrawInternal
│   │                            (~3,800 lines; split into partials on 2026-11-21, see below)
│   ├── Game1.LevelLoad.cs       partial: StartLoadingLevel, Reset/Finalize, SetupLevel, icons, textures
│   ├── Game1.SongClock.cs       partial: PauseSong / ResumeSong / SeekSong / RestartSong,
│   │                            music ↔ clock sync (KeepMusicInSync; MusicPosition.cs)
│   ├── Game1.Editors.cs         partial: Editor Hub, Level Selector, Chart Editor, playtests
│   ├── Game1.Import.cs          partial: drag-and-drop + copying levels into mods/levels
│   ├── Game1.Window.cs          partial: FPS cap, fullscreen, render target, letterbox, present
│   ├── Game1.Discord.cs         partial: Rich Presence + display names
│   ├── Game1.SettingsMenu.cs    partial: in-game settings overlay results
│   ├── Game1.Android.cs         partial (Android): touch guard, APK assets, SAF / mods picker
│   ├── Game1.Touch.cs           partial (Android): back gesture, shared touch pointer, menu-button auto-hide (§13)
│   ├── Game1.Osu.cs             partial: osu! background, .osz / .osk import, replay lookup (§14.2)
│   ├── Osu/                     osu! support (fork, §14.2): OsuBeatmap (parser), OsuLibrary
│   │                            (mods/osu, import), OsuLevelLoader (.osu → FnmmLevel + extras),
│   │                            OsuManiaSkin (.osk / beatmap skin → NoteStyle), OsuHitsounds,
│   │                            OsuStoryboard, OsuVideo (+ FfmpegVideoStream; Android:
│   │                            Platforms/Android/MediaCodecVideoStream), OsuSettings
│   ├── ScrollTimeline.cs        (fork) variable scroll speed (osu! SV), used by NoteRenderer.Scroll
│   ├── AudioDecoder.cs          (fork) song audio: OGG (OggLoader) or MP3 (NLayer) → PCM
│   ├── FreeplayMenu.Osu.cs      (fork) Freeplay OSU!MANIA tab, difficulty selector (§10.1)
│   ├── Game1.UiSfx.cs           partial: UI sounds from screen-state diffs (§10.6)
│   ├── Program.cs               entry point + crash handling (updater is NOT
│   │                            run here any more; see §16)
│   ├── DefaultMods.cs           ships/installs the default mods (§14.1)
│   ├── ModsStorage.cs           desktop mods folder location + move (fork, §13)
│   ├── LevelFiles.cs            shared level import / delete (fork, §10.1, §12.2)
│   ├── Game1.Replay.cs          partial: replay recording / playback / online exchange (§12.4)
│   ├── Game1.Online.cs          partial: online room matches (sides, spectating, score bar) (§12.3)
│   ├── Replay/ReplayData.cs     replay format, ReplayStore (files), ReplayRecorder (§12.4)
│   ├── FmmNoteSystem.cs         FMM-faithful note system (skin.json "noteSystem": "fmm", §14)
│   ├── FnmmModels.cs            canonical FNMM JSON schema + FnmmLevel
│   ├── FnmmLoader.cs            ZIP/RAR/folder loading, nested/flat layout,
│   │                            inner-archive + SharpCompress fallback
│   ├── FnmmSceneRenderer.cs     shared texture loader, animator factory and
│   │                            ordered scene drawer (gameplay + Chart Editor
│   │                            stage preview)
│   ├── FnmmConverter.cs         legacy-format in-memory conversion
│   ├── FnmmSaver.cs             ZIP writer used by Chart Editor
│   ├── FnmmTimeConverter.cs     beats vs. seconds rules
│   ├── NoteRenderer.cs          note physics, judgement, holds, autoplay,
│   │                            character animation hooks, multiplayer visuals
│   ├── EventProcessor.cs        scene events / action packs / camera / HUD
│   ├── NoteStyle.cs             note style loading (PNG + XML atlas)
│   ├── NoteSkins.cs             skin constants (visual only) + action-pack
│   │                            health analysis / ShouldAvoid (fork)
│   ├── NoteSplashStyle.cs       splash effect loading (PNG + XML atlas)
│   ├── ParticleNoteSplash.cs    MonoGame.Extended particle splash
│   ├── AtlasPacker.cs           packs wide strips into GPU-safe atlases
│   ├── SpriteAnimator.cs        per-object animation state machine
│   ├── BgSpriteAnimator.cs      type-2 background tiling animation
│   ├── ArrowEffectManager.cs    receptor visual effects
│   ├── Settings.cs              persisted global settings (Settings.json)
│   ├── Controls.cs              (fork) rebindable hotkeys: HotkeyAction, KeyBinding, matching, save
│   ├── LayoutKeyboard.cs        (fork) keyboard state incl. non-ASCII layout keys (Ñ…) via SDL
│   ├── ChartDifficulty.cs       (fork) osu!mania star rating port (also at a song rate)
│   ├── FreeplayMenu.Difficulty.cs (fork) background level analysis: stars (both sides, any
│   │                            rate), BPM, notes, peak NPS, density graph, first note
│   ├── FreeplayMenu.Browse.cs   (fork) search / sort / favourites / random, panel input,
│   │                            "..." options, modifiers / attempts windows, confirm (§10.1)
│   ├── FreeplayMenu.LevelPanel.cs (fork) right panel drawing (toolbar, curved list, options menu)
│   ├── FreeplayMenu.Details.cs  (fork) selected-level details on the left (§10.1)
│   ├── FreeplayMenu.Preview.cs  (fork) streamed audio preview of the selected level
│   ├── FreeplayMenu.Icons.cs    (fork) per-level health icons (lazy) + initials badges
│   ├── FreeplayData.cs          (fork) freeplay_data.json: favourites, plays, attempts, modifiers
│   ├── PlayModifiers.cs         (fork) modifier registry + categories (§8.7)
│   ├── Game1.Freeplay.cs        (fork) partial: song rate / enemy mode in gameplay, saving attempts
│   ├── UI/UiDraw.cs             (fork) shared drawing helpers for direct-to-window menus
│   ├── UI/OptionsMenu.cs        (fork) reusable "..." options menu with confirmations (§10.0)
│   ├── UI/ModifiersWindow.cs    (fork) modifiers window (categories), openable from any screen
│   ├── UI/AttemptsWindow.cs     (fork) all attempts of a level + their replays
│   ├── UI/PointerInput.cs       (fork) menus' pointer: the mouse, or touches as a mouse on Android (§13)
│   ├── UI/PickerTouch.cs        (fork) taps + BACK/APPLY + scaling for the 4 Settings style pickers
│   ├── PlatformPaths.cs         mods/levels/assets paths + Android SAF import
│   ├── PlatformShare.cs         reveal file in file manager + clipboard (SDL2) / Android share sheet
│   ├── FreeplayMenu.cs          main menu + level panel + import modal +
│   │                            in-menu update prompt + credits
│   ├── MainSettingsMenu.cs      "HUD EDITOR" launcher screen (explains the
│   │                            editor, ENTER opens it)
│   ├── PauseMenu.cs             pause/restart/settings/exit menu
│   ├── HudEditor.cs             drag-and-drop HUD layout editor
│   ├── HudSettings.cs           persisted HUD layout
│   ├── LoadingScreen.cs         cat loading/ready/clearing screen
│   ├── SongTransition.cs        end-of-song AnkushCat cascade
│   ├── TouchControls.cs         Android virtual buttons
│   ├── Updater.cs               GitHub Releases self-update (desktop)
│   ├── DiscordRpc.cs            Rich Presence (desktop only)
│   ├── CrashLogger.cs           unhandled exception logging
│   ├── OggLoader.cs             OGG → SoundEffect + PCM for seeking
│   ├── KeyBindingOverlay.cs     key rebinding UI
│   ├── ModsHelpScreen.cs        Android mod-folder help overlay
│   ├── Managers/
│   │   ├── SettingsManager.cs   data-driven tabbed settings UI/logic (§10.3)
│   │   ├── CameraManager.cs     camera matrix, beat zoom, shake
│   │   ├── TweenManager.cs      transform tween helper
│   │   ├── ParticleManager.cs   particle helper
│   │   └── AudioManager.cs      DISABLED (commented out in Game1)
│   ├── UI/
│   │   ├── HealthBar.cs         FNF-style health bar + icons
│   │   ├── TimerBar.cs          song timer presets
│   │   ├── JudgementOverlay.cs  rating popups + combo digits
│   │   ├── CountdownMenu.cs     countdown pack selector
│   │   ├── CountdownOverlay.cs  ready/set/go playback
│   │   ├── ArrowsMenu.cs        note-style selector
│   │   ├── NoteSplashMenu.cs    splash-style selector
│   │   ├── FontMenu.cs          UI font selector
│   │   ├── GameOverOverlay.cs   death screen (RETRY / MENU, no auto-restart)
│   │   ├── GameplayHudOverlay.cs in-game HUD drawing (health, stats, timer,
│   │   │                        song info, debug, FPS, flashes) + GameplayStats
│   │   ├── ErrorOverlay.cs      load-error + "drop a level" screens, ASCII-safe
│   │   │                        text wrapping
│   │   ├── UiInterfaces.cs      IDirectScreen / IScreenOverlay
│   │   ├── UiScale.cs           resolution-proportional scaling (1280×720 = 1.0)
│   │   ├── DarkModeHelper.cs    Windows dark title bar via SDL → HWND
│   │   ├── UiSfx.cs             UI sound effects: UiSound enum, queue, loading (§10.6)
│   │   └── Multiplayer/UI/...   (under Multiplayer)
│   ├── Multiplayer/
│   │   ├── NetworkManager.cs    online hub client singleton (reconnect/backoff, events on game thread, §12.1)
│   │   ├── WsHubConnection.cs   WebSocket JSON hub transport (protocol 4, replaces SignalR, §12.1)
│   │   ├── Rooms/RoomProtocol.cs room protocol wire types (keep in sync with the server's worker/src/protocol.ts)
│   │   ├── Rooms/RoomClient.cs  room state, chat, round level, match events (§12.1)
│   │   ├── UserDirectoryClient.cs PlayerId → username cache (REST + packets)
│   │   ├── NetworkPackets.cs    ping, official level list, leaderboard / history DTOs
│   │   ├── MultiplayerMenu.cs   online menu core + settings + leaderboard (partial, §12.2)
│   │   ├── MultiplayerMenu.MatchHistory.cs  leaderboard RECENT MATCHES cards + match detail popup (§12.2)
│   │   ├── MultiplayerMenu.Rooms.cs / .RoomBrowser.cs / .RoomSettings.cs / .WaitingRoom.cs /
│   │   │   .SongSelect.cs      room screens (§12.2)
│   │   ├── MultiplayerMenu.LevelFiles.cs    LOCAL list auto-refresh / import / delete
│   │   ├── MultiplayerMenu.LocalRows.cs     song select LOCAL rows: sections, holder squares
│   │   ├── MultiplayerSettings.cs persisted MP settings (server URL, profiles, §12.0)
│   │   ├── TimeSyncManager.cs
│   │   ├── LevelHasher.cs       SHA-256 level verification (file or bytes)
│   │   ├── LevelCache.cs        RAM + SHA-256-verified disk cache of
│   │   │                        official levels (`official_cache/*.fmc`)
│   │   ├── LevelDownloader.cs   official level download (128 KB chunks,
│   │   │                        restores from verified cache first)
│   │   ├── Leaderboard/         leaderboard + match history (merged with server)
│   │   └── Transfer/            LevelTransferClient.cs (HTTP level up/download, replay
│   │                            tracks), LocalLevelIndex.cs (hash cache) (§12.1)
│   ├── Editors/
│   │   ├── EditorPicker.cs      "Editor Hub": New Chart / Chart Editor / HUD Editor
│   │   ├── EditorType.cs        NewChart, Chart, Hud (+ unused placeholders:
│   │   │                        Character/Stage/Alphabet/Sprite/Event/Week/Dialogue)
│   │   ├── IEditor.cs           IDirectScreen + Update/BackPressed/IsModified
│   │   ├── Chart/
│   │   │   ├── ChartEditor.cs   redesigned chart editor (§11)
│   │   │   ├── ChartTimeline.cs
│   │   │   ├── NoteGrid.cs
│   │   │   ├── EditorAudioManager.cs
│   │   │   ├── LevelSelector.cs level picker + "new chart" creator (BPM, .ogg)
│   │   │   └── UndoManager.cs
│   │   └── Common/              BackupManager, FileValidator
│   ├── Platforms/Android/       MainActivity, CrashDialog, ModsDocumentProvider, LevelShareProvider, AndroidMemoryBudget
│   ├── Content/                 DefaultFont.spritefont, MilkyNice.otf
│   ├── Icon.ico / Icon.bmp      embedded + copied next to exe
│   ├── app.manifest             Windows DPI/game-mode manifest
│   ├── version.txt              copied next to exe; updater reads it
│   ├── obfuscar.xml             Obfuscar config (obfuscation is disabled)
│   └── Legacy/                  EXCLUDED from compilation
├── Assets/                      bundled runtime assets, tracked in git since the
│                                 upstream "Assets Updater" commit (0.4.7 → 0.4.8);
│                                 copied to output, AndroidAssets on Android
│   ├── data/notestyles/         funkin.json, pixel.json style configs
│   ├── images/                  UI art, popup fonts, folder icon,
│   │                            NOTE_hold_FMM.png (hold fallback for skins without one)
│   │   └── ui/multiplayer/      multiplayer menu icons (back, connect, create,
│   │                            join, leaderboard, lock, quickplay, settings)
│   ├── LoadingScreen/           AnkushCat.png
│   ├── sounds/ui/               UI sound effects (*.wav, §10.6)
│   ├── music/online/            online menu music (*.mp3, §10.7)
│   └── Mobile Control/          touch hitbox art
├── DefaultMods/                 default mods, shipped as the build's mods/ (§14.1):
│   ├── notestyles/FMM (Default)/ default note skin, FMM note system (note.png,
│   │                            good/badnote, holdtrail/holdend, skin.json)
│   ├── notestyles/FMM (XML)/    same look as an XML atlas (fmmnoteskin.png/.xml,
│   │                            skin.json); both skin.json list every option
│   ├── countdowns/Default/      default ready/set/go
│   ├── fonts/MilkyNice.otf      default UI font
│   └── notesplashes/            README + FMM (Default)/ (default splash atlas,
│                                 splash.json); code splashes are built in
├── References/                 not shipped: hud.hx (Codename recreation of the FMM HUD the
│                                 FMM note system is based on), FMM (XML) source PNGs/.pdn
├── FunkyMakerPlayer_win/        local packaged build (gitignored, not source)
├── FunkyMakerUpdater/           standalone updater source (not part of game build)
├── FunkyMakerServerRepo         gitlink (submodule pointer) with no
│                                 `.gitmodules`; empty in a normal checkout
├── build.cmd, build.ps1        interactive build wizard (§2)
├── confuser.crproj              ConfuserEx config (not run automatically)
└── FunkyMakerPlayer.crproj      second ConfuserEx config (hard-coded paths
                                  from another machine; not run automatically)
```

The utility scripts `convert_fnf_to_fmm.py` / `extract_yellow_arrows.py` and
`MULTIPLAYER_CLEANUP_PLAN.md` were removed upstream in 0.4.7.

---

## 4. Build, packaging, and dependencies

Read `FunkyMakerPlayer/FunkyMakerPlayer.csproj` before changing build behavior.

### Key project properties

| Property | Value |
|---|---|
| `OutputType` | `Exe` (`WinExe` for Windows Release) |
| `TargetFrameworks` | `net10.0;net10.0-android` |
| `RollForward` | `Major` |
| `Version` / `AssemblyVersion` | `0.4.8` |
| `Nullable` | `enable` |
| `ImplicitUsings` | `disable` |
| `PublishReadyToRun` / `TieredCompilation` | `false` |

### Package references

| Package | Version | Platform |
|---|---|---|
| `MonoGame.Extended` | 6.1.1 | all |
| `MonoGame.Framework.DesktopGL` | 3.8.* | non-Android |
| `MonoGame.Framework.Android` | 3.8.* | Android |
| `MonoGame.Content.Builder.Task` | 3.8.* | all |
| `NVorbis` | 0.10.5 | all |
| `Obfuscar` | 2.2.50 | all (`PrivateAssets=all`; not auto-run) |
| `SharpCompress` | 0.48.1 (upstream 2026-11-19; API `ArchiveFactory.OpenArchive`) | all |
| `SixLabors.ImageSharp` | 3.1.12 | all |
| `DiscordRichPresence` | 1.2.1.24 | non-Android |
| `FontStashSharp.MonoGame` | 1.3.7 | all |
| `Xamarin.AndroidX.DocumentFile` | 1.0.1.14 | Android |

`Microsoft.AspNetCore.SignalR.Client` was removed with protocol 4 (2026-11-25): the online
server is now a Cloudflare Worker and the client talks to it over a plain `ClientWebSocket`
(`Multiplayer/WsHubConnection.cs`, §12).

### Copied / embedded content

- `Assets\**\*.*` → copied to output; also included as AndroidAsset on Android.
- `DefaultMods\**\*.*` → copied to output as `mods\**`; AndroidAsset as
  `mods/**` in the APK (Link `Assets\mods\...`, prefix stripped). See §14.1.
- `Icon.ico`, `version.txt` → copied next to the executable.
- Upstream 0.4.9 **deleted** `events\hard.json` / `events\yellow_arrows.json` and their
  `EmbeddedResource` entries ("no sirven"). `CorneredEventHandler` still asks for
  `events.hard.json` via `GetManifestResourceStream`, which now returns null.
  `.gitignore` keeps un-ignoring `FunkyMakerPlayer/events/**` (harmless) and
  `DefaultMods/**`; it ignores `References/` (local reference material: hud.hx,
  FMM (XML) source images).
- `Icon.ico`, `Icon.bmp` → embedded resources.
- `Legacy\**` is excluded from `Compile`, `EmbeddedResource`, and `None`.

### Desktop publish

Release non-Android publish is **single-file, self-contained, not trimmed**:

```powershell
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Release -r win-x64 -f net10.0 /p:TargetFrameworks=net10.0
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Release -r linux-x64 -f net10.0 /p:TargetFrameworks=net10.0
```

- Output: `FunkyMakerPlayer/bin/Release/net10.0/<rid>/publish/`.
- `PublishSingleFile=true`, `SelfContained=true`, `PublishTrimmed=false`.
- `IncludeNativeLibrariesForSelfExtract=true`, `IncludeAllContentForSelfExtract=true`.
- A custom `IncludeNativeLibraries` target copies `runtimes/**/*.dll` into the
  publish folder after publish.
- Windows Release uses `WinExe` (no console), `app.manifest`, and `Icon.ico`, both for
  `publish -r win-x64` and for a plain `build -c Release` on Windows (no RID; fork fix,
  it used to stay a console app). Linux keeps `Exe`.
- **The `publish\` folder contains only `FunkyMakerPlayer.exe` (+ `.pdb`)** on a
  verified win-x64 publish. `Assets/`, `mods/` (from `DefaultMods/`),
  `version.txt`, and `Icon.ico` are bundled into the single file / included in
  the self-extract bundle, not copied as loose files. The non-publish build
  folder (`bin\Release\net10.0\<rid>\`) does contain loose `Assets/`, `Content/`,
  `mods/`, `version.txt`, `Icon.ico`, and all DLLs. On first start the
  single-file exe copies the bundled `mods/` defaults next to itself (§14.1;
  verified: publish → run → `mods
otestyles\Default\…` etc. appear).
- If you need a portable folder with loose assets instead of a single file,
  build with `-c Release /p:TargetFrameworks=net10.0` (no `-r`) or publish with
  `PublishSingleFile=false`.

### Android publish / signing

```powershell
$env:FUNKY_STORE_PASS = "..."
$env:FUNKY_KEY_PASS = "..."
dotnet publish FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0-android -c Release
```

- The `net10.0-android` target requires the .NET Android workload; install it or
  run `dotnet workload restore` before publishing.
- Release signing requires **both** environment variables; the build fails with a
  custom `CheckAndroidSigningSecrets` MSBuild error if either is missing.
- Keystore expected at `FunkyMakerPlayer\..\funkymakergame.keystore`
  (repository root), alias `funkymakergame`.
- Package id: `com.funkymakergame.player`; title `FunkyMaker Player`;
  `ApplicationVersion=8`, `AndroidVersionCode=8`, display version `0.5.0`.
- `SupportedOSPlatformVersion=29`; Release ABIs `arm64-v8a;x86_64`. A 64-bit APK will not
  install on a 32-bit-only phone (`INSTALL_FAILED_NO_MATCHING_ABIS`); 32-bit needs the separate
  Mono build in §1 (CoreCLR has no android-arm runtime). minSdk 29 = Android 10+ only.
- AOT disabled (`RunAOTCompilation=false`), Mono IL interpreter disabled (`UseInterpreter=false`; in `.NET 10`
  Android `Debug` builds with `UseMonoRuntime=true`, the SDK defaults `UseInterpreter=true`, and `mono_ee_interp`
  crashes with `SIGSEGV (SI_KERNEL)` on 32-bit worker threads when running ImageSharp / chart analysis),
  `EmbedAssembliesIntoApk=true`, `AndroidUseSharedRuntime=false`. `AndroidKeyStore` and `AndroidSupportedAbis` in
  the Release PropertyGroups now respect explicit `-p:AndroidKeyStore=false` / `-p:RuntimeIdentifiers=...`
  overrides.
- `Platforms/Android/AndroidManifest.xml` + `Platforms/Android/Resources/**`.

### Test builds (fork, 2026-11-20)

A "test build" only runs while the online server allows it, so a test version can be
given away and switched off later.

- Toggle: MSBuild property `FunkyTestBuild` (default `false` in the csproj main
  PropertyGroup; or per build `-p:FunkyTestBuild=true`) → defines `FUNKY_TEST_BUILD` →
  `TestGate.Enabled`. Normal builds never contact the server for this.
- `TestGate.cs`: on `Game1.Initialize` a background check does `GET
  {DefaultServerUrl}/api/testing` up to `MaxAttempts` = 5 times (20 s timeout each, 3 s
  apart; written for the old Render server that slept, the Worker answers at once). Server URL is the built-in default;
  Settings.json / `FUNKYMAKER_SERVER_URL` are ignored on purpose. Debug builds only:
  `FUNKYMAKER_TESTGATE_URL` overrides it (local testing).
  Results: `Passed` (`{"open":true}`), `Closed` (`open:false`, optional `message`),
  `NoInternet` (no network interface, or DNS/network-level socket errors on the last
  attempt), `Unreachable` (timeouts, HTTP errors, bad JSON, incl. a server without the
  endpoint → 404).
- `Game1.TestGate.cs`: while not passed, `Update` runs only `UpdateTestGate` and `Draw`
  only `DrawTestGate` (window drawn straight to the backbuffer), so no menu, level or
  setting is reachable. Closed → "TESTING IS CLOSED" + server message, ENTER/ESC (tap /
  BACK on Android) quits. NoInternet / Unreachable → ENTER/tap retries (5 more
  attempts), ESC quits. Checking → "Attempt N/5". Checked once per launch only.
- Server side (Worker, `GET /api/testing`): `[vars]` `TESTING_OPEN = "false"` and
  `TESTING_MESSAGE = "..."` in `FunkyMakerTransferServer/worker/wrangler.toml`, then
  `npx wrangler deploy` in `worker/` (§12.z). Default open. (The old ASP.NET server used the
  `Testing__Open` / `Testing__Message` env vars on Render.)
- Not tamper-proof: the exe isn't obfuscated, so a determined user could patch it.

### Obfuscation

`confuser.crproj` and `FunkyMakerPlayer.crproj` (ConfuserEx, repo root) and
`FunkyMakerPlayer/obfuscar.xml` (Obfuscar) are present, but **none runs
automatically**. The Obfuscar MSBuild target in the csproj is
commented out. Do not claim builds are obfuscated unless you wire it up.

---

## 5. Runtime architecture

### 5.1 Entry point (`Program.cs`)

1. Startup goes straight to the game window. The blocking pre-launch
   `Updater.CheckAndUpdate()` call was **removed in 0.4.7**; update checks now
   run asynchronously from `Game1.Initialize` (Release builds only, see §16).
2. If the first CLI argument is an existing file, `Game1.FnmmPath` is set; the
   game then loads that level at startup.
3. `new Game1().Run()` inside try/catch.
4. Unhandled exceptions go to `HandleCrash`, which writes
   `<exe dir>/crash/crash_<timestamp>.log` and `<exe dir>/crash.log`, then shows
   a platform dialog (Win32 MessageBox / zenity / kdialog / console).

### 5.2 `Game1` is the central class

`Game1.cs` is `public partial class Game1 : Game` and owns almost all runtime
state, input dispatch, and rendering. Key groups:

- Managers: `_settingsManager`, `_cameraManager`, `_touchControls` (Android).
- Graphics: `_spriteBatch`, `_font`, `_pixel`, `_renderTarget`, `_rtWidth/_rtHeight`.
- Level: `_level`, `_noteRenderers`, `_events`, `_corneredEvents`, `_objAnimators`.
- Menus: `_freeplay`, `_editorPicker`, `_levelSelector`, `_activeEditor`,
  `_mainSettingsMenu`, `_hudEditor`, `_multiplayerMenu`, `_pauseMenu`,
  `_arrowsMenu`, `_splashMenu`, `_countdownMenu`, `_fontMenu`, `_keyBindOverlay`.
- Loading: `LoadState { Idle, Loading, Finalizing, Done }`, `_loadingScreen`,
  `_pendingLevel`, `_pendingPath`, `_loadingComplete`.
- Audio: `_music`, `_musicInst`, `_musicPcm`, `_musicSampleRate`,
  `_musicChannels`.
- Timing: `_songTime`, `_songBeats`, `_started`, `_paused`, `_songStopwatch`,
  `_pausedTime`, `_pauseStarted`, `_songEndTime`, `_songEndDelay`.
- Multiplayer: `_isMultiplayerMatch`, `_multiplayerPlayerRole`,
  `_multiplayerStartTimestamp`, `_opponentName`, `_opponentScore`,
  `_opponentHealth`, `_resultsScreen`, `_stateUpdateTimer`.
- HUD: `_healthBar`, `_timerBar`, `_judgementOverlay`, `_gameOverOverlay`.
- Countdown/transition: `_countdownOverlay`, `_songTransition`.
- Static settings bridge (see §10.3).
- Playtest: `_playtestingFromEditor`, `_playtestStartSeconds`,
  `_preloadedPlaytestPcm` (Chart Editor → gameplay → back, §11).
- Return targets: `ChartEditorReturnTarget { EditorPicker, Freeplay,
  LevelSelector, Gameplay }`, `HudEditorReturnTarget { Freeplay, EditorPicker,
  FreeplaySettings, GameplaySettings, MultiplayerSettings, PauseMenu }`.

Rendering helpers moved out of `Game1` in 0.4.7: scene textures/animators/draw
→ `FnmmSceneRenderer`, HUD → `UI/GameplayHudOverlay`, error/drop-hint screens
→ `UI/ErrorOverlay`. `Game1` still owns the calls and state.

**Window focus gating.** `Game1.IsWindowActive` / `Game1.CanAcceptInput` block
input while the window is unfocused, during a short `_focusRegainGrace`, and
until the mouse button is released after refocus (`_waitMouseReleaseAfterFocus`),
so an Alt+Tab or focus click never triggers menus or notes. `OnDeactivated`
auto-pauses solo gameplay (not multiplayer). Editors (e.g. `NoteGrid`) check
`CanAcceptInput` too. On Windows the title bar uses dark mode
(`UI/DarkModeHelper`).

**State is flag-based, not a formal state machine.** The main flags are:

| Flag | Meaning |
|---|---|
| `_inFreeplay` | main menu / level list |
| `_inEditorPicker` | editor launcher grid |
| `_inLevelSelector` | Chart Editor level picker |
| `_inEditor` | an `IEditor` is active |
| `_inMainSettings` | settings from main menu |
| `_inHudEditor` | HUD layout editor |
| `_inMultiplayer` | multiplayer menu |
| `_inPauseMenu` | pause menu |
| `_settingsManager.IsActive` | in-game settings overlay |
| `_loadingScreen != null` | loading/ready screen |
| `_resultsScreen != null` | multiplayer results |
| `_songTransition != null` | end-of-song transition |
| `_errorMsg != null` | error screen |
| `_level == null` | no level loaded (drop hint) |
| `_started`, `_paused` | gameplay timing |
| `_isMultiplayerMatch` | a PvP match is active |
| `_playtestingFromEditor` | gameplay launched from the Chart Editor |

### 5.3 Update order (`UpdateInternal`)

Order matters because overlays consume input before gameplay.

1. Merge Android touch input into the keyboard state; update `TouchControls`.
   Global volume hotkeys `-`/`+` (also numpad) change Master Volume everywhere,
   menus included, except key rebinding, the Chart Editor (`-`/`+` = zoom) and
   while typing text (`LevelSelector.IsTypingName` = New Chart modal open,
   `MultiplayerMenu.IsEditingText`). The volume toast is drawn by every screen.
   Online menu + settings overlay (opened with `F1` / the settings pill): a
   dedicated ESC branch unfocuses the row or closes the overlay and calls
   `MultiplayerMenu.ResetInputCooldown()` (the general ESC branch skips
   `_inMultiplayer`, and the menu isn't updated while the overlay is open).
2. ESC/P edge detection → pause menu / settings / back handling.
3. Pause menu (`_pauseMenu.HandleInput`) → resume/restart/settings/exit.
4. Arrows menu, splash menu, countdown menu, font menu.
5. Settings menu (`_settingsManager`).
6. Freeplay menu (`SettingsRequested`, `EditorPickerRequested`,
   `NewChartRequested`, `ChartEditorRequested`, `LevelChosen`, `MenuAction`
   dispatch — see §10.1).
7. Editor Picker, Level Selector, active Editor.
8. Main Settings menu, HUD Editor, Multiplayer menu.
9. Loading screen (runs even when `_level` state is transient).
10. Results screen (multiplayer).
11. Gameplay: game-over check → countdown → song timing/stopwatch → camera
    beat zoom → Cornered exclusive events → judgement/combo timers → note
    renderers → particle splash → events/animators → notes alpha fade →
    health checks → song end.
12. Song transition update, then `base.Update`.

In multiplayer matches **Esc opens the pause overlay without pausing** (gameplay
and audio keep running); `P` is disabled and logs
`[Game1] Pause disabled during multiplayer match`. With no level loaded, Esc
toggles the settings overlay (outside Freeplay/Multiplayer menus).

### 5.4 Draw order (`DrawInternal`) and letterboxing

Menus are drawn **directly to the window** for crisp text; gameplay is drawn to a
fixed render target and then letterboxed.

Order:
1. Freeplay menu (direct), mods help (Android), song transition, touch controls.
2. Editor picker / Level selector / Active editor / Main settings (direct).
3. HUD editor (rendered to render target + `BlitToScreen`).
4. Multiplayer menu (direct) + song transition.
5. Loading screen (render target + blit).
6. Results screen (render target + blit).
7. Error / no-level drop hint (render target + blit).
8. Gameplay:
   - scene objects in scene.json order (`DrawSceneOrdered`)
   - HUD (health bar, icons, stats, timer)
   - notes + particle splashes
   - screen flashes, countdown, judgement, game over overlay
   - song transition
   - `BlitToScreen()`
   - overlays: settings, pause, arrows, splash, countdown, font
   - Android touch controls last.

`BlitToScreen()` scales the render target to fit the window preserving aspect
ratio, centered, with black bars. `GraphicsDevice.Clear(Color.Black)` happens
before the blit.

(fork) The blit is done by `PresentRenderTarget(dst)` with **sharp bilinear**
scaling: the 1280×720 frame is first upscaled nearest-neighbour by the integer
factor just above the window scale (`_upscaleTarget`, capped by GPU max texture
size), then drawn bilinear to the letterbox rect. Pixel art (`bilinear=false`
objects) stays crisp and evenly sized at any window size or fullscreen; before,
a plain bilinear stretch blurred it everywhere but 1280×720. The loading screen
uses the same path (the old "Bilinear Filter" setting was removed).

(fork, 2026-11-20) **Native render resolution.** `_rtWidth/_rtHeight` are now the
*logical* size (1280×720 / 720×1280) that all gameplay layout code uses; the physical
`_renderTarget` is logical × `_rtScale`. `EnsureRenderTarget()` (called every frame
in `DrawInternal`, in `RenderLoadingScreen`, `LoadContent` and `SetupLevel`) sets
`_rtScale` to the window's letterbox scale (clamped 0.25 .. GPU max texture size) when
`Game1.NativeRenderResolution` is on, else 1, and recreates the target on change.
Every `SpriteBatch.Begin` into the target passes `RtScaleMatrix` (scene: via
`FnmmSceneRenderer.DrawSceneOrdered(..., outputTransform:)`, composed after the
camera/fixed matrices), so the game draws in logical coords but rasterizes at native
resolution. `_lastCamMatrix` stays logical (strumline placement). Passes that used
PointClamp "for crisp text" (HUD editor, results, countdown/judgement/game over)
use `RtTextSampler` (Linear when scaled). `PresentRenderTarget` works on the physical
size, so at native resolution k = 1 and it's a ~1:1 blit; the sharp-bilinear
upscale only runs in 720p mode. **New Begin calls that draw into `_renderTarget`
must pass `RtScaleMatrix`**, or they draw at 1/scale size in the top-left corner.
Menus/overlays drawn directly to the window are unaffected.

### 5.5 Level loading pipeline

`StartLoadingLevel(path)`:
1. Stops music, clears error/overlay/pause state, sets `LoadState.Loading`,
   `_pendingPath`, `_inFreeplay=false`, `FnmmPath`.
2. Creates `LoadingScreen` (progress/step display).
3. `Task.Run` calls `FnmmLoader.Load(path, progress)` on a background thread
   (no GPU calls there).
4. On success: `_pendingLevel = level`, `_loadState = Finalizing`.
   On failure: `_errorMsg = ex.Message`, `_loadState = Done` (drawn by
   `ErrorOverlay.DrawError`).
5. Main thread: `LoadState.Finalizing` calls `SetupLevel()`, then
   `LoadState.Done`; the loading screen plays Ready → Clearing → Done.
6. For a directory, `FnmmLoader.Load` first looks for a `data.json` anywhere
   inside it (`FindLevelDir`, recursive) and loads that folder. Only if none is
   found and the folder has `data/` does it throw `PSYCH_ENGINE_MOD`, which the
   `StartLoadingLevel` catch turns into a friendly "Psych Engine mods are not
   supported…" message (fork change; upstream shows the raw marker).

### 5.6 `SetupLevel()` responsibilities

In rough order:
- dispose old audio, reset scene objects to initial state.
- choose logical render size: `1280×720` if `level.Scene.Landscape`, else `720×1280`
  (physical target = that × `_rtScale`, `EnsureRenderTarget`, §5.4).
- build sprite usage (background vs. character) and preload textures into a
  `name → textures` map via `FnmmSceneRenderer.LoadLevelTextures` (reuses
  `_spriteTextureCache` across restarts of the same level).
- apply GPU max texture size, split oversized strips, pack atlases.
- build one `SpriteAnimator` per scene object (index-aligned) via
  `FnmmSceneRenderer.CreateAnimators`.
- force middlescroll off in multiplayer (side-by-side layout).
- create health icons, note renderers (one per strumline), wire note callbacks.
- create `EventProcessor` (converts event times to seconds if needed).
- create `CorneredEventHandler` if UUID matches `01kd72pw682p6mrj4wg388zgz5`.
- load music via `OggLoader.LoadWithPcm` (for seeking).
- initialize camera/HUD/countdown/etc.

---

## 6. FNMM level format

### 6.1 Containers

`FnmmLoader.Load(path)` detects by magic header:

| Header | Format | Loader |
|---|---|---|
| `PK` (0x50 0x4B) | ZIP (`.fnmm`/`.zip`) | `FnmmConverter.ConvertIfNeeded` → `LoadFromZip`; **if that throws**, falls back to the SharpCompress extractor |
| `Ra` (0x52 0x61) | RAR | SharpCompress → temp dir → `LoadFromDirectory` |
| anything else | unknown | last-chance SharpCompress extraction, else "Unknown level container format" |
| directory with a `data.json` anywhere inside | unpacked level | `LoadFromDirectory` on the first folder found |
| directory with `data/` and no `data.json` | Psych Engine mod | throws `PSYCH_ENGINE_MOD` (friendly message in `Game1`) |

ZIP layout can be **flat** (`data.json` at root) or **nested** (all files inside
one UUID-named subfolder). ZIP entry names are normalized (`\` → `/`) and
directory entries skipped. The extractor (`LoadFromRar`, now used for RAR and
fallback ZIPs) writes to `%TEMP%\fnmm_*`, then:
1. if a `data.json` is found, runs `FnmmConverter.ConvertDirectoryIfNeeded`
   (in-place legacy conversion) and loads that folder;
2. otherwise, if the archive contains an inner `.fnmm`/`.zip`/`.rar`, loads
   that recursively (falling back to the outer file name if `meta.name` is
   empty);
3. otherwise throws "No data.json or .FNMM level found in archive."

Freeplay discovery: `*.fnmm`, `*.zip`, `*.rar` (case-insensitive) recursively
under `mods/levels`, skipping `Backups/`, plus top-level directories containing
`meta.json` or `data.json`. Folder entries show the folder name as-is (fork
change: dots in folder names are kept).

### 6.2 Level files

| File | Required | Notes |
|---|---|---|
| `data.json` | yes | chart data; `data.json` must exist or load fails |
| `scene.json` | no | defaults to empty scene |
| `events.json` | no | defaults to empty events |
| `meta.json` | no | `name`, `UUID`, `version`; `name` overrides display name |
| `music.ogg` | no | decoded by `OggLoader`; may be absent |
| `*.png` (root) | no | loose images keyed by filename without extension |
| `sprites/<name>/sprite.json` | no | sprite definition |
| `sprites/<name>/<n>.png` | no | frames; numeric filenames preserve index gaps |

### 6.3 `data.json`

```jsonc
{
  "bpm": 120,          // <= 0 → times treated as seconds
  "version": 2.0,      // >= 2.0 → times are seconds
  "npb": 4,            // notes per beat
  "speed": 1.0,
  "strumLines": [      // FunkyMaker writes "strumLines"; the loader also accepts
                       // "strumlines"/"StrumLines"/"STRUMLINES"
    [                  // strumline 0 = player (right)
      [                // lane 0 (left) … one array per lane, 4 lanes
        { "start": 0.0, "duration": 0.5, "props": { "skin": 1, "onPressEvent": 0, "onMissEvent": -1 } }
      ],
      [], [], []
    ],
    [ [], [], [], [] ] // strumline 1 = opponent (left)
  ]
}
```

- Nesting is **strumline → lane → notes** (`Strumlines[s][lane][i]`).
- `start`/`duration` are in beats or seconds per §6.7.
- `duration > 0` = hold note; `0` = tap.
- `props.skin` is **purely visual** (`NoteSkins`): `0` default, `1` negative
  (X), `2` positive (O). There is no skin 3; any other value is drawn as `0`.
  The player applies **no** hardcoded effect for any skin.
- `props.onPressEvent` / `onMissEvent` are **action-pack indices**; `-1` = no
  event. These packs are the only source of note effects (damage, instakill,
  heal…). Pack index `0` is valid and fires.
- `FnmmData.TimesAreSeconds` / `ToSeconds()` / `EffectiveBpm` are the helpers.

### 6.4 `scene.json`

```jsonc
{
  "objects": [
    {
      "spr": "boyfriend",
      "visible": true,
      "fixed": false,
      "strumLine": 0,      // -1 = background/UI; 0 = player; 1 = opponent
      "camera": -1,        // optional camera attachment
      "anim": 0,           // optional initial animation index
      // V2 legacy position:
      "x": 0, "y": 0, "scale": { "x": 1, "y": 1 },
      // V3 new position (alternative to x/y/scale):
      "transform": [
        { "position": {"x":0,"y":0}, "scale": {"x":1,"y":1}, "scrollFactor": {"x":1,"y":1} }
      ],
      "color": { "r":255, "g":255, "b":255 },
      "opacity": 1.0
    }
  ],
  "cameras": [
    { "x": 514, "y": 1070, "scale": 0.95 },                 // legacy
    { "position": {"x":514,"y":1070}, "scale": 0.95 }       // nested/V3
  ],
  "icons": ["boyfriend", "dad"],
  "landscape": true
}
```

- `SceneObject.GetPosition/GetScale/GetScrollFactor` pick `transform[current]`
  when present, else `x/y/scale`.
- `SaveInitialState()` is called at load; `ResetToInitialState()` restores
  `spr`, `visible`, `strumLine`, `currentTransformIndex` on restart.
- `SceneCamera.UsesNestedPositionFormat` is set by `FnmmLoader` when the nested
  form is detected; this controls save format and legacy scale handling.

### 6.5 `events.json`

```jsonc
{
  "events": [
    { "time": 4.0, "actions": [
      { "type": "CameraSet", "props": { "mode": "tween", "target": 1, "transitionTime": 0.5 } }
    ]}
  ],
  "action_packs": [
    { "actions": [ { "type": "PlayerHealth", "props": { "operation": "remove", "minHealth": 0.0, "value": 30.0 } } ] }
  ]
}
```

- `PlayerHealth`: `operation` `add` / `remove` / `set`, `value` and `minHealth`
  on the **FunkyMaker health scale 0–30 (30 = 100% of the bar)**,
  `NoteSkins.FmmMaxHealth`. `EventProcessor` divides by 30 to get the engine's
  0..1 health (fork fix; upstream divided by 100, making every level health
  effect ~3.3× too weak). Example above = instakill.
- Engine health is 0..1 and starts at 0.5. The built-in hit `+0.023` / miss
  `-0.0475` are FNF-style constants, **not** FMM values (unverified against
  FunkyMaker, see §18).

- `event.time` is stored in beats/seconds like notes, then **mutated to seconds**
  in the `EventProcessor` constructor when `!TimesAreSeconds`.
- Events are sorted by time at load.
- Action packs are referenced by note props (`-1` = none); any index `>= 0`
  fires (see §8.5).
- Supported action types (from `EventProcessor.KnownTypes`):
  `ObjectAnimationSet`, `ObjectVisibilitySet`, `ObjectSpriteSet`,
  `ObjectBlendColor`, `ObjectOpacitySet`, `ObjectTransformSet`,
  `ObjectPositionSet`, `CameraSet`, `CameraZoom`, `ScreenFlash`,
  `BarIconChange`, `RunActionPack`, `PlayerHealth`, `ObjectChartChange`,
  `CameraShake`, `GUI`.
- Common props: `object`, `anim`, `sprite`, `value`/`visible`, `color_r/g/b`,
  `position`/`transform`, `interpolation`, `mode`, `target`, `transitionTime`,
  `transitionTimeUnit`, `easingType`, `strumLine`, `actionPack`, `operation`,
  `minHealth`, `alpha`, `duration`, `timeUnit`, `strength`, `shakes`, `delay`,
  `element`.
- `ObjectSpriteSet` swaps the animator and carries over position/scale/tint/opacity/
  fixed AND the current anim index, event lock and hold. Required: levels do alts by
  swapping sprites constantly (HEART `mark`↔`markalt`), and some rely on the anim
  continuing on the new sprite (PARADIGM 94.2s `wormtran` keeps looping anim 6).
  (Resetting to idle here was tried and broke HEART.)
- `ObjectChartChange`: the object whose strumline actually changes always goes to
  idle, releasing an event lock/hold (FMM behaviour; legacy levels put `changetype`
  before `changeanim`). Repro: PARADIGM 83.744s. Other objects on the old/new
  strumline still only reset if not event-locked. Caveat: new-format events with the
  anim action BEFORE the chart change lose that anim (bwa annaedition 59.625s).

### 6.6 `sprite.json`

```jsonc
{
  "type": 1,
  "data": [
    { "bilinear": true, "loop": true, "num": 4, "spd": 24, "spd_fps": true,
      "offset": {"x":0,"y":0}, "scale": 1.0 }
  ]
}
```

- `type`: see §7.1.
- `num` may be a number or a numeric string.
- `loop`, `spd`, `spd_fps`, `offset`, `scale` control animation playback.

### 6.7 Time conversion rules (`FnmmTimeConverter`)

`AreTimesInSeconds(version, bpm)`:
- `version >= 2.0` → seconds.
- else `bpm <= 0` → seconds.
- else beats → `ToSeconds(t) = t / bpm * 60`.

`GetEffectiveBpm(bpm)` returns `bpm > 0 ? bpm : 120` for scroll/countdown math.

### 6.8 Legacy conversion and saving

`FnmmConverter.ConvertIfNeeded` detects old ZIPs by `evt.json` or `data.json`
containing `"notes"` and not `"strumlines"`. It converts in memory
(`ConvertDirectoryIfNeeded` does the same in place for extracted folders):
- `notes` flat 8-lane array → `strumlines`
- `evt.json` → `events.json` (`ConvertOldEvent`; same-time entries grouped in file
  order). Old types: `camera` → `CameraSet` (`auto` or 0-based index copied as-is,
  `focus`, always `transitionTime=0.83` s), `changeanim`/`changesprite`/`changetype`/
  `visible` (`yes`/`true`) → Object* actions, `flash` → fixed `ScreenFlash`,
  `shake` (no args) → `CameraShake` delay 0.10 s / 2 shakes / strength 12. Args are
  read as strings (bare JSON numbers/bools tolerated) and parsed culture-invariant.
- scene `type` (`0=bg,1=player,2=enemy`) → `strumLine`
- sprite `type 0` → treated like `type 2`
- fixes `sprite.json` fields

`FnmmSaver.Save(level, outputPath)` writes a **flat ZIP** with
`meta.json`, `data.json`, `scene.json`, `events.json`, `music.ogg`, root PNGs,
and `sprites/<name>/...`. It uses lowercase `strumlines`, preserves V2/V3
camera and object formats, and writes to a `.tmp` then atomically replaces.
A `.rar` output path is redirected to a sibling `.fnmm` (fork change), since
the content is always ZIP.

`ChartEditor.SaveLevel` also normalizes the target before calling it: an
unpacked folder saves as `<folder>.fnmm`, any extension other than
`.fnmm`/`.zip` becomes `.fnmm`. If the target exists,
`BackupManager.CreateBackupIfNeeded` first copies it to
`mods/levels/Backups/<LevelName>/original.fnmm` (Freeplay ignores `Backups/`).

---

## 7. Rendering systems

### 7.1 Sprite types

From `sprite.json` `type` and `SetupLevel`:

| Type | Meaning | Splitting behavior |
|---|---|---|
| `1` | multi-anim character sheet (one PNG per animation index, each a horizontal strip) | never split (would corrupt) |
| `2` | single background/UI sheet (one PNG, one horizontal strip) | tile/split when too wide |
| `3` | small icon PNGs | never split |
| `0` | item/prop; depends on usage | split if too wide; treated as background if only used by background objects, otherwise character |

Sprite usage is derived from scene objects: a sprite used only by objects with
`strumLine < 0` is background; otherwise character.

### 7.2 Texture handling

- **Level textures are freed when the next level loads** (`DisposeLevelTextures`, 2026-11-21): they used
  to be dropped without `Dispose`, and MonoGame only frees GPU textures when the GC finalizes them
  (decided by managed memory, not GPU memory). Online rounds load level after level from the room, so
  phones ran out of GPU memory: later levels were black / partly black online while fine in solo.
  `_levelTextures` = every texture of the current scene; skipped while a chart editor is open or
  suspended (it can share the cache). Freeing also clears `_objAnimators` and the health icons.
- Images that fail to load are skipped (logged only). `FnmmSceneRenderer.LastFailedTextures` /
  `LastTextureError` count them per `LoadLevelTextures`; `SetupLevel` turns that into an in-game message
  shown when the level starts (`_textureLoadWarning`, 12 s), e.g. a 32-bit phone out of memory used to
  show a silent black scene.
- **32-bit process memory protections (`!Environment.Is64BitProcess`, 2026-11-22):** `FnmmSceneRenderer.LoadLevelTextures`
  caps `maxTexSize` to `4096`, lowers the per-strip LOH compaction threshold from `64 MB` to `16 MB`, and routes
  any multi-frame strip whose raw RGBA size exceeds `AtlasPacker.GetDefaultStripVramBudgetBytes()` (`20 MB` on
  32-bit processes vs `48 MB` on 64-bit Android) through `TryPackAtlas` so it streams and downscales instead of
  allocating multiple full-sheet buffers in `LoadTextureViaImageSharp` (also fixed a bug in
  `FnmmSceneRenderer.LoadLevelTextures` where the `needsDownscale` branch lacked a `continue;` after
  `TryPackAtlas`, so whenever `TryPackAtlas` succeeded and set `animData.IsSplit = true`, execution fell
  through to `fullTex!.Width` / `textures.Add(fullTex!)` with `fullTex == null`, throwing
  `NullReferenceException` on `maxTexSize=4096` devices). LOH compaction is wrapped in
  `FnmmSceneRenderer.ForceCompactingGc()`, which catches `PlatformNotSupportedException` because 32-bit Android
  runs on the Mono runtime (where `GCSettings.LargeObjectHeapCompactionMode = CompactOnce` is unsupported and
  previously threw `PlatformNotSupportedException` inside `LoadLevelTextures`'s `finally` block, aborting
  `SetupLevel()` into a black screen). On 32-bit Android, `EnsureRenderTarget`
  and `PresentRenderTarget` also keep the gameplay render target at logical 720p (`scale = 1f`, `k = 1`, no
  intermediate `_upscaleTarget`).
All of this lives in `FnmmSceneRenderer` (shared by gameplay and the Chart
Editor stage preview); `Game1` delegates to it.

- `FnmmSceneRenderer.GetGpuMaxTextureSize()` P/Invokes OpenGL
  `GL_MAX_TEXTURE_SIZE`, clamped to MonoGame's 16384 (`MONOGAME_MAX_TEXTURE_SIZE`).
- If a single frame exceeds the limit → downscale and set
  `DownscaleCompensation` so it still draws at visual size.
- If the whole strip is too wide but frames fit → split per-frame (character) or
  into chunks (background). `SpriteAnimData` tracks `SplitOffset`,
  `ChunkFrames`, `FrameWidth`, `TextureIndex`, `DownscaleCompensation`.
- `AtlasPacker.PackHorizontalStrip` packs oversized strips (and large single-frame sprites when
  a level exceeds its platform VRAM budget) into GPU-safe 2D atlases; `UsesPackedAtlas` +
  `AtlasFrameRects` then drive frame lookup:
  - **Level-wide `AtlasPacker` optimization & streaming PNG / PCM pipeline (2026-11-23):**
    - **Streaming row-by-row PNG decoder (`AtlasPacker.cs`):** standard 8-bit non-interlaced PNGs
      (Gray, RGB, Indexed+`PLTE`, GrayAlpha, RGBA + `tRNS`, covering 100% of typical FNMM strips)
      stream straight from `IDAT` chunks via `ZLibStream` + RFC 2083 scanline unfiltering using a
      2-row sliding window into downscaled per-frame buffers (automatically falling back to
      ImageSharp for interlaced/exotic PNGs). Avoids allocating 200–630 MB full-resolution `Rgba32`
      images for giant strips (e.g. `Yellow`'s 425-frame `312800×528` = 630 MB strip).
    - **Transparent-margin trimming + exact pixel deduplication + NFDH shelf packing (`AtlasPacker.cs`,
      `SpriteAnimator.cs`):** tracks tight non-transparent bounds per frame (`FrameRect.TrimX`,
      `TrimY`, `OrigWidth`, `OrigHeight`, `IsEmpty`), deduplicates identical trimmed frames via
      64-bit FNV-1a hash + pixel-exact verification (keeping each frame's own `TrimX/TrimY`), skips
      completely transparent frames (`IsEmpty`), packs unique tiles into bounded 2D pages (`<= 4096px`)
      with 1 px transparent anti-bleed gutters, and preserves per-frame `AtlasIndex` in
      `ApplyPackedAtlasResults`. `SpriteAnimator.DrawAtlasFrame` compensates trimmed offsets accurately
      for both normal and flipped (`Scale < 0`) / `IsFixed` draws, `CurrentFrameSize` uses
      `OrigWidth/OrigHeight`, and `DrawSingle` + `DrawStrip` now apply `DownscaleCompensation` consistently.
    - **Adaptive level-wide VRAM budget & RAM-aware Android budgets (`Platforms/Android/AndroidMemoryBudget.cs`,
      `FnmmSceneRenderer.cs`, `AtlasPacker.cs`, `Game1.LevelLoad.cs`):**
      `LoadLevelTextures` performs a 0-RAM 24-byte PNG header pre-scan across uncached sprites and
      tracks actual uploaded GPU texture bytes (`usedVramBytes`) against a running platform budget.
      On Android, `AndroidMemoryBudget.Query()` reads real device `ActivityManager.MemoryInfo`
      (`TotalMem`, `AvailMem`, `LowMemory`, `IsLowRamDevice`) and scales budgets to each phone's RAM:
      32-bit stays capped at **96–128 MB** level / **8–12 MB** strip (`minScale = 0.15f`) due to the
      32-bit virtual address limit; 64-bit Android scales across tiers from **144 MB / 16 MB** (≤2 GB)
      → **192 MB / 20 MB** (3 GB) → **256 MB / 24 MB** (4 GB) → **384 MB / 36 MB** (6 GB) →
      **512 MB / 48 MB** (8 GB) → **768 MB / 64 MB** (12+ GB flagship), dynamically clamped against
      live `AvailMem` (`AvailMem / 3`, or `AvailMem / 4` on 32-bit, and `× 0.75` if `LowMemory`) and
      also driving `OggLoader`'s audio half-rate / mono thresholds (up to 256 MB / 512 MB PCM on 12+ GB
      phones). Desktop 64-bit PC stays **unlimited** so PC visual and audio quality remain 100% native.
      Small sprites (`<= 256 KB`, e.g., health icons) are exempt from global downscaling; as
      trimming/dedup saves VRAM on early sprites, the remaining budget automatically raises scale for
      later sprites. On Android (`!_playtestingFromEditor`), `releaseSourceBytes: true` frees each
      `frames[fi]` PNG `byte[]` immediately after GPU upload instead of holding ~180 MB of raw PNG bytes
      across all sprites.
    - **Streaming PNG speedup & Android multi-threaded texture pipeline (`AtlasPacker.cs`, `FnmmSceneRenderer.cs`, 2026-11-23):**
      `DecodeStreamingPngToFrames` skips `ConvertScanlineToRgba` completely for source rows skipped
      during downscaling, uses zero-copy `MemoryMarshal.Cast<byte, Rgba32>` for RGBA8 (`ColorType 6`),
      uses `Span<byte>` in `UnfilterScanline`, skips deduplication hashing when `frameCount == 1`, and
      uses tighter `1.10x` pre-trim headroom for >64 MB strips to avoid double-decoding 500–750 MB PNGs.
      `AtlasPacker` is split into thread-safe CPU preparation (`PrepareHorizontalStripCpu`) and main/GL
      upload (`UploadPreparedAtlas`). On both Android and Desktop (`#if !BLAZORGL`), `FnmmSceneRenderer.LoadLevelTexturesParallel`
      runs PNG decode/downscale/trim/dedup/shelf-pack across bounded background workers (**2** workers
      with `giantLimit = 1` on 32-bit; up to **4** workers / `giantLimit = 2` on 64-bit Android; up to
      **6** workers / `giantLimit = 2` on >4 GB Desktop PC) with a bounded ready-slot semaphore so peak
      RAM stays flat, while the main/GL thread smoothly uploads textures in exact frame order, animates
      `LoadingScreen`, and pumps hub keepalives. Desktop 64-bit PC keeps `levelVramBudget = long.MaxValue`
      (unlimited, 100% native quality untouched) while cutting massive 5+ GB level load times well under
      the server's match-loading timeout (180 s).
    - **Lossless-first `AtlasPacker` probe & zero-downscale PC 64-bit (`AtlasPacker.cs`, `FnmmSceneRenderer.cs`, 2026-11-24):**
      - `AtlasPacker.GetDefaultStripVramBudgetBytes()` now returns `long.MaxValue` on PC 64-bit (>4 GB RAM), removing the old 128–192 MB per-strip downscale cap on PC (100% native pixel quality for every sprite on PC).
      - On memory-constrained platforms (Android 32/64-bit and ≤4 GB PC), `PrepareHorizontalStripCpu` now runs a ~0-RAM streaming scanline probe (`ProbeStreamingPngTrimAndDedup`) BEFORE deciding whether to downscale: it computes exact tight non-transparent frame bounds and 128-bit frame hashes in a single streaming pass. If the real post-trim + deduplicated atlas area (`uniqueTrimmedArea * 4 * 1.12`) already fits inside `stripVramBudget`, `budgetScale` stays `1.0f` (100% native quality, zero blur); if downscaling is still necessary, `budgetScale` is computed from the real post-trim+dedup size rather than bloated raw untrimmed bytes.
      - When decoding at `1.0x` after probing (`WriteTightNativeRowToFrames`), only tight `TrimW × TrimH` buffers are allocated for unique non-empty frames (duplicate frames share the first matching frame without extra RAM allocation), keeping 32-bit Android peak RAM flat even for huge strips (e.g. *Trinity Encore*).
      - `FnmmSceneRenderer` now estimates post-trim+dedup VRAM via `EstimateLosslessVramBytes` (`0.45x` multi-frame, `0.85x` single-frame, capped at `stripVramBudget`) instead of raw untrimmed RGBA bytes when computing `dynamicLevelScale`, and routes medium/large strips (`>512 KB` multi-frame / `>1 MB` single-frame) through lossless `1.0x` `AtlasPacker` when a level VRAM budget is active.
    - **Multiplayer long-load keepalive & infinite-wait guards (`NetworkManager.cs`, `Game1.LevelLoad.cs`,
      `Game1.Online.cs`, `Game1.cs`, `LoadingScreen.cs`, 2026-11-23):** `RenderLoadingScreen` now calls
      `NetworkManager.Instance.PumpKeepAliveDuringLoad()` so hub pings continue every 2 s during
      heavy `SetupLevel` loads, and `NetworkManager.Update` clamps `dt` to `0.5 s` so a single slow load
      frame never triggers a false `CONNECTION_TIMEOUT` disconnect. `CheckMultiplayerLoadingScreenStatus`
      recovers `MatchStart` from `RoomState.Match.StartTimestamp` if missed during load, exits cleanly
      back to the Waiting Room with a toast if the server cancelled the match or dropped the player for
      taking too long (instead of hanging forever on `Waiting for ...`), allows ESC / Android Back / B
      on the waiting screen to cancel and return to the room, and deduplicates `LoadingScreen`'s
      per-frame `Waiting:` console log.
- `Game1._spriteTextureCache` (`name → index → SpriteTextureCacheEntry`) avoids
  recreating textures on song restart/playtest; it is cleared when loading a
  different level.
- `SpriteAnimator` advances frames, handles loop, speed, offsets, scale,
  `IsSetByEvent`, `HoldUntil`, and per-object opacity. Every animation starts
  at frame 0. A character's idle is **always anim 0** (`SpriteAnimator.IdleAnim`;
  no "1-frame placeholder" heuristic). Character return-to-idle follows FMM:
  `loop=true` holds the pose for the sing time then idles; `loop=false` **stops
  on the last frame** (no auto-idle); event-set non-looping anims are protected
  while they play (notes/misses skip `IsSetByEvent` characters). Notes animate
  hidden / 0-opacity characters too (no visibility gate), because events run
  after note renderers each frame: a character revealed on the same note
  already shows the sing pose.
  While a hold is active (`HoldUntil`), animations with more than 2 frames loop
  frames **0–1** (`HOLD_LOOP_FRAMES = 2`), including event anims set mid-hold; on
  release playback jumps to frame 2 if still in the loop range, plays the rest
  once and then ends **exactly like a non-hold animation** (`loop=false` freezes
  on the last frame, `loop=true` poses idle after the sing time, looping event
  anims keep looping). Seeking re-poses active notes via
  `FnmmSceneRenderer.TriggerCharacterSing` (same mapping as a note hit).
- `EventProcessor` sprite swaps preserve position/scale/color/opacity/fixed
  state from the previous animator (respecting transform arrays).
- `BgSpriteAnimator` handles tiling for type-2 backgrounds.

### 7.3 Camera and scene

- `CameraManager` builds the world matrix; `Game1.BuildCameraMatrix` delegates
  to it using `_events.Camera` / `_events.HasCameraEvents`.
- `GetBaseW()/GetBaseH()`: landscape `2160×1920`, portrait `1920×2160`.
- Camera `x/y` is the world-space center; `scale` is zoom relative to base.
- `scrollFactor` on objects controls parallax; `fixed=true` objects draw in
  screen space.
- `CameraSet` / `CameraZoom` events and beat-synced zoom live in
  `EventProcessor` / `CameraManager`.
- Auto-camera (`EventProcessor.NotifyNoteHit`): uses the singer's `object.Camera`;
  if unset (-1), FNF convention (strumline 0 → cam 1, strumline 1 → cam 0). If that
  camera doesn't exist (single-camera levels), it falls back to cam 0 — it never
  invents a camera at the character's position unless the scene has no cameras.
  Single-camera levels start with auto disabled; a `CameraSet` auto (or out-of-range
  target) event re-enables it.

### 7.4 HUD

- `UI/GameplayHudOverlay.DrawHUD` draws the whole gameplay HUD (health bars,
  single/multiplayer stats, timer, song info, debug info, FPS, controls
  status); `CalculateStats` computes `GameplayStats` (weighted FNF accuracy,
  100% when nothing has been judged); `DrawScreenFlashes` draws flash events.
- `UI/HealthBar`: health value 0..1, gradient opponent/player fill with border
  and highlights, icon bobbing.
- `HudSettings` (`Assets/data/hud_settings.json`, see §15) stores element
  positions/sizes **and visibility**: `MasterHudVisible`, per-element
  `HealthBar/Score/Misses/Accuracy/SongInfo/StatusInfo/Combo/Judgement/
  OpponentIcon/PlayerIcon` `Visible`, `StatusInfoPosition` (bottom-left
  Autoplay/Scroll/Speed line). `TimerBarVisible` and the strumline toggles are
  `[JsonIgnore]` proxies to `Game1.ShowTimer`, `NoteRenderer.ShowStrumlines`,
  `NoteRenderer.HidePlayerStrumline`, `Game1.HideOpponentStrumline`,
  `Game1.HideExtraStrumlines` (persisted by `Settings` instead).
- Hold rendering (`FMMNoteStyleRenderer`, `CodenameNoteStyleRenderer`) ports
  FNF `SustainTrail`: end caps keep a 1:1 aspect ratio, `bottomClip = 0.9`,
  body and cap join with no gap/overlap, and 1 px edge rows are trimmed to
  avoid seams.
- `UI/TimerBar`: presets `Legacy`, `HUD Editor` (was `Custom HUD`; old saves are
  converted on load), `Top`, `Left`, `Middle`, `Right`, `Strumline`; text modes
  `Time Left`, `Time Elapsed`, `Song Name`, `Song Name + Difficulty`, `None`, used by
  every preset (`GetText`; Legacy / HUD Editor used to always show elapsed time, and
  now center the text on the bar). Force Top only applies to the automatic presets.
- `UI/JudgementOverlay`: stacked rating popups and falling combo digits.
- `UI/CountdownOverlay` plays `ready/set/go` from `mods/countdowns/<name>/`,
  falling back per file to `mods/countdowns/Default/` and then the shipped copy.
  `EnsureLoaded(SelectedCountdown)` runs at each song start, so a pack picked in
  Settings applies to the next song. (Before the fork fix it was loaded once in
  `LoadContent`, before `Settings.Load`, so the saved pack never showed.)

---

## 8. Gameplay systems

### 8.1 Strumlines and layout

- `NoteRenderer.LANE_COUNT = 4`, `LANE_WIDTH = 110`, `BASE_SPEED = 450`
  (FNF 1.0× speed; was 600 before 0.4.7).
- (fork) Scroll speed is **constant**: `_scrollSpeed = BASE_SPEED` px/s (×
  `ScrollMultiplier` × screen scale). It used to be `BASE_SPEED × BPM/120 × data.speed`,
  so the same setting scrolled at different speeds per chart; the chart's `speed`
  field is now ignored in gameplay (the chart editor's `NoteGrid` still uses BPM).
- (fork) `NoteRenderer.LaneLeftX` is the single lane-X formula (normal and
  middlescroll split); it applies the HUD editor strumline scale
  (`HudStrumScale`) around the lane group's centre.
- Strumline index `0` = player (right), index `1` = opponent (left),
  extra strumlines fill to the right (index 2+ are hidden when idle).
- `Midlescroll` centers the player and splits the opponent around them.
- Android touch hitboxes: invisible at rest; while pressed they show at `Game1.TouchHitboxOpacity`
  (Settings → CONTROLS → Touch Hitbox Opacity, 0-100 %, default 30 %, saved as `TouchHitboxOpacity`;
  was a fixed 40 %). Touch Hitbox Colors OFF still hides them completely.
  - **Mobile touch & hold fixes (`TouchControls.cs`, `NoteRenderer.cs`, `Game1.cs`, 2026-11-24):**
    - `TouchControls` now tracks active gameplay touches by `touch.Id` (`_fingerLane`), locks a finger to its
      starting lane while a hold note is active (`IsLaneHolding`, else 35 % lane-width hysteresis before
      sliding), supports multi-finger same-lane taps (`IsLaneJustPressed`), eliminates 1 px integer-rounding
      gaps and screen-edge misses (`LaneFromX`), dynamically resyncs hitbox bounds (`SyncBounds`) against
      `TouchPanel.DisplayWidth/Height`, prevents Pause button touches from also firing lane 3 (and vice versa),
      switches into `Gameplay` mode *before* polling `TouchPanel.GetState()` (including during countdown), and
      follows live `NoteRenderer.PlayerKeys` instead of hardcoded `D/F/J/K`.
    - `NoteRenderer.UpdatePlayer` now reads `prevMask` from `WasLaneDown` (`_touchControls.WasLaneDown(lane)`)
      instead of `IsKeyDown(prevKb, ...)` (which previously OR'd the *current* frame's touch state).
- `HideExtraStrumlines`, `HideOpponentStrumline`, `NoteRenderer.ShowStrumlines`
  (master toggle) and `NoteRenderer.HidePlayerStrumline` (hides visuals, input
  still works) are settings. Splashes only show when the strumline is visible.
- Upscroll receptor at ~15% height; downscroll at ~85%.

### 8.2 Judgement

| Rating | Window | Score |
|---|---|---|
| Sick! | ±45 ms | 120 |
| Good | ±90 ms | 69 |
| Bad | ±135 ms | 34 |
| Shit | ±166 ms | 17 |
| Miss | — | −20 |

- (fork, 2026-11-20) Points follow FunkyMaker Mobile: Sick 120, miss −20 (`NoteRenderer.SCORE_*`,
  `MISS_PENALTY`); Good/Bad/Shit and the hold bonuses keep FNF's proportions scaled by 120/350
  (were 350/200/100/50 and 250/150/75/25). **Every miss now costs 20**: notes that pass, ghost taps
  with Ghost Tapping off, and holds released before 25 %. Miss-free notes (avoid / heal notes) still
  cost nothing. Score can go negative.
- `NoteRenderer.ScoreMultiplier` (fork) multiplies every point gained or lost on that strumline
  (score kept as a double, `Score` rounds it). Used for online side balancing (§12); 1 elsewhere.
  `MaxScoreForChart(level, chart)` = Sicks + full holds, avoid-notes excluded;
  `SideScoreMultiplier(level, chart)` = max(player, enemy) perfect score / this chart's (≥ 1).
- `HIT_WINDOW = 0.166f` is also the miss threshold.
- Hold bonuses: Sick 86 / Good 51 / Bad 26 / Shit 9.
- Hold minimum completion `0.25`; hold end leniency `0.10 s`; completed hold
  counts as a Sick, failed hold counts as a Shit.
- `InputBuffer` default **and max** `0.010 s`; `SafeFramesTime` default **and
  max** `0.083 s` (they can only be lowered; `Settings.Load` clamps them).
  `ScrollMultiplier` is clamped to `0.25–5.0` (`<= 0` → `1.0`).
- `GhostTapping` default `true`: empty presses don't break combo.

### 8.3 Health

- Manual hit: `+0.023`.
- Miss: `-0.0475` (not applied to avoid-notes, see §8.5).
- **No skin-based effects** (fork change): the old fallbacks (skin 1 → health
  0, skin 3 → +0.15, `OnDamageNoteHit`/`OnInstakillNoteHit`/`OnHealNoteHit`)
  were removed. All note effects come from `onPressEvent`/`onMissEvent` packs.
- Health <= 0: single-player game over overlay (`GameOverOverlay`: RETRY /
  MENU buttons, Left/Right/Up/Down select, Enter/Space confirm, `R` retry,
  Esc/Backspace menu; it **never auto-restarts**); **multiplayer disables game
  over**.
- Health is also driven by `PlayerHealth` action packs when events are defined.

### 8.4 Holds

- `duration > 0` creates a hold; `holdEnd = start + duration`.
- Must hold through the body; release handling has a 100 ms end leniency (`HOLD_END_LENIENCY`) and an
  85 ms deterministic `songTime` drop-grace (`HOLD_RELEASE_GRACE`, `_holdReleaseTime[lane]`) so brief
  1–5 frame mobile touchscreen / multi-touch micro-drops don't immediately cancel active holds (if truly
  released, completion progress is measured at the original release timestamp).
- Completed hold counts as Sick and shows the hold confirm animation.
- `_holdDuration[lane]`, `_holdEndTime[lane]`, and `_holdReleaseTime[lane]` track active holds.

### 8.5 Autoplay and note event packs

- `OnNotePressEvent` / `OnNoteMissEvent` fire for any index `>= 0`;
  `EventProcessor.FireActionPack(index)` is 0-based and validates bounds.
- **Avoid-notes** (`NoteSkins.ShouldAvoid`, fork): a note is meant to be
  avoided when the health effect of its `onMissEvent` pack is greater than
  that of its `onPressEvent` pack (pressing hurts/kills, or missing heals).
  Effects come from `NoteSkins.GetPackHealthEffect` (fraction of the bar, FMM
  value / 30: `add` +v, `remove` −v, `set` v − 0.5, nested `RunActionPack`
  followed, cycles ignored).
  Notes without events are never avoid-notes, whatever their skin.
- **Miss-free notes** (`NoteSkins.IsMissFree`, fork): **only avoid-notes**. When one passes the hit
  window it is not a miss (no `Misses++`, no combo break, no penalty); its `onMissEvent` still fires.
  (2026-11-20: notes that heal when pressed used to be miss-free too, but ordinary FMM notes often
  heal on press, so passed notes cost nothing; found in the first online playtest.)
- All misses (passed note, ghost tap with Ghost Tapping off, hold released before 25 %) go through
  `NoteRenderer.RegisterMiss`: miss count, combo break, −20 × multiplier, sound, `OnNoteMiss`, popup.
- `NoteRenderer.Botplay` presses every note except avoid-notes (player
  strumline only).
- Notes with skin 1/2 and avoid-notes keep drawing after passing the receptor.
- X / O overlays (drawn when the note style has no dedicated frame for the
  skin) follow the skin only: 1 → X, 2 → O.

### 8.6 Countdown, song end, results, transitions

- `CountdownOverlay` uses `level.Data.EffectiveBpm`; `songTime` is negative
  during countdown and reaches 0 when music starts.
- Multiplayer skips the local countdown and starts at the server timestamp.
- `LoadingScreen.Phase`: `Loading → Ready → Clearing → Done`.
  `LoadState`: `Idle → Loading → Finalizing → Done`.
- Multiplayer loading waits for both players ready **and** the server
  `CountdownStart` signal.
- **Fork fix (stuck "Waiting for Player N...")**: an `OpponentReady` that arrives while
  this client is still switching from the menu into the match was dropped
  (`OnOpponentReadyChanged` ignores it when `!_isMultiplayerMatch`, and
  `StartMultiplayerMatch` resets `_opponentReadyState`). The faster player then
  started and the slower one waited forever. Now `CountdownStart` also marks the
  opponent ready (`Game1.MarkOpponentReadyForStart`; the server only sends it once
  both are ready), and an `OpponentState` packet received while still on the loading
  screen (those are only sent after the opponent's song started) starts the match
  anyway (late, but playable). Also, the menu's 3-2-1 countdown
  (`MultiplayerMenu.AdvanceMatchCountdown`) now compares the server timestamp with
  `TimeSyncManager.GetServerTime()` instead of the local clock.
- **Level display name (fork)**: in gameplay `_level.LevelName` is overwritten in
  `FinalizeLoadedLevelForGameplay` with the menu name: the file / folder name
  (`Game1.FileDisplayName`), or `_multiplayerLevelName` in matches (official levels:
  the server name). So Freeplay, the online menu, loading screen, results, window
  title, Discord and match history agree; meta.json's `name` is no longer shown in
  gameplay (the Chart Editor still shows/edits it).
- Song end: when `_songTime >= _songEndTime` and chart/music are done, a
  1.5 s delay starts a `SongTransition`.
- `SongTransition.Phase`: `Filling → Full → Clearing → Done`.
- Single player transitions back to Freeplay; multiplayer shows
  `MultiplayerResultsScreen`.
- `RestartSong()` resets health/timers/notes, resets scene objects, seeks to 0.
- **Music ↔ chart sync (fork, 2026-11-23, `Game1.SongClock.cs` + `MusicPosition.cs`)**: the song
  clock is the Stopwatch (master: multiplayer start times and replays rely on it); the music is an
  OpenAL source that used to run unchecked, so an audio stall (Android hitch, underrun, the PCM copy
  of a timeskip running after the clock was already moved) desynced them for the rest of the song.
  `MusicPosition` reads / sets the source's `AL_SAMPLE_OFFSET` via MonoGame's internal
  `MonoGame.OpenAL.AL` (`GetSource`, `alSourcei`, `GetError`; `SoundEffectInstance.SourceId` /
  `HasSourceId`), bound once with compiled expressions; clears the AL error queue (a leftover error
  would make MonoGame's next checked call throw); if binding fails `Available` is false and the sync
  is off. `_musicInstStart` = song time where the current instance's buffer starts (0 after load,
  the seek time after `RecreateMusicAt`). `KeepMusicInSync` (per frame while playing, not paused,
  not ended) smooths `music − clock` (τ 0.2 s) and, past 45 ms, moves the music in place
  (`TrySet`) or recreates it at the clock time; 0.25 s hold after play / resume / seek; back-to-back
  resyncs (< 3 s apart) widen the tolerance ×1.5 up to 150 ms so coarse position reporting can't
  loop. `SeekSong` also seeks in place when the buffer covers the target (no PCM copy; works
  without `_musicPcm`, e.g. 32-bit matches), else `RecreateMusicAt`. Log tag `[Audio Sync]`.
  Verified on desktop OpenAL (read, set, set while paused, pitch/rate, out-of-range refusal).

### 8.7 Freeplay modifiers and personal bests (fork, `Game1.Freeplay.cs`)

- `ConfigureRunModifiers()` runs at the start of `SetupLevel` (so also on restart). Solo play only
  (not online, replays or Chart Editor playtests): `_runRate` = `FreeplayData.SongRate`,
  `_runEnemy` = `EnemyMode` and the level has ≥ 2 strumlines. `Game1.CurrentRunRate` /
  `CurrentRunEnemy` expose them (HUD status line shows `[RATE xN]` / `[ENEMY]`; the timer's
  "Song Name + Difficulty" stars use the side and rate).
- **Song rate:** `_songTime = stopwatch × rate`; `SeekSong` divides the target by the rate; the
  countdown, note renderers, events and scene animators get `dt × rate`; every music
  `SoundEffectInstance` gets `Pitch = log2(rate)` (`ApplyRunRateToMusic`, after each
  `CreateInstance`), so the pitch changes with the speed (Psych / osu! DT-HT style). 0.5–2.0x
  is exactly MonoGame's ±1 octave pitch range.
- **Enemy mode:** like online role 2 without being a match: strumline 0 (yours) gets chart 1,
  strumline 1 chart 0, character animations swap, misses use the tinted sing pose, health icons
  swap and mirror (also `BarIconChange`). **Level health effects are ignored** (`OnPlayerHealth` /
  `OnPlayerHealthSet` with `_runEnemy`): they target the player side (Bad Day drains health on a
  timer and sets the player's bar to full near the end, which killed the enemy player); only
  your hits / misses move the bar.
- **Hide-HUD events in enemy mode** behave as online (`SoftHudEvents` includes `_runEnemy`):
  the GUI event doesn't hide the HUD, judgements or combo; the strumlines only drop to 50 %.
  `GameplayHudOverlay.DrawHUD` takes `softHudEvents` for that.
- Replays aren't recorded with a rate other than 1.00x or in enemy mode (recorder invalidated
  "freeplay modifiers"; playback doesn't reproduce them).
- Modifier registry: `PlayModifiers.cs` (`PlayModifier`: id, name, description, category,
  on/off + optional value delegates, `Unavailable(levelPath)`, `BlocksRecords`). Add new ones
  there with their category; `ConfigureRunModifiers` is where gameplay reads them.
- **Attempts:** every solo run is saved as a `FreeplayAttempt` in `freeplay_data.json`
  (`FreeplayLevelRecord.Attempts`, oldest first, capped at 200 per level keeping each side's
  best): score, accuracy, misses, max combo, ratings, date, side (`Enemy`), `Rate`, `Failed` +
  `Progress` (game over: `OnSoloRunFailedForFreeplay`, called where the game over overlay opens)
  and `ReplayPath` (the replay saved for that run: `OnSoloSongFinishedForReplay` sets
  `_lastSavedReplayPath`, then `OnSoloSongFinishedForFreeplay` records the attempt). Quitting
  from the pause menu isn't an attempt. Not saved at all with botplay at any point
  (`_runAssisted`, set in `UpdateReplayRecording`) or after a timeskip (`SeekSong`). Runs at
  other rates are saved (tagged) but never count as the best. **Personal best** =
  `Best(side)`: highest score among finished 1.00x attempts (`Ranked`); a new one shows a
  "Nuevo record" toast back in Freeplay. Rank: S+ ≥ 99.5 %, S ≥ 95, A ≥ 90, B ≥ 80, C ≥ 70,
  else D; SFC / GFC / FC labels. Files of the first version (best only, `Player` / `Enemy`
  fields) are moved into `Attempts` on load.

---

## 9. Events and action packs

`EventProcessor` is constructed with `(FnmmLevel, SpriteAnimator[], texture
cache)`. It:
- initializes per-object opacity from `scene.json`.
- converts event times to seconds (mutating `level.Events.Events`).
- sorts events and processes them in order as song time advances.
- on seek, re-fires state-changing events up to the target time; pure
  `ScreenFlash`/`CameraShake` visuals are skipped during seek.
- tracks unknown event types in `_unknownTypes` and logs them once.

Action packs can be fired from note props. `PlayerHealth` supports operations
(`add`, `set`, etc.) with `minHealth`; `GUI` toggles HUD visibility via
`visible`/`value`; `ObjectChartChange` reassigns an object's strumline at
runtime; camera/tween events drive transforms.

`CorneredEventHandler` is a special exclusive handler enabled only for the
Cornered level UUID; it handles yellow-arrow/scrambled note behavior.

---

## 10. UI, menus, and settings

### 10.0 UI conventions (fork) — "..." options menus

**Use this pattern in every list that has per-row actions besides the main one.** A row's main
action stays on Enter / click (play, watch...); everything else lives in a **"..." options menu**:
- The selected / hovered row shows a "..." button (`UiDraw.MoreButton`). Clicking it, **right
  clicking the row**, or the **Options key** (`HotkeyAction.FreeplayOptions`, default `C`, alt
  Menu key; the name says "Options Menu (...)" because it is shared) opens `UI/OptionsMenu`
  next to the row: Up/Down + Enter/Space or mouse, Esc / Backspace / click outside closes.
- **Destructive actions never run from one key press**: give the item a `Confirm` text and the
  menu turns into "question / YES, <ACTION> / CANCEL" (CANCEL selected by default). A direct key
  (Delete) may open the menu straight at that confirmation (`Open(..., confirmFirst: "delete")`).
- Owner wiring: call `OptionsMenu.Update(...)` before your own input every frame (it returns
  true while it uses the input and hands back the chosen item id), `Draw` last.
- Used by: Freeplay levels (§10.1), Freeplay attempts window, REPLAYS (§12.4). **When adding a
  list or a row action anywhere else (online level list, rooms, leaderboard, editors' level
  lists...), add it here instead of a bare hotkey or per-row buttons.**
- Modal windows built the same way (self-contained class in `UI/`, drawn with `UiDraw`, owner
  calls `Open` / `Update` / `Draw`): `ModifiersWindow`, `AttemptsWindow`.

### 10.1 Main menu (`FreeplayMenu`)

Main menu cards (mouse hover/click or keyboard):
- Desktop: `FREEPLAY`, `MULTIPLAYER`, `REPLAYS`, `EDITORS`, `SETTINGS`, `CREDITS`.
- Android: `FREEPLAY`, `MULTIPLAYER`, `REPLAYS`, `EDITORS`, `SETTINGS`, `OPEN MODS`,
  `IMPORT FILE`, `GRANT FOLDER ACCESS`, `CREDITS`.
- (fork) `REPLAYS` (index 2, `MenuAction = 7`) opens `UI/ReplayBrowser` (§12.4). Indices of
  the later cards moved by one (`ExecuteMainMenuSelection`, `menuCount` 6 / 9).

`HUD EDITOR` is **no longer a main-menu item**; it is reached from `EDITORS`,
from Settings → GRAPHICS → "HUD Editor", or from the pause menu (desktop).

Main menu (root):
- Up/Down navigate (skip disabled items), Enter select.
- **Esc opens Settings** (`SettingsRequested`), it does not quit.
- `7` opens the Editor Hub (`EditorPickerRequested`).
- `U` (or clicking the corner badge) re-opens a dismissed update prompt.

Freeplay level panel (opened by `FREEPLAY`). **Redesigned in the fork (2026-11-21)**, split
into partial files (`FreeplayMenu.Browse/LevelPanel/Details/Preview/Icons/Difficulty.cs`):
- **Lists.** `_allPaths/_allNames` = every scanned level; `_paths/_names` = the visible list
  (search + favourites filter + order) that everything else indexes. `RebuildView(keepPath)`
  rebuilds it keeping the selection by path; `Refresh()` keeps the selected path too and, on
  the first scan, restores `FreeplayData.LastSelected` (saved on play and when the panel closes).
- Up/Down (also PgUp/PgDn/Home/End) select, Enter or click plays, wheel scrolls, Esc clears the
  search first, then closes. Keyboard keeps the selection centred; mouse hover selects without
  re-centring (`_manualScroll`).
- **Playing** a level from the list runs a 0.45 s confirm animation (row flashes, others fade,
  preview fades; StartSong sound at its start) before `LevelChosen` (`BeginConfirm`). The
  import modal's "SI, JUGAR" still sets `LevelChosen` directly. `Game1` calls
  `FreeplayData.MarkPlayed` when it consumes `LevelChosen` (play count, recent order).
- **Header:** LEVELS, count pill (`n/N SONGS` in cyan when filtered), close X; toolbar row:
  search box (click / `Ctrl+F` / `F3`; desktop types via `UI/TextEntry`, Android opens the
  system text dialog; Enter/Esc stop typing, Up/Down still move, Ctrl+Backspace clears;
  accent-insensitive substring match), SORT chip (`Tab`: A-Z, STARS, RECENT, MOST PLAYED,
  NEWEST = file creation/write time), FAVS chip (`Shift+F`, favourites only), RANDOM (`Shift+R`);
  then IMPORT / + NEW / REFRESH. `Game1` skips the volume hotkeys while `IsTypingSearch`.
- **Rows** (curved list: rows move right and fade with distance from the smoothed selection,
  slide in one after another when the panel opens): level icon, favourite diamond, name (the
  selected row scrolls a long name inside a scissor rect), personal-best rank badge, star pill,
  and on the selected / hovered row a "..." button. The old per-row EDIT / DEL buttons are gone.
- **Level options menu** ("...", §10.0): PLAY, ATTEMPTS (n), EDIT CHART, ADD/REMOVE FAVORITE,
  SHOW IN FOLDER (desktop, `PlatformShare.Reveal`), DELETE (its confirmation is the existing
  delete modal). `F` toggles favourite, `7` edits, `Delete` deletes, `H` attempts, `M` modifiers.
- **Details** (left side while the panel is open; the main menu cards fade out and their
  bounds are cleared): icon (bumps on the preview's beat), name, BPM / length (÷ song rate) /
  notes / "ENEMY CHART", star pill, best rank + SFC/GFC/FC, note-density graph (current side
  in the star colour, other side faint, white line = preview position, peak NPS), tiles (taps,
  holds, other side's notes, strumlines; dropped first on short windows), **personal best card**
  (best finished 1.00x run of the side: score, accuracy, misses, max combo, "replay" if it has
  one; attempts / plays / last played; click it or `H` → attempts window) and a **modifiers bar**
  (chips of the active modifiers + "Best scores and replays aren't saved" when one blocks
  records; click it or `M` → modifiers window).
- **Attempts window** (`UI/AttemptsWindow`): every attempt of the selected level for the
  current side, newest first; rank (F for a game over), score, BEST / FAILED AT n% / combo label
  / rate tags, accuracy, misses, max combo, S/G/B/Sh, date, WATCH (if the replay file still
  exists, else "NO REPLAY"; checked once when the window opens) and "..." (WATCH REPLAY, DELETE
  ATTEMPT with confirmation; the replay file is kept). Enter / click / WATCH sets
  `FreeplayMenu.ReplayRequested`; `Game1` loads it with `ReplayStore.Load` + `StartReplay` (a
  finished replay returns to Freeplay). Wheel / Up / Down scroll.
- **Modifiers window** (`UI/ModifiersWindow`, `M`, the modifiers bar; self-contained so other
  screens can open it later): the registry `PlayModifiers.All` grouped by category in a 2x2 grid
  — **Difficulty reduction**: Slower (0.50–0.95x), **Difficulty increase**: Faster
  (1.05–2.00x), **Automation**: Autoplay (= `NoteRenderer.Botplay`, also Settings → Practice,
  saved in settings.json), **Fun**: Enemy Mode (greyed with a reason on levels without an enemy
  chart). Empty categories show "Nothing here yet". Slower / Faster are one song rate
  (`FreeplayData.SongRate`; switching one on replaces the other) and each remembers its rate
  (`SlowRate` 0.75 / `FastRate` 1.25 by default). Up/Down/Tab select, Enter/Space toggle,
  Left/Right change the value (or switch off/on), R resets all, Esc / M / CLOSE / click outside
  close. Left/Right in the level list still nudge the rate by 0.05. `PlayModifiers.Changed`
  makes the list re-rate the stars. Star ratings, length and NPS follow the side and rate.
  Gameplay side: §8.7.
- **Star rating:** `ChartDifficulty` is a port of osu!mania's difficulty calculator (Strain
  skill: per-column + overall strain, 400 ms sections, 0.9 weighted peaks, ×0.018; lanes =
  columns, holds = long notes). `Calculate(notes, columns, clockRate)` divides note times by the
  rate (like osu! DT/HT). `FreeplayMenu.Difficulty.cs` analyses each level on a background task
  after `Refresh()` via `FnmmLoader.TryLoadChartData` (data.json only), cached by path + last
  write time (`LevelInfo`/`SideInfo`: notes, stars at 1.0x, taps, holds, peak NPS, 72-bucket
  density, first note, length). Other rates: `QueueRatedStars` fills `_ratedStars` in the
  background (the 1.0x value shows meanwhile). The STARS order re-sorts as ratings arrive.
- **Audio preview** (`FreeplayMenu.Preview.cs`): after the selection rests 0.35 s, only
  `music.ogg` is read (`FnmmLoader.WithLevelFiles`) and streamed with NVorbis on a
  `DynamicSoundEffectInstance` from 0.15 s before the first note, looping back there; 0.6 s
  fade in, 0.3 s fade out, volume = `Game1.SongPlaybackVolume`. `Game1` calls
  `UpdatePreview(dt, allowed)` every frame from `Update`'s finally block (allowed = in Freeplay,
  no loading screen / error), so leaving Freeplay stops it. RAR packages have no preview.
- **Icons** (`FreeplayMenu.Icons.cs`): for rows on screen, a worker reads scene.json
  `icons` and the sprite's `sprite.json` + `0.png`, cuts frame 0 (`num` frames per strip, all
  types), trims transparent borders, shrinks to 112 px and the game thread makes the texture
  (≤ 160 kept, least recently drawn freed). Shown icon = the **opponent's** (icons[0], as in FNF
  freeplay; the player is usually BF), else the player's, else a badge with the initials in a
  colour from the name. Blank icons (1x1 / empty sprites some levels use) count as none.
- **Background** tinted towards the selected level's star colour, with a bottom glow and grid
  that pulse on the preview's beat.
- `R` / refresh button rescans (toast "Lista de niveles actualizada").
- `I` or `O` / import button / footer bar / empty-list drop box opens the
  import picker (`TriggerFileImport` → `PlatformPaths.PickDesktopFiles`:
  Win32 dialog on Windows, zenity/kdialog on Linux; copies into `mods/levels`).
  Imports clear the search and the favourites filter so the new level is listed.
- `N` / "new" button → Level Selector with the new-chart modal open.
- **Fork:** `Delete` or DELETE in the options menu opens the import modal in
  delete mode (`_modalIsDelete`; "BORRAR NIVEL", default NO, `Y` yes, `S` does not
  confirm here). `LevelFiles.Delete` sends it to the Recycle Bin (Windows), the Trash
  via `gio trash` (Linux, if available) or deletes it (otherwise / Android); only paths
  inside the levels folder are accepted. `LevelsDeleted` tells `Game1` to refresh the
  Level Selector; its Freeplay data is forgotten (`FreeplayData.Forget`).

Imports (drag-and-drop or picker) call `OnLevelsImported`: refresh, open the
panel, select the new level, show a toast, and open a "play now?" modal
(Left/Right toggle, Enter, `Y`/`S` yes, `N`/Esc no).

Credits: fade-in screen with clickable links; Esc/Enter/click closes.
(2026-11-20) Content: developers ankush (@thisisankush; also commits as Thisisfenix / AngelJDevex)
and AbelitoGamer; FMM by KingamesCreator (store links); online menu music from the Duck Game OST;
UI sounds from Mii Maker / Tomodachi Life (Nintendo); Icons8; osu!mania star rating (ppy/osu, MIT);
libraries. The text shrinks to fit small windows. Edit `creditData` in `FreeplayMenu.cs`.

`Game1` dispatch: `SettingsRequested` → settings overlay; `EditorPickerRequested`
→ `EnterEditorPicker`; `NewChartRequested` → `LevelSelector(startCreateModal:
true)`; `ChartEditorRequested` → `OpenChartEditor(path, Freeplay)`;
`LevelChosen` → `StartLoadingLevel`. `Game1` reads main-menu actions once per
frame with `ConsumeMenuAction()` (fork): `3` multiplayer, `4` import file
(Android), `5` open mods (Android), `6` SAF help + folder picker (Android).
The old `1` (HUD editor launcher) branch was removed — no card sets it.

### 10.2 Pause menu

ESC/P toggles pause in solo play (in matches only Esc works and the song keeps
running, see §5.3). Items by context:
- Desktop solo: `Resume`, `Restart Song`, `HUD Editor`, `Settings`,
  `Exit to Menu`.
- Android solo: `Resume`, `Restart Song`, `Settings`, `Exit to Menu`.
- Multiplayer: `Resume`, `Settings`, `Exit Match`.

`PauseAction`: `Resume`, `RestartSong`, `OpenHudEditor` (returns to the pause
menu afterwards), `OpenSettings`, `ExitToMenu`. Restart and HUD editor are
ignored in matches. Losing window focus auto-pauses solo gameplay.
Timeskip scrubbing sets `_pausedByTimeskip`; on resume it can auto-resume.

### 10.3 In-game settings (`SettingsManager`)

- (All hotkeys below are rebindable now, see "Rebindable hotkeys"; defaults shown.)
- Scroll speed hotkeys in a song (fork): **F3** −0.10x, **F4** +0.10x (same
  range as the setting, 0.25–5.00x), with a "SCROLL SPEED" toast
  (`DrawScrollToastOverlay`); the older PageUp/PageDown (±0.25x) now shows the
  toast too. F3/F4 work when a level is loaded, **including while paused** (pause
  menu open), but not with other menus/settings open. PageUp/PageDown only unpaused.
- Graphics → **Lane Underlay** (fork, `NoteRenderer.LaneUnderlay`, saved as
  `LaneUnderlay`, 0–0.75, step 0.05, default 0 = off): full-height black column
  behind the player strumline's lanes (+8 % margin). Drawn by
  `Game1.DrawLaneUnderlay` → `NoteRenderer.DrawLaneUnderlay` (player renderer,
  `HudRole == 0`) at the start of the HUD batch: above the stage, below every
  HUD element and the notes (also in the HUD editor preview). Follows HUD
  offset, position and strumline alpha.
- Gameplay → **Show Key Labels** (fork, `NoteRenderer.ShowKeyLabels`, saved as
  `ShowKeyLabels`, default OFF): the key names on the player's strums used to be
  always drawn.

Rebuilt in 0.4.7 as a data-driven list (`SettingItem` with `Label`, `Value`,
`Kind`, `Description`, `OnSelect`/adjust callbacks; `BuildTabEntries`). Opened
from the main menu (Esc or `SETTINGS`), the pause menu, and the multiplayer
menu (`GameplaySettingsRequested`).

Tabs (same on all platforms; some rows are desktop-only):

| Tab | Rows |
|---|---|
| `GAMEPLAY` | Downscroll, Middlescroll, Scroll Speed (0.25–5.00×), Ghost Tapping; *Practice*: Botplay, Timeskip (OFF / 5–60 s; `TimeskipSeconds` 0 = off) → Seek Current Song; Advanced Timing [Show/Hide] → Input Buffer, Safe Frames |
| `NOTES` | Note Style, Note Splash, Countdown Style; *Readability*: Lane Underlay, Show Key Labels; *Strumlines*: Strumlines → Player / Opponent / Extra; *Effects*: Arrow Effects → Effect Type |
| `HUD` | HUD Editor (desktop), Show HUD → Combo & Judgements, Score & Stats, Health Bar → Health Icons (Move with bar / Pinned to ends), Song Title, Botplay/Speed Indicator, Song Timer → Position, Color, Text, (automatic positions only) Force Top |
| `AUDIO & VIDEO` | *Audio*: Master Volume → Music Volume, Menu Music Volume, SFX Volume; *Display*: Fullscreen (desktop), VSync, Render Resolution (Native/720p), FPS Cap, FPS Counter, Font Style (shows current font file); *Other*: Debug Info; *Storage* (desktop): Move Mods Folder |
| `CONTROLS` (fork) | Note Keys (desktop, opens `KeyBindingOverlay`) / Touch Hitbox Colors (Android), hotkeys grouped by `Controls.Info.Group` (Anywhere, In a song, Menus, Online), Reset Controls |

Indented rows only appear when their parent is enabled. Italic names are section
headers: `SettingKind.Header` rows (`Header(title)`), drawn by `DrawSectionHeader`,
never selectable; `MoveSelection(game, dir)` (Up/Down in `Game1`) and
`BuildTabEntries` skip them. Tab constants: `GameplayTab` 0, `NotesTab` 1,
`HudTab` 2, `AudioVideoTab` 3, `ControlsTab` 4.

Settings cleanup (fork): removed Color Grading (and `Content/ColorGrade.fx`),
Camera Zoom (and `CameraManager.Increase/DecreaseZoom`), Extended Camera (and the
MonoGame.Extended `OrthographicCamera` path in `CameraManager`), Camera Follow
Player (and its `EventProcessor` block), Bilinear Filter (did nothing), Arrow
Effect Intensity/Chance sliders (fixed at 1.0 / 25 %), and the unused online
fields (`MatchFoundSound`, `ChatNotifications`, `ReadyAlerts`, `AllowMatchInvites`,
`ShowOnlineStatus`, `AnonymousMode`, `BandwidthLimitKbps`). `NoteRenderer.Autoplay`
is now `NoteRenderer.Botplay` (also `DisableBotplay`, `UpdateBotplay`). Master Volume
now scales every sound, Chart Editor music / hitsounds / metronome included.

Online settings overlay (`MultiplayerMenu`, `% 4` categories): "Show Opponent
Notes" is the same setting as Opponent Strumline — `MultiplayerSettings.ShowOpponentNotes`
is a `[JsonIgnore]` pass-through to `!Game1.HideOpponentStrumline`, saved in
settings.json (`Game1.SaveSettings()`). "Ghost Transparency" is labelled "Opponent
Notes Opacity" (field still `GhostTransparency`). The F1 pill reads "<key>  Settings".

#### Rebindable hotkeys (fork, `Controls.cs`)

`HotkeyAction` enum + `Controls.All` (label, description, default primary and
alternate `KeyBinding`). A `KeyBinding` is a key plus required Ctrl/Shift/Alt.
`Controls.Pressed/Down(action, kb, prev, ignoreKey?)`: a binding matches when its
key is down and its modifiers are held; extra modifiers are allowed unless a more
specific binding on the same key also matches (Shift+T beats T, Ctrl+Right beats
Right). Saved in settings.json as `Hotkeys` (action name → ["Ctrl+Shift+T", ...],
`None` = empty); missing actions get defaults. `Controls.Hint(action)` feeds
on-screen hints (freeplay header buttons/footers, main-menu hint, update badge,
Editor Hub hint, Scroll Speed / Fullscreen descriptions).

| Action | Default | Used in |
|---|---|---|
| Toggle Fullscreen | Alt+Enter / F11 | Game1 (desktop; then keys withheld until released) |
| Volume Up / Down | = / Num+ , - / Num- | Game1 global |
| Scroll Speed ∓0.10 | F3 / F4 | Game1, in a song incl. paused |
| Scroll Speed ∓0.25 | PgDn / PgUp | Game1, playing only |
| Pause / Resume | P | Game1 (also dismisses error screen) |
| Timeskip Forward / Backward | T, Ctrl+Right / Shift+T, Ctrl+Left | Game1 (hold repeats; unmodified lane keys ignored) |
| Retry (Game Over) | R | `GameOverOverlay` |
| Open Chart Editor | 7 | Game1 in song, `FreeplayMenu` (hub / edit selected), `EditorPicker` |
| Previous / Next Tab | Q / E | settings, `MultiplayerMenu` tabs/categories, `ReplayBrowser` |
| Refresh | R / F5 | freeplay, online level list, online leaderboard |
| New Chart | N | freeplay |
| Import Level | I / O | freeplay, online level list |
| Share Level | S | online level list |
| Delete | Delete | freeplay, online level list, replay browser |
| Show Update Prompt | U | freeplay main menu |
| Online: Open Settings | F1 | `MultiplayerMenu` (opens this settings screen) |
| Online: Reset Settings | R | online settings screen |

Not rebindable on purpose: menu navigation (arrows, Enter, Space, Esc, Backspace),
Y/N in confirm dialogs, text entry, the Chart Editor and HUD editor shortcuts.

Rebinding: Enter/click a hotkey row → "Press key..."; the one key pressed (with any
held Ctrl/Shift/Alt) replaces the action's binding **including the alternate slot**
(defaults may still show two keys until rebound). Esc cancels, Backspace = no key.
Modifiers alone are ignored; bare arrows/Enter are refused.

Layout keys (`LayoutKeyboard.cs`, fork): MonoGame DesktopGL 3.8.6 maps SDL key
**codes** (layout characters) with `KeyboardUtil.ToXna` and returns `Keys.None` for
non-ASCII ones (Spanish ñ, ç, ´, º, ¡, ¿), so those keys never reached the game.
`LayoutKeyboard.GetState()` (used everywhere instead of `Keyboard.GetState()`)
adds them from SDL's physical key state (`SDL_GetKeyboardState`, loaded from the
SDL2 library MonoGame ships). Each such key gets its US-position `Keys` (e.g. Ñ →
`OemSemicolon`) if the layout doesn't already produce it, else a spare value
(`Oem8`, `OemAuto`, …). `Controls.KeyName` shows the layout character ("Ñ");
`Controls.AsciiKeyName` strips accents for the ASCII-only SpriteFont (Ñ → N), used
by the note-key overlay and strum key labels. `DefaultFont.spritefont` now has
`<DefaultCharacter>?</DefaultCharacter>` so unsupported characters never throw.
Verified on a Latin American layout: adds ¿=OemPlus, ´=OemOpenBrackets,
Ñ=OemSemicolon. Android returns `Keyboard.GetState()` unchanged. While capturing (and
until all keys are released afterwards) Game1 gives the rest of the game an empty
keyboard (`_keyCaptureHoldoff`); closing settings cancels a capture. Android touch
buttons P/Q/E are mapped to the Pause / Prev / Next Tab primary keys
(`MergeTouchWithKeyboard`) so rebinding doesn't break them.

#### FPS cap and fullscreen (fork)

- **FPS Cap** (GENERAL, saved `FpsCap`): Unlimited, 30, 60, 75, 90, 120, 144, 165,
  180, 240, 360. The loop stays variable (`IsFixedTimeStep = false`); `Game1.EndDraw`
  → `WaitForFrameSlot()` waits after each present until the next slot (Stopwatch
  schedule; `Thread.Sleep` for all but ~1.5 ms, then spin; schedule restarts if a
  frame runs over a whole period). `ApplyFpsCap()` raises the Windows timer to 1 ms
  (`timeBeginPeriod`). MonoGame's fixed time step was used first and was unreliable
  (coarse sleeps + catch-up updates). Works together with VSync.
- **Render Resolution** (AUDIO & VIDEO → Display, saved `NativeRenderResolution`,
  default `true` = "Native", `false` = "720p"): see §5.4.
- **Fullscreen** is now a real borderless window (`Game1.SetFullscreen`):
  `IsFullScreen = false`, `Window.IsBorderless = true`, back buffer = display width ×
  (display height **+ 1**, the extra row off-screen), position 0,0. The +1 keeps the
  driver / Windows fullscreen optimizations from promoting the GL window to "real"
  fullscreen, which froze window capture (OBS) on one frame; leaving restores 1280×720 at the previous window position. SDL's
  own fullscreen (used before, even with `HardwareModeSwitch = false`) minimizes on
  focus loss and behaved like exclusive fullscreen. Fullscreen state is still not
  saved (starts windowed). Not yet checked in game.

Controls: `Q`/`E` switch tabs (also Android virtual buttons); Up/Down
navigate; Enter/Space toggles or runs an action, or focuses an adjustable row;
**Left/Right adjust the focused row, otherwise switch tabs**; full mouse
support (click tabs, rows, `<`/`>` arrows, wheel); Esc back.

Settings are persisted by `Settings` (desktop `%AppData%/FunkyMakerPlayer/
settings.json`; Android app-specific `settings.json`). `Settings.Load(Game1)`
copies values into static bridges:

| Static target | Fields |
|---|---|
| `NoteRenderer` | `Botplay` (JSON `Botplay`; legacy `Autoplay` still read), `Downscroll`, `ScrollMultiplier`, `Midlescroll`, `GhostTapping`, `InputBuffer`, `SafeFramesTime`, `PlayerKeys[0..3]` |
| `NoteRenderer` (new in 0.4.7) | `ShowStrumlines`, `HidePlayerStrumline` |
| `Game1` | `ShowTimer`, `TimeskipSeconds` (0 = off; `TimeskipEnabled` is now a computed property; legacy JSON `TimeskipEnabled: false` loads as 0), `TimerColorPreset`, `TimerPreset`, `TimerTextMode` (adds "Song Name + Difficulty": star rating via `ChartDifficulty`, cached per level in `GameplayHudOverlay.StarsFor`), `TimerForceTop`, `ShowDebugInfo`, `ShowFpsCounter`, `MasterVolume` (JSON `MasterVolume`; legacy `MusicVolume` still read), `SongVolume` (song music; the song instance uses `SongPlaybackVolume` = master × song), `NoteStyleName`, `NoteSplashStyleName`, `HideExtraStrumlines`, `HideOpponentStrumline`, `ShowCombo`, `IconMode` (0 move with bar, 1 pinned to ends), `SelectedCountdown`, `HideTouchHitboxColors` |
| `ArrowEffectManager` | `Enabled`, `SelectedEffect` |

`Settings.Save(vsync)` writes all values back. Key bindings are stored as
`Keys` names and require an array of length 4.

### 10.4 HUD editor

Strumline offsets (fork): the editor's Player/Opponent strumline boxes now save
`HudSettings.PlayerStrumlineOffset` / `OpponentStrumlineOffset` (1280×720 px,
relative to the boxes' base positions 740,60 / 100,60). `NoteRenderer.HudRole`
(0 player, 1 opponent, set in `Game1` strumline setup) applies the offset at
draw time on top of the level/event position. Before, positions were hardcoded
and never saved. Verified in game with a test offset.

Resizing (fork): the selected element has a corner handle (drag), and `[` / `]`
or the mouse wheel over it shrink/grow it. Health Bar / Timer Bar change their
`Size` (width and height); every other element has a uniform scale (0.25–4.00,
step 0.05) saved in `HudSettings` as `ScoreScale`, `MissesScale`, `AccuracyScale`,
`SongInfoScale`, `StatusInfoScale`, `ComboScale`, `JudgementScale`,
`OpponentIconScale`, `PlayerIconScale`, `PlayerStrumlineScale`,
`OpponentStrumlineScale` (default 1; old json files load as 1). Used by
`GameplayHudOverlay` (text scale), `HealthBar` (icon size, base 100 px; in
follow-the-bar mode the icon stays centred 10 px below the bar top),
`JudgementOverlay.AddJudgement/ShowCombo(…, sizeMul)` and `NoteRenderer`
(strumline). Strumline boxes are centre-anchored: the saved offset is now the box
**centre's** movement from its default centre (identical to the old top-left
offset at scale 1). Multiplayer HUD text does not use the scales. Not yet
checked in game.

Entry points: Editor Hub → "HUD Editor" and Settings → GRAPHICS → "HUD Editor"
(desktop) both go through the `MainSettingsMenu` launcher screen (ENTER opens);
the pause menu (desktop) opens the editor directly and returns to pause.
`HudEditorReturnTarget` records where to go back to. `HudSettings.ForceReload()`
runs before opening.

- `Esc` saves and goes back, `S` save, `R` reset defaults (positions **and**
  visibility), `G` grid, `H` instructions.
- `A` toggles all elements (master HUD toggle); `V`, Space or Delete toggles
  the selected element's visibility; right-click selects + toggles.
- Click-drag elements; arrow keys nudge (Left Shift = 1 px, otherwise 5 px);
  Tab cycles elements.
- Saves through `HudSettings.Instance.Save()` → `Assets/data/hud_settings.json`
  (Android: app internal storage, next to settings.json; see §15).
- (fork) Reads the pointer with `PointerInput.GetRawState()`: on Android a finger held down is
  the left button, so elements can be dragged / resized by touch. The keyboard-only actions
  (S / R / G / H / A / V) still have no touch buttons; ESC = the back gesture saves and leaves.

### 10.5 Overlays

`ArrowsMenu`, `NoteSplashMenu`, `CountdownMenu`, `FontMenu` are opened from
settings/results and drawn after the letterboxed blit. (fork, 2026-11-23) They share
`UI/PickerTouch`: layout scales with `sh / 720` (header, rows; they were fixed pixels, 38 px
rows on a 1080 px phone), each row drawn is registered for hit tests, the header has BACK and
APPLY buttons, and `Poll(Selection)` turns clicks / taps into: a row = preview it, the previewed
row again or APPLY = apply (same as Enter), BACK = cancel (same as Esc). Works with the mouse
on desktop too. `KeyBindingOverlay`
handles rebinding the four lane keys. `ModsHelpScreen` is Android-only.

### 10.6 UI sound effects (fork)

Menus and editors play short sound effects through `UI/UiSfx.cs`.

- **Files:** `Assets/sounds/ui/*.wav` (46 files, snake_case names, 16-bit PCM
  mono). They came from a `miiMakerTomo/` drop folder; `slider_end/tick/
  tick_down` were MP3s with a `.wav` extension and were converted to real WAV
  (MonoGame `SoundEffect` only reads PCM WAV). Unused for now: `boing`,
  `cursor_steady`, `hover`, `hover2`, `scale_up/down`, `vert_stretch_up/down`.
- **API:** `UiSfx.Play(UiSound.X)` queues a semantic sound (thread-safe).
  `UiSfx.Flush()` runs once per frame at the end of `Game1.Update` and plays
  only the **highest-priority** request of that frame, so one input that
  triggers several reactions (menu confirm + screen opening) makes one sound.
  `UiSfx.Frame` counts frames for menus that keep a snapshot across frames.
- **Loading:** lazy, on first use. Path `<assets root>/sounds/ui/<name>.wav`
  (`UiSfx.Initialize(FindAssetsRoot())` in `LoadContent`); if missing (e.g.
  Android before asset extraction finishes) falls back to
  `TitleContainer.OpenStream("Assets/sounds/ui/<name>.wav")` (APK assets).
  Failures are logged once (`[UiSfx]`) and cached.
- **Volume:** `Game1.MasterVolume` (the "Master Volume" row / `-` `+` hotkeys)
  × `UiSfx.Volume` (**SFX Volume** row in Settings → AUDIO & VIDEO; `SoundFiles` (countdown/miss sounds) uses it too, persisted as
  `SfxVolume`, default 0.8) × a per-sound gain in `UiSfx.Defs`.
- **How sounds are triggered** — mostly by *state diffs*, not by input paths,
  so keyboard, mouse and touch behave the same:
  - `Game1.UiSfx.cs`: snapshot before `UpdateInternal`, compare after. Screen
    depth (Freeplay 0 → Multiplayer/Editor Hub/gameplay 1 → Level Selector/
    HUD launcher 2 → Chart/HUD editor 3) gives Confirm (deeper) or Back
    (shallower); also Settings/Pause/Game Over/Key binder/style pickers/
    loading screen (step ticks, ready, start) and error screens.
  - `FreeplayMenu` and `MultiplayerMenu` wrap `Update` → `UpdateCore` and
    diff a `SfxState` record kept **across frames** (so background-thread
    changes like the update prompt or network "match found" are heard).
  - `LevelSelector` wraps `Update` with a per-call diff (list, new-chart modal,
    typing, BPM, errors).
  - Explicit calls in action methods: `SettingsManager` (toggle/action,
    slider tick or `slider_end` when the value did not change),
    `ChartEditor` (tools, strumlines, snap/zoom/speed/BPM, hold length,
    undo/redo, play, playtest, save/fail, delete, move, copy/paste, mirror,
    presets, toggles), `HudEditor` (grab/release, nudge on key press, Tab
    cycle, toggles, save, reset), `EditorPicker`, `ModsHelpScreen`,
    `MultiplayerResultsScreen`, song/match finished, volume hotkeys, toasts.
- **Mapping** (`UiSound` → file): Move `cursor_move`, MoveList
  `cursor_panel_move`, MoveRow `cursor_normal_move`, Tab `change_tab`, SubTab
  `select_tab`, Confirm `press`, Back `back`, Toggle `select_misc`, Focus
  `cursor_press`, Slider `slider_tick(_down)`/`slider_end`, Error `no_click`,
  AlreadyInUse `select_already_in_use`, StartSong `select`, StyleSelect
  `select_style`, ColorSelect `select_color`, EditorSelect `select_part`,
  Notice `notice`, Receive `receive`, Wait `wait`, Save `finish`, Delete
  `delete`, Undo `undo`, Type `keyboard_ok`, TypeFull `keyboard_full`,
  Nudge `position_up/down`, `move_together/apart`, Grab/Release
  `start/stop_rotation`, Cycle `rotate_cw/ccw`, LoadTick `load1-4` (random).
- The Chart Editor's hitsound/metronome are separate (synthesized tones).
- Gameplay itself (note hits/misses) has no UI sounds.

### 10.7 Online menu music (fork)

- **Files:** `Assets/music/online/portraits_of_kings.mp3` (main loop, 2:58)
  and `jazzroom.mp3` (1:02), 44.1 kHz stereo MP3, decoded at runtime with the
  `NLayer` package (pure C#), streamed through a `DynamicSoundEffectInstance`
  in 0.1 s buffers (3 queued) — no full decode in memory. Loaded lazily on the
  first visit; same desktop path → `TitleContainer` (APK) fallback as UiSfx.
- **Class:** `Managers/OnlineMenuMusic.cs`, driven from `Game1.Update`'s
  `finally` (`UpdateOnlineMenuMusic`).
- **Rules:** "in online menu" = `_inMultiplayer`, or the main settings / HUD
  editor opened from it (`_hudEditorReturnTarget == MultiplayerSettings`).
  Portraits of Kings plays there (fade-in 1.5 s). While
  `MultiplayerMenu.IsConnectingScreen` (`MenuState.Connecting`) Portraits
  fades out in 0.35 s and pauses (keeps its position), and Jazzroom restarts
  from the top (0.15 s fade-in) and loops; leaving that screen fades Jazzroom
  out (0.35 s) and Portraits back in. Starting a match / leaving the menu
  fades everything out (0.35 s); Portraits resumes where it was next time.
- **Volume:** `Game1.MasterVolume` (master) × `OnlineMenuMusic.Volume` ×
  fade. `OnlineMenuMusic.Volume` is the **Menu Music Volume** row in Settings →
  AUDIO & VIDEO (default 70%, step 5%, persisted as `MenuMusicVolume`). Applied live
  every frame, so slider/hotkey changes are heard immediately.

---

## 11. Editors

`EditorPicker` ("Editor Hub"; opened by the `EDITORS` card or `7` on the main
menu) lists four working entries (arrows/mouse, Enter/Space, Esc):

| Entry | `EditorType` | Opens |
|---|---|---|
| New Chart | `NewChart` | `LevelSelector` with the create modal open |
| Chart Editor | `Chart` | `LevelSelector` → `ChartEditor` |
| HUD Editor | `Hud` | `MainSettingsMenu` launcher → `HudEditor` |
| Character Editor | `Character` | `CharacterEditor` (`Editors/Character/CharacterEditor.cs`) |

The other `EditorType` values (Stage, Alphabet, Sprite, Event, Week,
Dialogue) remain in the enum as upcoming tools.

`IEditor : UI.IDirectScreen` (drawn directly to the window):
`Update(float dt, KeyboardState kb)`, `bool BackPressed`, `bool IsModified`.

### Level Selector / New Chart

- Lists `.fnmm`, `.zip`, `.rar` and unpacked folders in `mods/levels`; mouse and
  keyboard.
- "CREATE NEW CHART / LEVEL" modal: name, BPM, optional `.ogg` (Windows file
  dialog). Writes `mods/levels/<name>.fnmm` (adds `_2`, `_3`… if it exists)
  and opens it in the Chart Editor. Default name `New Chart N`.

### Chart Editor

Redesigned in 0.4.7: timeline scrubber, clickable toolbar (Play, Playtest,
Undo, Redo, Preview toggle, Save, Back), 8-column grid (left block = a
non-player strumline, right block = strumline 0), docked stage preview that
renders the real scene through `FnmmSceneRenderer` (characters sing on
playback), hitsounds and metronome, toasts, UI scaled with `UiScale`.

| Input | Action |
|---|---|
| `1`–`4` | Tool: Select / Place Note / Place Hold / Delete |
| `Q` / `W` | Focus right strumline (0, BF) / left strumline |
| `Tab` / Shift+Tab | Cycle which strumline (1..N-1) is on the left block |
| `D` `F` `J` `K` | Step input: toggle a note on lanes 0–3 at the playhead |
| `Z` / `X` | Snap divisor down / up (1/4 … 1/32, default 1/16) |
| `[` / `]` | Shorten / lengthen selected hold |
| `T` | Cycle the selected note's preset, or the brush preset (Normal → Hurt → Instakill → Heal) |
| Left / Right | Move selected note across the 8 columns |
| Delete / Backspace | Delete selected note |
| `M` | Mirror current measure |
| `B` | Open/close **Scene Characters & Icons** modal (`CHARS (B)` button) |
| Ctrl+C / Ctrl+V | Copy current measure / paste at playhead |
| Ctrl+Z / Ctrl+Y | Undo / Redo |
| `V` | Show/hide stage preview |
| `C` | Cycle stage-preview camera |
| `H` / `N` | Toggle hitsounds / metronome |
| Up / Down (Shift = faster) | Scroll (when not playing) |
| PageUp / PageDown / Home | Jump 4 beats back / forward / to start |
| `+` / `-` (also numpad) | Grid zoom 0.50×–2.50× |
| `O` / `P` | Chart scroll speed −/+ 0.1 |
| Space | Play/pause |
| Enter / Shift+Enter | Playtest from playhead / from start |
| Ctrl+S | Save |
| Esc | Pause playback and go back |

- **Note presets** (fork; toolbar "NOTE PRESET" 2×2: `NORMAL`, `HURT X`,
  `KILL X`, `HEAL O`, inspector button `PRESET (T)`). Skins are visual, so a
  preset = skin + `onPressEvent` action pack:

  | Preset | skin | onPressEvent pack | onMissEvent |
  |---|---|---|---|
  | Normal | 0 | none (-1) | -1 |
  | Hurt | 1 (negative) | `PlayerHealth remove 6, minHealth 0` (20%) | -1 |
  | Instakill | 1 (negative) | `PlayerHealth remove 30, minHealth 0` (100%) | -1 |
  | Heal | 2 (positive) | `PlayerHealth add 5, minHealth 0` (~17%) | -1 |

  Values are on the FMM 0–30 health scale.

  `EnsureHealthPack` reuses an existing pack that contains exactly that one
  action, otherwise appends one to `events.action_packs` (toast "Added action
  pack #N"); appended packs are not removed by undo (harmless if unused).
  Cycling a note whose skin/events match no preset ("custom") jumps to Normal
  and replaces them (undoable). The inspector shows the preset name, or
  "`<Skin>` skin | press APn | miss APn" for custom notes. Copy/paste keeps
  skin **and** both event indices.
- The editor itself applies no gameplay effect; skins only change the drawing
  (red X / cyan O tint when the note style has no dedicated frame).
- Save: see §6.8 (normalizes the path to `.fnmm`, backs up the previous file,
  writes a flat ZIP via `FnmmSaver`).
- Playtest: `Game1.BeginEditorPlaytest` starts gameplay at
  `PlaytestStartSeconds`, reusing the editor's cached textures and PCM for an
  instant start; `7` during the playtest calls `ReturnFromPlaytest` and goes
  back to the editor.
- From gameplay (solo, level loaded) `7` opens the Chart Editor for the
  current level (`ChartEditorReturnTarget.Gameplay`).
- **Scene Characters & Icons modal (`ChartEditor.Characters.cs`, `CHARS (B)`):**
  assigns any character from `mods/characters/` onto **Player (`SL0`)**, **Opponent (`SL1`)**,
  or **Extra (`SL2+`, with `+ NEW SL`)**, fine-tunes scene `X` / `Y` / `Scale` / `Flip`,
  optionally updates `scene.Icons`, and can suspend `ChartEditor` to launch `CharacterEditor`
  directly (`+ CHARACTER EDITOR`) and return cleanly on `Esc`. Saved `.FNMM` files embed
  standard `sprites/<name>/sprite.json` + `0.png..N.png` strips for 100% FunkyMaker compatibility.

### Character Editor (`Editors/Character/`, 2026-11-25)

- **Supported input formats (`CharacterLibrary.ImportAny`):**
  1. **Sparrow v2 (`PNG + XML`, `SparrowCharacterImporter.cs`):** handles trimmed `frameX/frameY/frameWidth/frameHeight` and `rotated="true"` subtextures (`Rotate270`), plus automatic detection of companion **Psych Engine `.json`**, **V-Slice `.json`**, or **Codename `.xml`** definitions (`animations`, `indices`, `offsets`, `fps`, `loop`, `scale`, `flip_x`, `no_antialiasing`, and nearby `icon-*.png`).
  2. **Adobe Animate Texture Atlas / Spritemaps (`SpritemapCharacterImporter.cs`):** software rasterizer supporting `Animation.json` (both compact `AN/SD/TL/L/FR/E/ASI/SI/M3D/MX` and verbose keys, plus `STI` stage instances) and one or multiple `spritemap*.json` + `spritemap*.png` limb sheets, frame-label timelines, symbol dictionary (`addBySymbol`) timelines, loop modes (`LP`/`PO`/`SF`), color/alpha transforms, and premultiplied bilinear limb sampling.
  3. **`.ZIP` character packs & `.FNMM` levels:** auto-detects Sparrow, Spritemap, or FNMM archives and extracts characters + health icons from existing `.fnmm` levels.
- **Interactive stage & baking (`CharacterEditor.cs`, `CharacterLibrary.cs`):**
  - **Icon buttons:** the editor's buttons use white Material Design PNG icons (Apache 2.0) from `Assets/images/ui/character_editor/*.png` (40 files; loaded via `TitleContainer`, premultiplied, tinted by the button colour). `CharacterEditor.ResolveIcon` maps button labels to icon names; if an icon file is missing the button falls back to plain text.
  - Live stage viewport anchored at ground foot pivot `(0, 0)` with **Onion Skin (`G`)** ghosting `IDLE` frame 0 behind active poses, mouse/touch drag alignment, arrow-key/touch nudge buttons, live `D/F/J/K` (`1`–`5`) sing testing, `Flip X` baking, 1-click `Swap Left <-> Right`, `NativeFacing` (`Left` BF vs `Right` Opponent), `Smooth`/`Pixel` filter toggle, and automatic 2-frame FNF health icon strip splitting (`300x150` → `icon/0.png` normal + `icon/1.png` losing).
  - Saves FunkyMaker-native packages into `mods/characters/<slug>/` (`sprite.json`, `0.png..N.png`, `character.json`, optional `icon/`), also imported on Android SAF via `PlatformPaths.GetCharactersPath()`.
- **Local Online Custom Character Equip (client-side only):**
  - Persisted as `MultiplayerSettings.EquippedCharacter` (`""` = Level Default); selectable in **Multiplayer → Settings → User → `My Character (Local)`** and **Waiting Room** (`B` hotkey / `B  CHAR:` button).
  - In `Game1.SetupLevel` (`Game1.LevelLoad.cs`), during online FNF matches (when not spectating and `ChartOnlyMode` is off), clones `_level` in memory and applies the equipped character + health icon onto `myLocalCharStrum` (`0` for Role 1 / BF, `1` for Role 2 / Enemy), auto-aligning feet & visual height to the replaced character and auto-mirroring + swapping `Sing Left ↔ Sing Right` when placed on the opposite stage side without any server protocol changes.

---

## 12. Multiplayer (fork: room system; current protocol 4, 2026-11-25)

The 1v1 matchmaking (QUICK PLAY / PRIVATE ROOM with a 6-char code, the official game server's
`FindMatch` / `MatchFound` / `GameStart`) was replaced by **rooms** on the fork's own online server
(repo `AbelitoGamer/FunkyMakerPlayerTransferServer`, local path
`C:/Users/abeli/Desktop/sources/FunkyMakerTransferServer`). That server also contains the official
server's code (leaderboard + pairwise ELO, users, verified levels), level transfers and replays.
**Since protocol 4 it runs on Cloudflare Workers + Durable Objects + R2** (`worker/` folder, §12.z);
the ASP.NET version in `src/` (SignalR, Render) is retired but kept as the reference the Worker was
ported from. **Old game builds can't use the new server and new builds can't use the old ones**
(`Hello` refuses other protocol versions with "Update the game").

Sections 12.x / 12.y below describe protocol 3 work on the ASP.NET server; the room rules, limits
and client behaviour still apply (the Worker is a port), but anything about Render, Kestrel,
SignalR, `render.yaml` or `Transfer__*` env vars is history: see §12.z for the current equivalents.

### 12.z Cloudflare Workers server (protocol 4, 2026-11-25 / 26)

Why: Render's free plan caps bandwidth at 5 GB/month and level transfers blew through it; the
goal is $0. Workers and R2 don't charge for bandwidth. Playerbase is tiny (≤ 20, sporadic).

- **Deployed**: `https://funkymaker-online.abelitogameryt.workers.dev` (= client
  `MultiplayerSettings.DefaultServerUrl`). R2 bucket `funkymaker-online`; lifecycle rules
  `transfers-1d` / `replays-1d` delete leftovers after a day. `GET /` redirects to
  `/api/server/info`; `/health` for checks.
- **Server code** (`FunkyMakerTransferServer/worker/`, TypeScript, docs in `worker/README.md`):
  | Piece | Old ASP.NET server (Render) | Worker |
  |---|---|---|
  | Rooms, matches, chat | `RoomRegistry` + SignalR hub | `Hub` Durable Object (`src/registry.ts` pure logic, `src/hub.ts`), WebSocket `/multiplayer` |
  | Level files | temp folder on disk | R2 `transfers/` (chunked upload = R2 multipart) |
  | Official levels | `verified_levels/` folder | R2 `verified/` + precomputed `verified/index.json` (`npm run sync-levels -- --remote`) |
  | Leaderboard, history, users | JSON files / MongoDB | `Leaderboard` Durable Object (SQLite), `src/leaderboard-book.ts` (pure logic, ELO K = 32) |
  | Replays | memory | R2 `replays/` (`src/replays.ts`) |
  HTTP routes and JSON are unchanged: `/api/testing`, `/api/server/info`, `/api/leaderboard`,
  `/api/users[/{id}]`, `/api/verified_levels[/{id}/download]`,
  `/api/transfers/{id}/file|chunks|complete`, `/api/replays`, plus `POST /api/admin/import`
  (Bearer `ADMIN_TOKEN` secret; `"replace": true` wipes leaderboard / history / users first).
- **Settings** = `[vars]` in `worker/wrangler.toml` (the old `Transfer__*` / `Testing__*` env
  vars): `MAX_UPLOAD_MB` 250, `UPLOAD_CHUNK_MB` 6, `MAX_ROOM_MEMBERS` 16, `STALL_TIMEOUT_SECONDS`
  300, `DOWNLOAD_COMPLETION_GRACE_SECONDS` 600, `LOADING_TIMEOUT_SECONDS` 180, `CACHE_MINUTES` 15,
  `PROGRESS_INTERVAL_MS` 200, `TESTING_OPEN` / `TESTING_MESSAGE`, `HUB_LOCATION_HINT` (region of
  the room server when first created; empty = near the first player). Change → `npx wrangler deploy`
  in `worker/`. Secrets: `npx wrangler secret put ADMIN_TOKEN`; locally in `worker/.dev.vars`
  (gitignored). Node isn't on PATH on the dev PC: use `C:\Program Files\nodejs`.
- **Commands** (in `worker/`): `npm test` (unit tests, port of `RoomFlowTests` / ranking / ELO;
  44 passing), `npm run typecheck`, `npm run dev` (local server `http://127.0.0.1:8787`),
  `node scripts/e2e.mjs [url]`. **Running e2e against production writes fake players A / B into
  the leaderboard**: re-import the old data with `replace: true` afterwards.
- **Free-plan limits it is designed around**: 10 ms CPU per request, so the Worker never hashes
  or byte-counts big files (bytes are piped between the client and R2); 100 k requests/day
  (Workers and Durable Objects; 20 WebSocket messages = 1 request); R2 10 GB stored, free egress.
  Going over the free tier is billed, not capped.
- **Protocol 4 wire format** (client `Multiplayer/WsHubConnection.cs`, server `src/hub.ts`):
  plain WebSocket to `wss://<host>/multiplayer`, every frame JSON text:
  `{"type":"invoke","id":7,"method":"JoinRoom","args":[…]}` (no `id` = fire and forget) →
  `{"type":"result","id":7,"result":…}` / `"error":"…"`; server events
  `{"type":"event","method":"RoomState","payload":{…}}`; server `{"type":"ping"}` every 10 s; the
  client must send something every 90 s (its `Ping` invoke is enough). Messages over ~384 KB come
  as `{"type":"frag","fid","i","n","data"}` and the client splits its own big messages (64 K chars
  per fragment). Method names, arguments and payloads are the same as with SignalR (camelCase).
- **Other protocol 4 changes**: chunked uploads use exactly `HelloResult.UploadChunkBytes` (6 MiB)
  for every chunk but the last (R2 multipart parts must be equal and ≥ 5 MiB); packed uploads
  (osu! `.osz`, FNF `chartonly`) send `X-File-Hash: <sha256>` (`Protocol.FileHashHeader`) because the
  Worker can't hash them; single PUT is capped at 100 MB by Workers (chunked is always preferred);
  download progress `SetLevelStatus` reports keep a download alive (the server no longer counts bytes).
- **Old servers**: `funkymaker-transfer.onrender.com` (protocol 3) is suspended.
  `funkymakerserver.onrender.com` (the older 1v1 server) is still up; its leaderboard (4 players,
  15 matches) was imported into the Worker. Saved `*.onrender.com` URLs in a player's settings are
  ignored (§12.0), so they move to the new server.
- **Status (2026-11-26)**: deployed, client builds for desktop and Android, end-to-end script passes
  against production. **Not yet tested with the real game** (rooms, transfers, matches). Client code
  is on branch `cloudflare-workers` in both repos, not merged into `master` / `main` yet.

### 12.x osu!mania songs online + song select = Freeplay (fork, protocol 3, 2026-11-21)

- **Protocol 3** (client `Rooms/RoomProtocol.cs` = server `Protocol.cs`): `LevelEntry`/`RoomLevel`/
  `SongInfo` got `Kind` (fnf/osu), `SetId` (set folder name, lower-case), `SetName`, `Difficulty`,
  `SizeNoVideo`; `RoomSettings.SongTypes` (fnf / osu / both; official levels count as fnf);
  `HelloRequest.OsuSkipVideo`; transfer packets got `Kind`, `Variant` (full / novideo), `MaxSize`,
  `FileHash`. Server limits: MaxUploadMegabytes 250 (also in render.yaml), MaxLevelsPerMember 5000,
  hub message 4 MB. Server tests (34) include osu transfers with both variants and song types.
- **Client**: `RoomClient.ScanOsuLevels` lists every 4K difficulty of mods/osu (hash = the .osu,
  sizes from `OsuLibrary.SetSizes`). Upload of an osu! song = `OsuLibrary.PackSet` (.osz to temp,
  without video for the novideo variant); download = check `FileHash`, `OsuLibrary.ImportReceived`
  (import + find the .osu with the level hash). `LocalLevel` has the osu! fields. Option
  "Skip osu! Videos" in online Settings → Performance (resends Hello). Room setting "Song types".
  The lobby IMPORT accepts .osz.
- **Song select = the Freeplay browser**: a second `FreeplayMenu` built with an
  `IOnlineSongSource` (FreeplayMenu.Online.cs; MultiplayerMenu.SongSelect.cs implements it).
  Tabs OFFICIAL / FNF / OSU!MANIA as the room allows (`FreeplayTab.Official`). Songs not on this
  device use `FreeplayMenu.OnlineKey(hash)` (a temp path where nothing exists) so file readers find
  nothing; cached official levels use `LevelCache.WriteTempFile`. osu! rows group difficulties by
  SetId into synthetic `OsuSet`s. Rows show holder squares / OFFICIAL tag; the details show
  "IN THIS ROOM" (who has it, size, sent or not) and "ROOM" instead of personal best / modifiers.
  Off online: modifiers, attempts, song rate, chart editor, new chart, main menu; ESC / the middle
  button = back; ENTER = `IOnlineSongSource.Pick`. Preview audio mutes the online menu music.
- Verified 2026-11-21 with a local server and two windows (separate exe folders = separate mods):
  song types row, three tabs, remote osu! set listed, picked, packed by the holder, imported by the
  other, match played. Not verified end to end: the novideo variant (server test only), Android.
- **osu! = Mirror only** (2026-11-21): server `KindAllowed` refuses osu! songs unless the room mode
  is Mirror (pick error "only be played in Mirror mode"; changing the mode away clears an osu! pick;
  `SanitizeSettings` refuses SongTypes osu + a mode other than Mirror). Mirror matches of an osu!
  song always use side Player (the single chart); the vote buttons / VOTE pills are hidden for osu!
  songs. Client room settings couple both rows (osu!mania only → Mirror; leaving Mirror with osu!
  only → FNF & osu!). The OSU!MANIA tab outside Mirror shows an explanation instead of songs.
  Server tests: 35 (`Osu_songs_are_only_played_in_mirror_rooms`).
- **Gameplay**: `SetupLevel` keeps an osu! level at ONE centred strumline in online matches too
  (`if (_level.IsOsu) strumlineCount = 1` in the duel branch), like single player; the online
  score list stays.
- **Song select sections**: FNF / OSU!MANIA tabs online are split into SHARED LEVELS (you + someone
  else), YOUR LEVELS (only you), then "<PLAYER>'S LEVELS" (first holder in join order) for songs you
  lack; osu! sets as a whole. `OnlineSong.Group/GroupOrder` (set by `MultiplayerMenu.SectionOf`),
  `FreeplayMenu.GroupOnline` (stable sort after the chosen sort), headers via `_rowGroups` /
  `_headersThrough`, `RowTop` / `ListContentHeight` replace `i * itemH` in scroll + draw. OFFICIAL
  tab has no sections.
- **"?" icon**: `DrawLevelIcon` draws `DrawUnknownIcon` for rows not on this device.
- **Waiting room song slot** (taller, 196 px): art from the picker's icon cache via
  `FreeplayMenu.DrawSongArt` / `SongBanner` (they set `_graphicsDevice` themselves: the picker may
  never have been drawn): osu! = background behind the whole slot (darkened), FNF = icon square
  (initials badge, "?" while not downloaded; path = `Room.LevelPath` when ready). Lines: kind tag +
  picked by, title (osu!: set name), "[difficulty]  stars  size (without video)", holder squares,
  status / download bar. The in-match slot shows the same art.

### 12.y Online fixes (fork, 2026-11-22; server + client, protocol still 3)

- **Extra Strumlines setting ignored online, extras always hidden there**: `Game1.HideExtras`
  (= `HideExtraStrumlines || _isMultiplayerMatch`) replaces the setting in `RepositionStrumlines`, the
  strumline layout / draw and SetupLevel alphas (classic 2-strumline layout; the CPU still plays the extras).
- **Force start removed** (button, F key, `RoomClient.ForceStart`, server `/forcestart`; it's plain chat now).
- **A pick is final**: song select ENTER opens "Pick this song?" (`_pickConfirm`, `DrawPickConfirm` in
  `.SongSelect.cs`; Y / ENTER / CONFIRM sends, N / ESC / click outside cancels; the picker is `Busy` meanwhile).
  Server `SelectSong` refuses while `room.Song != null` ("can't be changed"); the slot's CHANGE SONG and
  `OpenSongSelect` with a song picked are gone. The server still clears a pick when the picker leaves /
  spectates or new settings disallow it (then the turn player picks again).
- **100 % has to be earned**: players on an empty strumline used to get 100 % for doing nothing
  (`ComputeAccuracy` returns 100 with no judged notes). Online, `Game1.MatchAccuracy` reports 0 % when
  nothing was judged; the server also sets Accuracy 0 on any `FinishMatch` with no judged notes (old
  clients). Display: accuracy shows green when it is 100 % (`MultiplayerResultsScreen.PerfectAccuracy`),
  0 misses green only with at least one hit (`NoMisses`); results screen and the round detail popup.
- **Leaving / crashing mid-song** (results screen waited forever, room stuck "in progress", late or missing
  "left" notices, CPU not taking over):
  - Server heartbeat: `Participant.LastSeen` (states / hits / key presses, `Relay` + `RecordState`; set to the
    song start in `TryStartMatchLocked`). `Sweep` (now every 250 ms) marks a participant silent for
    `RoomRegistry.SilenceTimeout` (10 s) as Gone ("X stopped responding and left the match"), broadcasts,
    and finishes the round when everybody else is done. SignalR `ClientTimeoutInterval` 60 → 30 s.
  - Server `FinishMatch` with `Quit` during **loading** removes the participant (start / cancel like a
    leaver); it used to be ignored. Client `QuitRoomMatch` now sends the quit also before `_started`.
  - Relay ignores participants with a Final (quit / finished players no longer drive strumlines).
  - Client `RoomClient.LeaveMatchIfNotPlaying` (every RoomState): the server lists you as PLAYING a match
    this game isn't in (`ActiveMatchId` differs, not pending) → sends `FinishMatch{Quit}` once (game
    restarted / rejoined).
  - Client gone detection (`UpdateRoomMatch`): also server status LEFT MATCH, or no longer in the server's
    `Match.Players` (dropped while loading) → CPU plays their strumline.
  - Results screen while waiting: `LiveRows` = every participant's latest relayed score, `[playing]` /
    `[left]` tags (`Game1.LiveResultRows`), instead of only your own row.
- **Spectating after leaving**: `RoomClient.MarkLeftMatch` / `LeftCurrentMatch` (quit locally, or your status
  is LEFT MATCH / FINISHED) → `MyAssignment` is null, so WATCH MATCH only spectates (it used to load you as a player).
- **Progress**: `RoomMember.UploadProgress` (new, -1 = none; server no longer overloads `LevelProgress` with
  it). Rows show "Sending level N%"; your slot shows "Sending the level to the room... N%" (or "Packing the
  set..." for osu!) with a bar from `RoomClient.UploadProgress` (local `Progress<long>` on the upload). Your
  own row uses local `Room.LevelState / LevelProgress`. Server: throttled progress changes set
  `Room.ProgressDirty`, flushed by `Sweep`, so every viewer ends on the same latest value.
- Server tests: 38 (new: forcestart is plain chat, pick is final, quit while loading, silent player dropped,
  nothing judged = 0 %, upload progress field). Not yet verified with two real game windows.
- **32-bit Android online multiplayer black-screen-to-menu & OOM fixes (2026-11-22):**
  - **Load / setup failure handling (`Game1.LevelLoad.cs`, `Game1.cs`):** `SetupLevel` used to catch exceptions
    by setting `_errorMsg` without rethrowing or calling `HandleSetupLevelFailure`, so `FinalizeLoadedLevelForGameplay`
    kept running, created a fresh `LoadingScreen` at `_uiAlpha = 0f` (solid black screen that never updated
    because `UpdateInternal` returns early when `_errorMsg != null`), called `OnRoomMatchLevelLoaded()`, and
    left `_errorReturnToMultiplayer = false` (kicking the player back to Freeplay on the next input). Likewise,
    background load exceptions in `StartLoadingLevel` left `_loadingScreen` active and skipped `HandleSetupLevelFailure`.
    Now both `SetupLevel` and `StartLoadingLevel` (via `_pendingLoadException` finalized on the main thread)
    route failures through `HandleSetupLevelFailure`, `FinalizeLoadedLevelForGameplay` aborts immediately if
    `_errorMsg != null || _level == null`, `DrawInternal` checks `_errorMsg == null` before drawing `_loadingScreen`,
    and Android screen taps (`_touchControls.TapReleased`) dismiss the error overlay after `_errorDismissGrace` (0.5 s)
    back to the online room.
  - **Disk-backed `LevelCache` (`Multiplayer/LevelCache.cs`, `LevelDownloader.cs`):** `WarmFromDisk` and
    `CacheLevel` used to keep every cached official level's full `.FNMM` `byte[]` archive in `_cachedLevels` in RAM
    permanently. Now once a level is verified in `official_cache/<hash>.fmc`, only its metadata and SHA-256 hash
    remain in RAM (`_levelInfo`, `_computedHashes`), `TryRestoreFromDisk` verifies disk files via streaming
    SHA-256 (`LevelHasher.CalculateHash(filePath)`), and `WriteTempFile` / `WriteTempFileForHash` copy directly
    from `official_cache/<hash>.fmc` (`File.Copy`) without allocating full-archive `byte[]` arrays in memory.
  - **On-the-fly legacy `.FNMM` JSON conversion & `LoadingScreen` safeguards (`FnmmConverter.cs`, `FnmmLoader.cs`, `LoadingScreen.cs`, `Game1.LevelLoad.cs`):**
    When a legacy-format `.FNMM` level (e.g., `deadlullaby.FNMM` with `evt.json` or `"notes"` in `data.json`)
    finished downloading into `mods/levels/Received/`, `FreeplayMenu.RequestIcons` (`WithLevelFiles`),
    `RoomClient.ScanLocalLevels` (`TryLoadChartData`), `FreeplayMenu.QueueStarRatings` (`TryLoadChartData`),
    and `FnmmLoader.Load` all called `FnmmConverter.ConvertIfNeeded(path)` concurrently on 3–4 worker threads.
    Each call unpacked **every file** in the `.FNMM` archive (`music.ogg` + all sprite PNGs) into a dictionary in
    RAM and re-compressed the entire archive into a new in-memory ZIP `byte[]`, exhausting 32-bit RAM before
    `StartLoadingLevel` even began (causing `new LoadingScreen(...)` to throw `OutOfMemoryException`
    synchronously on the main thread after `_inMultiplayer` and `_inFreeplay` were set to `false`, which stranded
    the game on `ErrorOverlay.DrawDropHint` `"Drag a .FNMM, .ZIP or .RAR file here"`). Now `FnmmLoader.Load`,
    `TryLoadChartData`, and `WithLevelFiles` read directly from the ZIP on disk and convert only the small JSON
    strings (`data.json`, `evt.json`, `scene.json`, `sprite.json`) on the fly via `FnmmConverter.IsOldZip` /
    `ConvertData` / `ConvertEvents` / `ConvertScene` / `FixSpriteJson` (and `TryLoadChartData`'s SharpCompress
    fallback reads only `data.json`). `LoadingScreen` now caches `AnkushCat.png` in a single shared static
    `Texture2D` guarded by `try/catch`, and `StartLoadingLevel` wraps its synchronous setup in `try/catch` routed
    to `HandleSetupLevelFailure`.
  - **Peak LOH audio & match memory reduction (`OggLoader.cs`, `AudioDecoder.cs`, `Game1.LevelLoad.cs`):**
    `OggLoader.DecodeVorbisWithPcm` and `AudioDecoder.DecodeMp3` (updated 2026-11-23) now stream
    4096-sample float chunks via `OggLoader.DecodeFloatStreamToPcm` into fixed **1 MB** `byte[]`
    chunks assembled once into an exact-sized final PCM array (eliminating both the full-song `float[]`
    LOH buffer and `MemoryStream.EnsureCapacity` 2× capacity doubling + `ToArray()` double-copy that
    caused OOM on 10–25 min marathon songs such as `Yellow` and `Trinity Encore`), with on-the-fly
    linear resampling and stereo-to-mono downmix across chunk boundaries (PC 64-bit stays native;
    32-bit streams >24 MB PCM at 22050 Hz and >64 MB PCM at 22050 Hz mono; 64-bit Android streams
    >80 MB PCM at 22050 Hz and >180 MB PCM at 22050 Hz mono; plus automatic compact 22050 Hz mono
    retry on `OutOfMemoryException`). On Android (`!_playtestingFromEditor`), `SetupLevel` frees
    raw sprite PNG `byte[]` buffers per frame during `LoadLevelTextures` and clears `_level.MusicData`
    immediately after decoding (plus `_musicPcm = null` in non-spectator online matches on both
    32-bit and 64-bit Android).
  - **Transfer server slow-upload/download & loading resilience (`FunkyMakerPlayerTransferServer`, 2026-11-23, Protocol 3 compatible — no client changes):**
    - `Program.cs` + `TransferEndpoints.cs`: disabled Kestrel `MinRequestBodyDataRate` / `MinResponseDataRate` (and per-request rate features) so slow/mobile/proxy-buffered transfers aren't killed by Kestrel's default 5 s / 240 B/s limit; set `MaxRequestBodySize = null` (per-transfer size still validated against `ticket.Size`), `KeepAliveTimeout = 10 min`, `RequestHeadersTimeout = 2 min`, and raised SignalR `ClientTimeoutInterval` from `30 s` → `90 s` (avoids dropping uploaders during uplink bufferbloat while keeping `SilenceTimeout = 10 s` in-match).
    - `RoomRegistry.cs`, `TransferOptions.cs`, `appsettings.json`, `render.yaml`: raised `LoadingTimeout` from `100 s` → `180 s` (configurable via `TransferOptions.LoadingTimeoutSeconds` / `Transfer__LoadingTimeoutSeconds`), `StallTimeoutSeconds` from `60 s` → `300 s`, and added `DownloadCompletionGraceSeconds = 600` (`MarkDownloadStreamComplete`) so slow clients draining Render's reverse-proxy buffer & verifying SHA-256 aren't prematurely hit with `"The download timed out."`. `CompleteDownload(ok: true)` now also recovers a member to `LevelReady` even if `Sweep` already expired the pending download entry, and truncated uploads report `"The upload ended early (connection closed before finishing)."` instead of a misleading file-changed error. Verified with **41/41** passing server tests (`RoomFlowTests.cs`).
  - **Chunked uploads & Online FNF "Chart Only (No Sprites)" mode (Client + `FunkyMakerPlayerTransferServer`, 2026-11-24, Protocol 3 backwards-compatible):**
    - **Chunked level uploads (`TransferEndpoints.cs`, `RoomRegistry.cs`, `TransferOptions.cs`, `LevelTransferClient.cs`, `NetworkManager.cs`, `RoomClient.cs`, `LevelDownloader.cs`):**
      - Server adds `PUT /api/transfers/{id}/chunks?offset=N` + `POST /api/transfers/{id}/complete` alongside legacy `PUT /api/transfers/{id}/file`, raises `HttpRequestsPerMinute` `60 → 360`, and advertises `SupportsChunkedUpload = true`, `SupportsFnfChartOnly = true`, `UploadChunkBytes = 4 MB` (`Transfer__UploadChunkMegabytes`) in `HelloResult`.
      - Client (`LevelTransferClient`) uploads in 4 MB chunks (`85 s` per-chunk timeout, up to 4 idempotent retries with backoff, automatic fallback to legacy single-PUT on 404/405 against older servers), uses `SocketsHttpHandler` TCP keep-alives, passes `CancellationToken` into `ProgressStreamContent`, retries interrupted downloads up to 3 times, cancels superseded/abandoned uploads via `RoomClient._uploadCtsByKey`, and sets `LevelDownloader` timeout to `Timeout.InfiniteTimeSpan`. Bypasses Render/Cloudflare ~100 s request timeouts and ~100 MB single-body caps on large levels.
    - **Online FNF "Chart Only (No Sprites)" mode (`MultiplayerSettings.ChartOnlyMode`, `FnmmLoader.cs`, `Game1.LevelLoad.cs`, `RoomClient.cs`, `MultiplayerMenu.cs`, `MultiplayerMenu.WaitingRoom.cs`, `RoomRegistry.cs`, `Protocol.cs`):**
      - Toggleable in **Multiplayer Settings → Performance (`Chart Only (No Sprites)`)** and directly in the **Waiting Room** (`M` hotkey, left-panel button, and right beside Mirror's `1 PLAYER / 2 ENEMY` vote buttons).
      - When enabled during online FNF matches (`Game1.LevelLoad.cs`), `FnmmLoader.Load(..., chartOnly: true)` skips loading sprites, stage layers, icons, and visual events — loading only chart + audio for minimal RAM/VRAM usage on low-RAM / 32-bit phones.
      - Over the network (`Protocol.VariantChartOnly = "chartonly"`, announced via `HelloRequest.FnfChartOnly`), holders pack a minimal `.fnmm` ZIP via `FnmmLoader.PackChartOnlyFnmm` containing only `data.json`, `meta.json`, minimal `scene.json` (`landscape`), and `music.ogg` (~3–12 MB instead of 100–250 MB). Receivers cache it in app storage (`chartonly_cache/<hash>.fnmm`, outside `mods/levels/` so Freeplay / `LocalLevelIndex` never scan it) and are never registered as full-level holders (`Member.ChartOnlyHashes`). Toggling the option in a room immediately re-syncs `Hello`, cancels any in-flight download of the old variant, and requests the new variant if needed. Verified with **43/43** passing server tests.

### 12.0 Server URL, testing two windows

- `MultiplayerSettings.EffectiveServerUrl`: env `FUNKYMAKER_SERVER_URL`, else `ServerUrl` in
  `multiplayer_settings.json` (JSON only), else `TransferServerUrl` (old name), else
  `DefaultServerUrl` = `https://funkymaker-online.abelitogameryt.workers.dev` (protocol 4; it was
  `https://funkymaker-transfer.onrender.com` up to protocol 3). Saved `ServerUrl` /
  `TransferServerUrl` values containing `.onrender.com` are ignored (`IsUsableSavedUrl`), so players
  with an old URL in their file reach the new server. WebSocket hub `/multiplayer` (§12.z).
- Every online menu uses this one URL: leaderboard sync, server info and the user directory used to
  fall back to the old `funkymakerserver.onrender.com`; they now use `DefaultServerUrl` too.
- `FUNKYMAKER_MP_PROFILE=<name>` uses `multiplayer_settings.<name>.json` (own PlayerId / name / colour),
  for two windows on one PC. The same PlayerId joining a room again **takes over** the old place (the
  other window gets `Kicked`: "joined from another window").

### 12.1 Client code

- `Multiplayer/Rooms/RoomProtocol.cs`: wire types, `Protocol.Version = 4`, `Protocol.FileHashHeader`
  (keep in sync with the server's `worker/src/protocol.ts`; it was a copy of the ASP.NET `Protocol.cs`).
- `WsHubConnection` (protocol 4, replaces the SignalR `HubConnection` with the same shape:
  `InvokeAsync` / `SendAsync` / `On<T>` / `Closed` / `Reconnecting` / `Reconnected`): `ClientWebSocket`
  with the JSON envelope of §12.z, fragmentation both ways (incoming capped at 64 MB), a silence
  watchdog (`ServerTimeout` 60 s; the server pings every 10 s) and automatic reconnect over the given
  delays (a new socket; `NetworkManager` then sends `Hello` again). Handlers run on the receive thread.
- `NetworkManager`: connection, reconnect (backoff + `WsHubConnection` auto reconnect; `Hello` again, then
  `RoomClient` re-joins the room), ping / `TimeSyncManager`, leaderboard, official level list (HTTP).
  **All server messages except Pong are queued and raised on the game thread** by `Update` (Game1 calls
  it every frame); `Call<T>` / `Send` invoke hub methods (results also on the game thread).
  Connect timeout 30 s (was 90 s for the sleeping free Render instance; the Worker never sleeps).
- `Rooms/RoomClient` (singleton): room list, current `RoomStatePacket` (Levels / History merged, they are
  null when unchanged), chat, the round's level (official: `LevelDownloader` / `LevelCache`; local: your
  file, or the server's `TransferDownload` into `mods/levels/Received/`, hash-checked; `TransferUpload`
  streams your file), `SetLevelStatus` reports, live states of the others, match events
  (`MatchReadyToLoad`, `MatchStarting`, `RoundFinished`). Local levels are hashed with `LocalLevelIndex`
  and star-rated (`ChartDifficulty`, player chart) in the background, then sent with `UpdateLevels`.
  The server sends the first RoomState + chat history before CreateRoom / JoinRoom return: buffered
  while joining.
- `Transfer/LevelTransferClient`: static HTTP helpers (level up/download with progress, replay tracks).
  Chunked uploads use exactly the server's chunk size (6 MiB; R2 multipart); packed variants
  (osu! `.osz`, FNF `chartonly`) are hashed locally (`HashFileAsync`) and the hash is sent as
  `X-File-Hash` on `POST /complete` or the single `PUT /file`.
- `UI/TextEntry`: free text (desktop `Window.TextInput`; Android `KeyboardInput.Show` dialog).

### 12.2 Menus (`MultiplayerMenu*.cs`)

States: `MainMenu` → `Connecting` → `RoomBrowser` → `RoomSettings` (create / edit / view) →
`WaitingRoom` ↔ `SongSelect`; plus `Settings`, `Leaderboard`.
- **Room browser** (`.RoomBrowser.cs`): search box (F), "Hide password rooms" (H), Refresh (R),
  CREATE ROOM (C); cards with OPEN / PASSWORD, name, mode, host, song being played (+ stars), a square
  per player (hover = name), players / max. Password prompt overlay. Polls `ListRooms` every 3 s.
- **Room settings** (`.RoomSettings.cs`): name (fixed after creation), mode, song selection (turns /
  host only), privacy + password, player limit 2..`NetworkManager.MaxRoomPlayers` (server env
  `MAX_ROOM_MEMBERS` in wrangler.toml, set to 16 (was `Transfer__MaxRoomMembers`, default 8), max 32, sent in `HelloResult.MaxRoomPlayers`; it used to be unused, always 8) (≥ current players), guests' local levels. Host
  edits between rounds; others see it read-only (C in the waiting room).
- **Waiting room** (`.WaitingRoom.cs`): left = players in join order (colour, HOST, PICKING, status
  READY / NOT READY / PLAYING / FINISHED / LEFT MATCH / SPECTATING (server sends room state on every
  FinishMatch; finished / quit participants used to stay PLAYING until the round ended), level download %, Mirror vote, host KICK on hover + confirm);
  buttons READY (R), SPECTATE (S; while a match you aren't in runs it is WATCH MATCH — the separate
  WATCH button in the list was removed, W still works), (FORCE START / `/forcestart` was removed
  2026-11-22, client and server), WATCH MATCH (W, match in progress),
  SPECTATE (S), ROOM SETTINGS (C). Centre =
  current song slot (Mirror: voting a side also sends READY, after the vote is accepted; players list
  scrolls with wheel / drag, compact rows; chat wraps long words by characters) (SELECT SONG on your turn / picked song + blocker text + download bar + Mirror
  vote 1/2 / match in progress; no CHANGE SONG since 2026-11-22, a pick is final) and the rounds played (newest first; click / ENTER =
  standings popup). Right = chat (T, ENTER sends and keeps typing, ESC stops, wheel scrolls).
- **Song select** (`.SongSelect.cs`, was the lobby): OFFICIAL / LOCAL tabs. Wheel / drag scrolls the list itself
  (3 rows per notch on desktop, 1 on Android), the highlight stays in view (it used to only move the highlight). LOCAL (`.LocalRows.cs`)
  lists only levels you may pick (server `Pickable`): `YOUR LEVELS` + `FROM <player>` sections with
  holder squares; I imports, DEL deletes your own. ENTER picks and returns to the waiting room.
- **Leaderboard** (`MultiplayerMenu.cs` `DrawLeaderboardModern`, tab 1 = RECENT MATCHES in
  `.MatchHistory.cs`, 2026-11-26): the server's match history (`GET /api/leaderboard` +
  `MatchHistoryResponsePacket`) carries every room round: `Mode` (versus / teams / mirror; empty =
  old 1v1 duel), `Players[]` (place, team −1 / 0 Player / 1 Enemy, side, score, accuracy, max combo,
  misses, disconnected, ELO change) and `WinningTeam`. `LeaderboardManager.ApplyServerMatchHistory`
  keeps all of it in `match_history.json` (`MatchHistoryEntry.Players`, `IsRoomMatch`, `Participants`,
  `Involves`; entries cached before this get their players filled in on the next sync); it used to
  keep only the first two players, so every match showed as a duel. Cards: mode pill (+ "· NP" for
  3+ players), level, time ago, result pill ("YOU #n" if you played, else "X won" / "Team Enemy won" /
  "Draw"), then the players by place with scores (Teams: each team's average + members; old duels:
  "A (score) VS B (score)"; overflow → "+N more"). ENTER / SPACE / click opens the **match detail**
  popup (#, player, team / side, score, accuracy, combo, misses, ELO ±); ENTER / ESC / click closes.
  `UserDirectoryClient` also learns names from every player of a room match (server too: `knownUsers`).
- Shared helpers in `.Rooms.cs` (named click targets `Hit` / `ClickedHit`, text field, checkbox,
  room events). Online settings rows added: Show Scores List, Scores List Side, Teams: Rival Strumline.
- **Text fields** (`BeginTextField` / `UpdateTextField`): chat, room name, password, search and the
  settings Username all use it. Desktop: `Window.TextInput`; clicking outside the field stops editing
  (name / search / username keep the text, the chat keeps an unsent draft, the click still reaches
  its target). Android: opens the system text dialog (`KeyboardInput.Show`).
- **Android touch** (`TouchAsMouse`, 2026-11-20): Mouse.GetState doesn't see touches on Android, so the
  first touch that isn't on the on-screen D-pad is turned into a mouse state (tap = click, drag =
  wheel scroll without a click). Before this, room screens couldn't be used on phones.
  **Fixed 2026-11-20 (READY / chat did nothing on phones):** MonoGame's `TouchPanel.GetState()` ages
  touches on every call (Pressed → Moved, Released dropped) and Game1's `TouchControls.Update()` reads it
  first each frame, so `TouchAsMouse` never saw Pressed: the drag start stayed stale, every tap counted
  as a drag and no click fired (hover still followed the finger). New fingers are now recognised by
  touch id, the drag threshold scales with the screen (≥ 4.5 % of its height), and a room button under
  the finger beats the D-pad / A / B drawn over it (`IsRoomHitAt`, `TouchControls.IgnorePoint`): READY sits
  under the D-pad and the chat box under A/B on most phones. Any other code reading `TouchPanel.GetState()`
  after Game1 has the same problem (e.g. `FreeplayMenu`).

### 12.3 Matches in Game1 (`Game1.Online.cs`)

- `RoomClient.MatchReadyToLoad` → `StartRoomMatch`: players get their side (role = side + 1, so the
  old role code still picks the charts: role 1 = chart 0 / player, role 2 = chart 1 / enemy);
  spectators follow the first player (strumline 0 not player-controlled, driven by their hits).
  Level loaded → `MatchLoaded`; `MatchStart` carries the synced start time (server time + max-ping
  buffer + 4 s; the client shows 3, 2, 1, Go synced to it with `CountdownOverlay.SyncTo`). Late spectators seek to the current song time.
- Strumline 1 (other side): Versus = the other player's presses; Teams = first player of the other team
  or the CPU (`TeamsRivalStrumline`); Mirror = CPU. **Extra strumlines (2+) are kept and played by the
  CPU** (they used to be dropped). Hits / key presses are relayed with the sender's PlayerId.
- **Side balancing**: `renderer[0].ScoreMultiplier = SideScoreMultiplier(level, myChart)`.
  `MaxScoreForChart` counts **every** note (2026-11-21): "avoid" notes (`ShouldAvoid`) used to be left
  out, but online nobody loses health so they're free points, and opponent charts often have notes
  that drain health on press: the enemy max was too low, its multiplier too high (Teams: the Enemy
  team outscored a more accurate Player team).
- **Strumline 1 stays in its fixed place online / in duel replays** even with 3+ strumlines; only the
  CPU extras (2+) float over characters (clamped on screen). Floating strumline 1 was placed over the
  wrong character for your side and could be pushed off-screen. Midlescroll split also applies then.
- **Loading screen**: list of every participant with OK / ... (server `MatchInfo.Loaded`, broadcast on
  each `MatchLoaded`; yourself from the local load). "Waiting for <name> (<side>)" only in Versus,
  otherwise "Waiting for N players". Android: the chat's system text dialog is closed when a match
  starts (`KeyboardInput.Cancel`; a player crashed when a song started while typing).
- **Health bar = score difference** (nobody dies online; hit / miss / event health is ignored in a
  match): Versus you − other; Teams your team's average − theirs; Mirror you − leader (− runner-up
  when you lead). Full bar at 12 % of a perfect run (≥ 1500). The old VS HUD (two bars, cards) was
  removed (`MultiplayerHUD.cs`, `OpponentStateInterpolator.cs` deleted); the normal HUD is used plus a
  scores list (`GameplayHudOverlay.DrawMatchScoreList`, left / right, Teams grouped with averages)
  and the ping badge.
- **Leaving**: a participant who leaves the room, disconnects or uses Exit Match (`FinishMatch` with
  `Quit`, plus a relayed state with `Left`) is announced in the chat and with an in-game message,
  shown struck through with `LEFT` at the bottom of the scores list, ignored by the score bar, ranked
  last as "left" (no ELO). If it was the player on strumline 1 (or the watched one), the CPU takes over.
- State packets at the online update rate; song end → `FinishMatch` + results screen
  (`MultiplayerResultsScreen`, rewritten: leaderboard with place / score / accuracy / misses, waits for
  the server ranking (Teams: one column per team with its average, winner highlighted), ENTER / ESC or 15 s → waiting room; Android: a tap after 0.35 s, and the touch
  overlay switches to menu mode (A / B) while it shows — it used to stay on the lane hitboxes, so phones
  couldn't leave the screen). Exit Match (pause overlay) sends what you have.
- Fairness rules kept: no pause / game over / botplay / timeskip in a match, softened GUI events.

### 12.4 Replays (fork)

- **What is recorded:** the player strumline's input frames (`NoteRenderer.InputRecorder`).
  `UpdatePlayer` turns the keyboard into lane masks (held / held last frame / just pressed) and
  records a `ReplayFrame(T, P, H, R, J)` whenever something changed (T = song time it was judged
  at, P = previous frame's song time). Judging itself moved to `ProcessInput(songTime, masks)`.
- **Playback:** `NoteRenderer.StartReplay(frames, inputBuffer, ghostTapping, safeFrames,
  songStartTime)` → `UpdateReplay`: each recorded frame is judged at its **recorded** song time;
  before it, a no-input tick runs at its recorded previous-frame time P, and the per-frame tick
  never runs past the next frame's P. This makes holds end at the same point relative to the next
  press; misses don't depend on frame timing. Result: identical hits / ratings / misses / score at
  any playback frame rate. **Verified** with a harness that drives the real `NoteRenderer`
  (hidden MonoGame window): 12 random charts (holds, early releases, skips, ghost taps, ghost
  tapping on and off), recorded at jittery 30–240 fps, replayed at 60 / 144 / 20 fps and jitter,
  also after a gzip-JSON round trip: 48/48 exact. Judging settings are per-renderer overrides
  (`InputBufferOverride`, `GhostTappingOverride`, `SafeFramesOverride`), so a replay uses the
  recorded player's settings. `ReplayInput` takes precedence over Botplay.
- **Files:** `<MP storage>/replays/*.fmr` = gzip JSON `ReplayFile` (version, level SHA-256 +
  file / display name, kind `solo` / `online`, match id, duration, `Tracks` with frames, judging
  settings, `SongStartTime`, `Result`). `ReplayStore` saves / lists / deletes.
- **Solo:** `Game1.ConfigureReplayForStrumline` (called per strumline in `SetupLevel`) attaches a
  `ReplayRecorder` to strumline 0 (not for playtests or unpacked folder levels). Botplay at any
  point or a seek (`SeekSong`) invalidates it. Saved in the solo song-end branch
  (`OnSoloSongFinishedForReplay`); failed / quit runs are not saved.
- **Online:** recorded the same way (not while spectating); tracks store `ScoreMultiplier` so the
  balanced score replays exactly. At a normal match end (`OnOnlineMatchFinishedForReplay`) your
  side is saved, then (Versus: the rival is the other player) your track is uploaded to the online server
  (`PUT /api/replays?player=me&opponent=them&level=hash`) and the opponent's is polled for up to
  2 min (`GET ...player=them&opponent=me...`) and added to the file. The opponent's frames carry
  **their own song times**, so the replay shows when they really pressed, not when packets
  arrived. A track whose `MatchStartServerMs` (synced server time at song start) differs by more
  than 30 s is from an older match and is ignored. Disconnected matches aren't saved. Needs the
  server's replay endpoints (`/api/replays`; on the Worker stored in R2 `replays/`, kept 1 day).
- **Watching:** main menu → REPLAYS (`UI/ReplayBrowser`: ALL / SOLO / ONLINE, Enter / click
  watch; "..." options menu (§10.0) with WATCH, SHOW IN FOLDER (desktop), DELETE (asks first);
  Delete opens that confirmation directly — the old "DEL twice" is gone), or Freeplay's
  attempts window (§10.1). `Game1.StartReplay` finds the level by hash (levels folder via
  `LocalLevelIndex`, then the official disk cache via `LevelCache.WriteTempFileForHash`) and loads
  it; `_pendingReplay` becomes `_activeReplay` in `ResetGameplayStateForLevelLoad`, so any normal
  load clears it, while pause → Restart (`RestartSong` → `SetupLevel`) restarts the replay.
  Online replays use the match layout without being a match (`IsDuelReplay`, `duelLayout` /
  `duelRole` in `SetupLevel`): you on strumline 0 with your chart, the opponent on strumline 1
  driven by their track (miss sounds muted). The match HUD's opponent score / misses / health come
  from strumline 1 (`UpdateDuelReplayStats`; health uses the hit / miss rule only, level events on
  the opponent's health aren't replayed). While watching: no recording, no game over, no seeking,
  a top banner (`DrawReplayBanner`). At the end each track's result is compared with the recorded
  one; a mismatch shows "Replay drifted...".
- **Not done yet:** WATCH button on the leaderboard's Recent Matches, pause / speed / seek
  controls, sharing replay files. Not tested in the running game UI yet.

## 13. Platform behavior

### Desktop (Windows/Linux)

- **Minimum window size 800×450** (`SDL_SetWindowMinimumSize` in `Initialize`, 2026-11-20). Smaller
  windows made layout code call `Math.Clamp(v, min, max)` with max < min, which throws; any Update/Draw
  exception on desktop writes `crash.log` and exits after 1 s (`HandleGameplayError`), so players
  resizing fast in the online menu "crashed" (reproduced: waiting room below 720 px wide). The online
  menus now use `ClampUi` (min wins, never throws) for every size-dependent clamp; use it (or Min/Max)
  for new layout code there too. Other screens still use `Math.Clamp` and rely on the minimum size.
- "Exe dir" = `PlatformPaths.GetAppBaseDirectory()`: the folder of
  `Environment.ProcessPath`, falling back to `AppDomain.BaseDirectory`. This is
  correct for single-file builds, where `AppContext.BaseDirectory` points at a
  temp extraction folder.
- Mods/levels live (fork) in `%AppData%\FunkyMakerPlayer\mods\`
  (`~/.config/FunkyMakerPlayer/mods` on Linux; the default, shared by every copy of the
  game) or next to the executable, `<exe dir>\mods\` (easier to edit by hand, but each
  copy has its own levels). `ModsStorage.Current` reads `mods_location.txt`
  (`appdata` / `nexttogame`) in the AppData folder. With no file yet it picks
  NextToGame only if `<exe dir>\mods` already holds the player's own content
  (`HasPlayerContent`: anything not `IsShippedDefault`), else AppData, and saves the
  choice. `PlatformPaths.GetModsBasePath()` returns `ModsStorage.CurrentPath`.
  Settings → AUDIO & VIDEO → Storage → **Move Mods Folder** (desktop only; opens a
  "Move mods folder?" window with from/to paths and the trade-off of each location,
  Y/Enter or click to move, N/Esc to cancel; it takes input via the key-capture path,
  `IsCapturingKey`; blocked while a level is loaded, `Game1.HasActiveLevel`) runs
  `ModsStorage.MoveToAsync` on a background thread. It moves per item: a level file,
  an unpacked level folder (has meta.json/data.json; other folders under `levels`, e.g.
  `Received/`, are walked into), each entry of notestyles/notesplashes/countdowns/fonts,
  loose files. Missing at the target → moved; identical (SHA-256, whole folder for
  folders) → source removed; different with the same name → moved as `name (2)`.
  When moving out of `AppContext.BaseDirectory/mods` (normal builds, where the shipped
  defaults live in the mods folder itself), the shipped default items are copied, not
  moved, so `DefaultMods.Bundled` keeps its fallback. Empty source folders are deleted.
  `ModsStorage.Generation` increments after a move; `Game1` and the online menu rescan
  their level lists. Freeplay / online menu look up the levels folder each time (no
  cached path). Move logic verified with a harness on temp folders (all cases pass);
  not tried from the real settings UI yet.
- Assets at `<exe dir>\Assets\` (falls back to `AppDomain.BaseDirectory\Assets`).
- Settings at `%AppData%\FunkyMakerPlayer\settings.json`.
- File pickers (Freeplay import, New Chart `.ogg`) use
  `PlatformPaths.PickDesktopFiles`: Win32 `GetOpenFileName` on Windows; on Linux
  (fork) `zenity`, then `kdialog`, converting the Win32 filter string. If neither
  is installed it logs a hint and returns nothing (drag-and-drop still works).
  The call blocks the game loop while the dialog is open, like the Win32 one.
- `Updater` (in-menu, Release only) and `DiscordRpc` are active.
- Crash logs at `<exe dir>\crash\`.

### Android

- Game code is under `Platforms/Android/`; `MainActivity` handles lifecycle,
  file picker, SAF, permissions, and crash dialog.
- `PlatformPaths.GetModsBasePath()` returns the app's external files dir
  (`GetExternalFilesDir(null)`) + `mods` (falling back to internal files).
- `ImportFromCustomFolder(uri)` uses AndroidX `DocumentFile` to copy
  `mods/levels`, `notestyles`, `notesplashes`, `fonts`, `countdowns` into app
  storage.
- `OpenFilePickerAsync()` saves a picked level into `mods/levels`; it accepts
  `.fnmm`, `.zip` and `.rar` (MIME filter includes zip/rar variants and `*/*`).
- `GetAssetsPath()` returns the APK asset root `Assets`; assets are extracted
  to a writable location when needed.
- `GetCrashLogsPath()` uses external storage `FunkyMakerPlayer/crashes`.
- Touch controls are merged into the keyboard state for menus and gameplay;
  pause button hidden during multiplayer. `TouchControls.IsPointOnMenuControl`
  lets menus ignore taps that land on virtual buttons (incl. settings `Q`/`E`).
  `TouchControls.TapReleased` = the last finger was lifted this frame (tap anywhere). The per-touch
  per-frame `Console.WriteLine`s in `TouchControls.Update` were removed (logcat spam on the UI thread).
- **Threading / "isn't responding" (2026-11-20):** MonoGame 3.8.6 runs every frame on the Android UI
  thread (`AndroidGameActivity.RenderOnUIThread = true`, the game view `Send`s each iteration to it). A
  long frame therefore blocks touch delivery; > 5 s with a pending tap = ANR dialog, which then stays up
  while the game runs fine. The worst one is finishing a level load (`SetupLevel` texture uploads, one
  frame, seconds for big levels on slow phones). Guard: `AndroidTouchGuardReady` sets
  `FLAG_NOT_TOUCHABLE` on the window, waits 3 frames (the flag needs a layout pass), then the finalize
  runs and `ReleaseAndroidTouchGuard` clears it (also cleared every frame once not finalizing). Don't set
  `RenderOnUIThread = false`: `TouchPanelState` has no locking, touches would race the game thread.
  Other long synchronous frames can still trigger an ANR if tapped during them.
  **Open (2026-11-20):** one player gets the popup every ~5 s from the online loading screen through the
  whole level while the game runs smoothly. A smooth game means the UI thread isn't blocked (the game
  runs on it), so this is an input event never being finished, not a slow frame; cause not found yet
  (no device/logcat). The per-frame "LoadingScreen Phase=Ready" log of that screen was removed.
- Updates: Release builds check GitHub on startup and show a native
  "Update Available" dialog whose Download button opens
  `https://github.com/Thisisfenix/thisisfenix.github.io/releases/latest`.
- Discord RPC is compiled out on Android.

- **Files app access (fork):** `ModsDocumentProvider` (system Files app → side menu
  "FunkyMaker Mods") now exposes the folder the game really uses,
  `GetExternalFilesDir(null)/mods`. It used to expose
  `Documents/FunkyMakerPlayerPublicMods/mods`, a different folder, so the game's own
  levels weren't reachable from the phone. Also added: `DeleteDocument`,
  `RenameDocument` (they were advertised but missing), a canonical-path
  `IsChildDocument` (the old prefix check never matched the "root" id), a path-escape
  guard in `GetFileForDocId`, and correct root flags (`SUPPORTS_CREATE | SUPPORTS_IS_CHILD`;
  the old `8 | 2` meant search + local-only). **Not compiled or tested** (no Android
  workload on the dev machine). No desktop-style "move to AppData" option on Android.

- **Touch UI (fork, 2026-11-23).** Reviewed on a phone (Galaxy A36, 2340x1080):
  - **Back gesture / button** (`Game1.Touch.cs`): it used to *leave the app* from every screen
    (targetSdk 36 on Android 16+ uses predictive back, so the key never reached the game).
    `MainActivity` registers an `OnBackInvokedCallback` (API 33+; manifest
    `enableOnBackInvokedCallback="true"`) and overrides `OnBackPressed` (older), both calling
    `Game1.RequestAndroidBack()`; a gamepad Back edge counts too. At the start of the frame
    `UpdateAndroidBack()` turns it into Escape held for one frame through
    `LayoutKeyboard.BackHeld` (so every screen sees it, the online menu included), or, on the
    plain main menu (`FreeplayMenu.IsAtRoot`, no overlay), `MoveTaskToBack` like other apps.
  - **Shared pointer** (`UI/PointerInput`): `Game1` feeds it `TouchControls.LastTouches` (touches
    are read once per frame: MonoGame ages them on every `TouchPanel.GetState()`) right after
    `TouchControls.Update`. `GetState()` = the mouse on desktop; on Android a finger moves the
    pointer (hover) without pressing, **lifting it without a drag = left click for one frame**,
    a drag past `max(24, 3.5 % of height)` px scrolls (wheel notch per 4 % of height, plus
    `DragDeltaY` for 1:1 lists) and never clicks, holding still `LongPressSeconds` (0.5 s) = a
    right click (options menus). `GetRawState()` (HUD editor) = left held while the finger is
    down. Touches starting on a visible D-pad / A / B are left to those buttons. Used by
    `Game1` (`rawMs`: Settings, pause, error / loading screens), `MainSettingsMenu`,
    `ReplayBrowser`, `EditorPicker`, the style pickers, `HudEditor`. Freeplay and the online menu
    keep their own touch code (Freeplay got a long press = options; the online menu's
    `TouchAsMouse` is unchanged).
  - **Menu buttons auto-hide**: Settings → CONTROLS → **Menu Buttons** AUTO / ON / OFF
    (`Game1.MenuButtonsMode`, settings.json `MenuButtonsMode`, default AUTO). In AUTO the D-pad,
    A / B and Q / E are hidden (not drawn, no input, `IsPointOnMenuControl` false) where
    `TouchControls.ScreenIsTouchReady` is set: Game1 sets it every frame except in the chart
    editor, HUD editor, Level Selector and key-binding overlay; the online menu's own instance
    always sets it. Gameplay (hitboxes + pause button) is unaffected.
  - **D-pad geometry**: a real cross fully on screen (Down used to be below the screen edge and
    Left/Right off-centre); A bigger, B lower-left of A; Q / E moved above B / A (they covered
    the Settings title and tab bar).
  - **Settings**: tabs / rows / `< >` were D-pad only (no touch reached `UpdateMouse`). Now: tap
    a tab, tap a row (toggles / actions; adjustable rows just select), finger-sized `< >` with a
    button background, hold `< >` to repeat (after 0.5 s, every 90 ms), drag scrolls 1:1,
    `< BACK` button. Pointer selections no longer re-centre the list (`_followSelectionScroll`
    off; with touch it made the next tap land on another row and undid drags); a finger never
    hover-selects.
  - **Pause menu**: items record hit boxes in `Draw`; hover selects, click / tap runs (desktop
    mouse too). **Loading screen**: a click / tap anywhere = Enter ("Tap to play"; with the
    D-pad hidden it was a dead end). **Replays**: on touch the first tap selects a row, a tap on
    the selected row watches it (no hover-select while scrolling).
  - **Texts**: key hints are replaced on touch (`PointerInput.IsTouch`): main menu, Freeplay
    footer / buttons (`Controls.Tag(action)` = "[key] " or nothing), Settings, pause, pickers,
    attempts, replays, Editor Hub, online main menu, loading / opponent-disconnected / online
    results prompts, "ESC  Back" → `PointerInput.BackLabel` ("<  Back").
  - Not done: HUD editor touch buttons (save/reset/grid/toggle), Chart Editor / Level Selector
    by touch, the online menu's other keyboard hints (room browser footer, "ESC Disconnect").

**Note:** the app's default mods path is its external files directory. The
in-game instructions (`ModsHelpScreen`) and `PlatformPaths` messages explain
that content is **imported** by selecting a folder containing a `mods`
subfolder (SAF), not read directly from `Documents`.

---

## 14. Mods and assets

Mods root: desktop `<exe dir>\mods`; Android app external files `mods`.

| Subfolder | Content |
|---|---|
| `levels/` | `.fnmm`, `.zip`, `.rar` level files (recursive) and unpacked level folders |
| `levels/Backups/<Level>/original.fnmm` | Chart Editor backups (ignored by Freeplay) |
| `notestyles/<name>/` | note skins (see below); `Default/` is the built-in one; `osu! <skin>/` = imported .osk (§14.2) |
| `osu/<set>/` | (fork) extracted osu! beatmap sets (.osz): `.osu` difficulties, audio, background (§14.2) |
| `notesplashes/<name>/` | splash PNG + XML atlas; `Example/` is a template |
| `fonts/` | `.otf` / `.ttf` directly in the folder |
| `countdowns/<name>/` | `ready.png`, `set.png`, `go.png`; `Default/` is the built-in one |

### Note styles

Supported layouts (both PNG + XML atlas):
- **Format 1 / classic**: `NOTE_assets.png` + `NOTE_assets.xml`
  (or `fmm.png`, `notes.png`, `default.png`, `sonic.png`).
- **Format 2 / V-Slice**: `notes.png`+`notes.xml` and
  `noteStrumline.png`+`noteStrumline.xml`.
- `NoteStyle` loads these via `LoadFromFolder` / `LoadByName`; it scans for
  candidate PNGs, pairs the same-base `.xml`, and parses TexturePacker-style
  `SubTexture` frames.
- The default style is `mods/notestyles/FMM (Default)/` (FMM note system;
  `DefaultMods.NoteStyleName`, `NoteStyle.LoadDefault`, shipped-copy fallback).
  The old setting value `"Default"` still means the default
  (`DefaultMods.IsDefaultNoteStyle`) and `Settings.Load` rewrites it to the new
  name. The previous default (fmm/notefmm atlases) was deleted; the same look
  ships as `FMM (XML)/`.
- Skins without a hold texture borrow `Assets/images/NOTE_hold_FMM.png`.
- In atlas skins, `skin.json` `colorizeByLane` now wins over the auto-generated
  `config.json` value (it used to be silently overridden).
- `GetAvailableStyles()` lists the default first and skips folders named like it
  (`FMM (Default)` or a stale `Default/` from older builds).
  It also lists FMM-system folders (no XML needed).
- **FMM note system (fork, `FmmNoteSystem.cs`).** `skin.json` `"noteSystem": "fmm"`
  switches a style from atlases to the original FunkyMaker Mobile format, recreated
  from `References/hud.hx` (a Codename Engine HUD script):
  - Files: `note.png` (required: one UP arrow, used for every lane and for the
    strums), `goodnote.png` (positive/O), `badnote.png` (negative/X),
    `holdtrail.png`, `holdend.png` (rows trimmed to visible pixels at load).
  - Notes rotate per lane (L −90°, D 180°, U 0°, R 90°) and are tinted with
    `noteColors`; strums are tinted `strumColor` (0x828282) at alpha 0.85;
    holds use `noteColors`, alpha 0.84, width ×0.8/0.7; 60 % drop shadow
    10 px (at 720p) below notes, holds and strums (`dropShadow`).
  - Strum animation (`FmmNoteSystemRenderer`, ratios of the rest size 0.7):
    key press → 0.45 + `pressedColors`; hit → 1.0 + pressed colour; lerp back
    (fpsLerp 0.2); grey again inside [0.55/0.7, 0.7/0.65] of rest. The script's
    literal rule never greys after a hit; the user confirmed the original does,
    so the band is mirrored above rest. Active holds re-hit once per step (FMM
    sustain pieces are individual hits). No receptor intro / glow / note pop.
  - Splash lane colours use `pressedColors` (hud.hx `onSplashShown`).
  - `NoteRenderer` branches on `_fmm` in `SetStyle`, `UpdatePlayer` (press/hit),
    `UpdateAutoplay`, `TriggerManualHit`, end of `Update`, `DrawReceptor`,
    `DrawNoteHead`; holds go through `INoteStyleRenderer`.
  - Load also bakes rotated + tinted copies into `NoteFrames` /
    `ReceptorFrames` / `HoldPieceFrames` / `HoldEndFrames` so the Arrows menu,
    key-binding screen and chart editor (Codename hold path, now also taken for
    `NoteSystem == Fmm`) work unchanged. ~28 textures of the note size.
  - Default scale when `noteScale` is absent: 102 px / `note.png` width.
  - Verified in game (autoplay, F-Boys): rotation, tints, lit/grey strums,
    holds with caps. Chart-editor preview not visually checked.
- **Opacity / miss sounds (any skin):** skin.json `noteAlpha`, `strumAlpha`,
  `holdAlpha` (NoteStyle; holdAlpha null = renderer default: atlas 0.85/0.75,
  Codename 0.7, FMM 0.84; FMM strum default 0.85). Miss sounds: `miss`,
  `miss1..9`, `missnote1..9` (.ogg/.wav/.mp3) in the skin folder, one at random
  per player miss (`NoteStyle.PlayMissSound`). None are bundled yet.
- **Arrows menu (`UI/ArrowsMenu.cs`)** uses the Note Splash menu's layout: an
  info block (type: "Funky Maker" / "Codename", no description, scales, whether negative/
  positive notes have their own images, holds) and a preview with labelled rows
  STRUMS / NORMAL / NEGATIVE (X) / POSITIVE (O). Notes without their own X/O
  image are drawn like gameplay: tinted (atlas) or lane-coloured (FMM) with the
  X/O overlay. Rows shrink to fit short windows. Verified on screen for both
  bundled skins.
- `Assets/data/notestyles/funkin.json` / `pixel.json` exist but nothing loads
  them (`LoadFromFolder` only reads `funkin.json` inside the style folder).

### Note splashes

`NoteSplashStyle.LoadFromFolder` looks for `noteSplashes.png`, `splash.png`,
`notesplash.png` (or any PNG) **paired with an XML atlas**. `splash.json` is
optional per-splash configuration (offsets, framerate, etc.). Frame names
include `note<dir> splash`, `<color> splash`, `note impact 1 <color>`, etc.

- **Default splash (fork):** `mods/notesplashes/FMM (Default)/`, a recreation
  of the original FMM splash (`noteSplashes.png/.xml`, generic `splash` frames,
  rotated + trimmed 700×700 Funkin Packer frames). `DefaultMods.SplashName`;
  old setting value `"Default"` (which meant the code-drawn splash) maps to it.
  The code-drawn splash is now the built-in **"Simple"**; built-ins
  (`NoteSplashStyle.IsBuiltIn`) are "Simple" and "Particles: …".
- **Playback (fork fix):** frames play once at `fps`; length = frames / fps
  (`NoteSplashStyle.Duration` is computed; `splash.json` `duration` is ignored).
  It used to stretch the frames over a fixed 0.3 s with a fade and draw at raw
  pixel size (700 px frames filled the screen).
- **splash.json:** `fps`, `scale`, `alpha`, `fade`, `colorizeByLane`, `colors`
  (per lane, hex or [r,g,b]; overrides colorizeByLane), `offsetX`, `offsetY`.
  FMM (Default) uses scale 0.5 and FMM's pressed colours (hud.hx onSplashShown).
- Splashes trigger only on Sick! hits (`NoteRenderer.TriggerSplash`), and now
  also on botplay hits on the player strumline (as in FNF).
- The red "combo break" circle drawn on the strum after a miss was removed
  (users read it as a splash on miss); `_comboBreakTimer` is still counted.
- Note Splash menu: "Type: Codename" (texture) / "Type: Code" + "Built in";
  the preview plays with the same frame/tint/alpha code as gameplay.

### Countdowns

Sounds (fork): one per beat, `three`, `two`, `one`, `go` (or FNF `intro3`,
`intro2`, `intro1`, `introGo`), `.ogg`/`.wav`/`.mp3`, per sound falling back to
the default pack (`CountdownOverlay.LoadSounds`, played on each step change).
Volume = master × SFX Volume (`SoundFiles.Play`). No sounds are bundled yet.
The first beat (step 3) draws nothing (the old text "3" fallback was removed).

Folder with exactly `ready.png`, `set.png`, `go.png`; selected in settings
(Countdown Style). The setting value for the default is the legacy lowercase
`"default"`, which maps to `mods/countdowns/Default/`; scans skip a folder
named Default (case-insensitive). Both `SettingsManager` and `CountdownMenu`
scan `PlatformPaths.GetCountdownsPath()` (the settings scan used
`AppContext.BaseDirectory\mods` before, which is wrong for single-file/Android).

### Fonts

`FontMenu` lists every `.otf`/`.ttf` in `mods/fonts/`, with `MilkyNice.otf`
first as "MilkyNice (Default)". The picked font is **not persisted**; at startup
`Game1.LoadContent` uses `mods/fonts/main.otf` > `main.ttf` > `MilkyNice.otf` >
`MilkyNice.ttf` > the shipped copy.

### 14.1 Default mods (`DefaultMods.cs`, fork)

The default note style, countdown and font live in `mods/`, not in `Assets/`,
so players can see them and copy them as examples.

- Source: repo `DefaultMods/` (layout = `mods/`, incl. a README per folder).
- Shipped copy: desktop `<AppContext.BaseDirectory>/mods` (single-file publish:
  the self-extract temp folder; normal build: the real mods folder itself).
  Android: APK `mods/**` extracted to `FilesDir/DefaultMods`, re-extracted when
  the assembly version changes (`.version` marker).
- `DefaultMods.EnsureInstalled()` (called in `LoadContent` right after
  `FindAssetsRoot`, and lazily by `Bundled()`) copies every shipped file **missing**
  from `PlatformPaths.GetModsBasePath()`. It never overwrites, so player edits
  stay and deleted defaults return on the next start (verified on a win-x64
  publish). Consequence: when a new version changes a default file, players who
  already have it keep the old one.
- `DefaultMods.Bundled(parts)` gives the untouched shipped path (null when it is
  the same folder as the mods folder) and is the fallback when a player's copy
  is broken or deleted mid-session.
- `FindAssetsRoot` now detects `Assets/` by `LoadingScreen/AnkushCat.png` (was
  `shared/images/fmm.png` / `Font/MilkyNice.otf`, which moved); the Android
  extraction list no longer includes `Font` / `shared`.
- Not yet verified on a device. The Android build compiles (2026-11-19, arm64 test APK).

### Bundled assets

- `Assets/` contains popup art, UI/multiplayer art,
  loading-screen cat, touch hitbox art, and style configs.
- `DefaultMods/` at repo root is shipped as `mods/`; it is content, not source.

---

### 14.2 osu! support (fork, 2026-11-21)

Semi-separated from the FNF side: the format lives in `Osu/`, gameplay hooks in `Game1.Osu.cs`,
Freeplay in `FreeplayMenu.Osu.cs`. Only **osu!mania 4K** is supported. Research notes come from
ppy/osu (MIT): `LegacyBeatmapDecoder`, `ConvertHitObjectParser`, `ManiaBeatmapConverter`,
`LegacyManiaSkinDecoder`, `LegacySkin`, `LegacyManiaColumnElement`, `LegacyNotePiece`.

- **Import** (.osz beatmap sets, .osk skins): drag-and-drop anywhere (`Game1.TryImportOsuFile`,
  checked before FNF formats) or Freeplay IMPORT (the picker accepts them in both tabs; the
  OSU!MANIA tab filters to them). `OsuLibrary.Import`: a .osz is opened as a ZIP, its root `.osu`
  files are read (header only) and the set is **rejected unless one difficulty is mania
  (`Mode: 3`) with 4 keys** ("no es de osu!mania" / "no tiene dificultades de 4 teclas").
  Accepted sets are extracted (zip-slip guarded) to `mods/osu/<BeatmapSetID Artist - Title>/`
  — like osu! itself — so each difficulty is a real `.osu` file. A .osk needs mania art
  (`mania-note*`/`mania-key*`) or a skin.ini `[Mania]` section and is extracted to
  `mods/notestyles/osu! <Name>/` (skin.ini at the root or in one wrapping folder).
- **Parser** (`OsuBeatmap`): `osu file format vN` (N < 5 → +24 ms on every time, like osu!);
  [General] AudioFilename / AudioLeadIn / PreviewTime / Mode; [Metadata] Title(Unicode),
  Artist(Unicode), Creator, Version, BeatmapID, BeatmapSetID; [Difficulty] CircleSize (= keys,
  `round`, ≥ 1), OverallDifficulty, HPDrainRate; [Events] background (`0,0,"file"`);
  [TimingPoints] (time, beatLength, uninherited); [HitObjects] column =
  `clamp(floor(x · keys / 512), 0, keys − 1)`, hold = type bit 128 with the end time before the
  first `:` of field 6. `//` comments skipped; bad lines skipped. Main BPM = the uninherited
  beat length that lasts longest.
- **Level** (`OsuLevelLoader`, dispatched by `FnmmLoader.Load` / `TryLoadChartData` for
  `.osu`): an in-memory `FnmmLevel` with `IsOsu` and `OsuExtras` (below), one strumline (centred by the existing
  1-strumline layout) with the 4 columns as lanes, times in seconds (`Data.Version = 2`),
  `Bpm` = main BPM, empty landscape scene, `MusicData` = the audio file (usually **MP3**),
  `BackgroundImage` = the background. The .osu is never modified; the Chart Editor refuses
  `.osu` (toast). **FNF rules** are used (judgement, score, health, modifiers, attempts,
  replays — the replay hash is the .osu file's, and `FindLevelForReplay` also searches
  mods/osu). Not converted: OD hit windows, HP drain, keys other than 4.
- **Settings** (`Osu/OsuSettings.cs`, settings.json `"Osu"`, Settings → GAMEPLAY → "osu! songs"):
  Note Skin (Beatmap's / Mine), Scroll Speed Changes, Hitsounds + Hitsound Volume, Video,
  Storyboard, Background Dim (default 70 %). Only osu! levels use them.
- **Extras** (`OsuLevelExtras`, built in `OsuLevelLoader.BuildExtras` on the loading thread;
  wired by `Game1.SetupOsuExtras` at the end of SetupLevel, per frame `UpdateOsuExtras` in
  UpdateGameplayWorld, drawn by `DrawOsuBackground` before the scene and `DrawOsuOverlay` after
  the notes; images are kept across restarts, freed with the level textures):
  - **Beatmap skin** (checked in ppy/osu: `LegacyBeatmapSkin` has `AllowManiaConfigLookups =>
    false`, is configured from the .osu and not a skin.ini, and `AllowHighResolutionSprites =>
    false`): a set folder with `mania-note*` images is shown with them when Note Skin =
    Beatmap's (`Game1.LevelNoteStyle()` → `OsuManiaSkin.Load(folder, beatmapSkin: true)`:
    images only, no skin.ini / [Mania], no @2x preference, no miss sounds); otherwise / if it
    has no usable note image, the chosen note style.
  - **Scroll speed changes** (`OsuLevelLoader.BuildScroll` → `ScrollTimeline`, set on every
    `NoteRenderer.Scroll`): osu!mania's Sequential algorithm, multiplier = SV ×
    mostCommonBeatLength / beatLength (SV = 100 / −beatLength of an inherited point, clamped
    0.01..10; an uninherited point resets it to 1; same time → the inherited point wins). Notes /
    holds are drawn by `ScrollDistance` (integral of the multiplier) instead of time difference.
    Judgement is still by time. Null (constant speed) when nothing changes.
  - **Hitsounds** (`OsuHitsounds`): per note, bank / custom index / volume from the hitSample or
    the timing point at end time + 5 ms (bank 0 → [General] SampleSet); hitnormal only with the
    normal flag or no flags, + whistle / finish / clap with the addition bank; a hitSample
    filename plays only that file (keysounds); index ≥ 1 → the map's
    `{bank}-hit{name}{index ≥ 2 ? index : ""}` (.wav/.ogg/.mp3), else the selected note style
    when it's an osu! skin, else nothing (osu!'s default samples aren't shipped). Holds sound at
    the head only. Played on key press for the column's next note (osu!mania key sounds; also in
    replays) and on autoplay hits; volume = sample volume × Hitsound Volume × master.
  - **Storyboard** (`OsuStoryboard`): the set's .osb then the .osu [Events], [Variables];
    Sprite / Animation / Sample, commands F M MX MY S V R C P(H V A) with chained values, L loops
    (period = last end − first start, iterations = count), T groups skipped; the 35
    osu-framework easings; origins by name or number; 640×480 → ×1.5 centred (x 0 → 160 px;
    widescreen −107..747 fills 1280); alpha > 1 wraps; elements are drawn only between their
    first and last command. Layers Background / Pass / Fail / Foreground behind the notes,
    Overlay above (dimmed); Pass / Fail by health ≥ 50 %. When the Background layer shows the
    map's background image, the plain background isn't drawn. Images load in SetupLevel
    ("Loading storyboard..."); premultiplied AlphaBlend and a premultiplied additive blend.
  - **Video** (`OsuVideo`): MonoGame has no usable video decoding (DesktopGL none, and the XNA
    `VideoPlayer` needs pipeline-built WMV), so frames come from a platform decoder behind
    `IOsuVideoStream` (`ReadFrame(rgba)` → video time, thread-safe `Cancel`), opened and read on a
    reader thread per run (`Session`: 6-frame queue, own Ended flag, so a killed old run can't
    end the new one); the game thread uploads the newest due frame (640×360 RGBA, letterboxed).
    **Video Quality** (Settings → osu! songs, `OsuSettings.VideoFullQuality`, default Standard):
    Standard decodes to 640×360 @ 30 fps; Full probes the file (`FfmpegVideoStream.Probe` parses
    `ffmpeg -i` stream info; `MediaCodecVideoStream.Probe` reads the MediaExtractor track) and decodes
    at its own size / fps, capped to 4K (1080p on Android) and 60 fps, falling back to Standard if the
    probe fails. Each `OsuVideo` carries a `VideoFormat`; the frame queue shrinks for big frames
    (64 MB budget, 2–6 frames). Changing it mid-song reopens the video. ffmpeg runs with
    `-fps_mode cfr` so frames land on their real timestamps (AVI null frames / late first frame used to
    shift the whole video early, e.g. Mesmerizer by ~2.5 s).
    Starts at the Video event time from the video's beginning, fades in / out over 500 ms,
    reopens the decoder at the new time after seeks / restarts. Restart checks (behind > 1 s,
    back > 0.25 s) only use frames of the *current* run (`Session.Shown`; before its first frame
    only "skipped back before `Requested`"): `_shownTime` keeps the old run's frame on screen, and
    comparing against it restarted the decoder every frame after a forward timeskip / lag spike, so
    the video froze for good (fixed 2026-11-23). Mid-video starts decode from `videoTime + _lead`
    so the first frame isn't already late; `_lead` is learned from the decoder's start-up time
    (×1.25 + 0.1, 0.15–4 s; MediaCodec decodes from the previous keyframe), no lead while paused.
    - *Desktop* (`FfmpegVideoStream`): ffmpeg pipe `-ss T -i v -an -vf scale+pad -r 30 -f rawvideo
      -pix_fmt rgba -`. The shipped ffmpeg is a **minimal static LGPL build** (~5.5 MB vs ~138 MB):
      `Tools/build-ffmpeg-min.sh` (ffmpeg 8.0 source, `--disable-everything` + only file/pipe,
      mov/avi/flv/matroska/asf/mpegps/mpegts/ogg demuxers, h264/hevc/mpeg4/msmpeg4/h263/flv/vp6/
      vp8/vp9/mpeg1-2/wmv/vc1/mjpeg/theora decoders, scale/pad/fps/format filters, rawvideo out,
      `-static`; licence + buildconf written to `Tools/ffmpeg/LICENSE.txt`). `Tools/fetch-ffmpeg.ps1`
      runs it through MSYS2 UCRT64 (`C:\msys64` or `$env:MSYS2_ROOT`; pacman installs make/nasm/
      diffutils), or with `-Full` / without MSYS2 downloads the full BtbN LGPL build. Output in
      `Tools/ffmpeg/` (gitignored, build dir `Tools/.ffmpeg-build/` too); the csproj copies the exe +
      licence next to the game, excluded from single-file. Linux: `bash Tools/build-ffmpeg-min.sh`.
      ffmpeg is looked for next to the exe (`PlatformPaths.GetAppBaseDirectory()`, the real exe
      folder: in the single-file Release `AppContext.BaseDirectory` is the temp extraction folder,
      which doesn't have ffmpeg), then `AppContext.BaseDirectory`, `mods/tools`, PATH; without it the background
      image stays. Pipeline verified with the minimal build (mp4/h264 → 30 fps RGBA).
    - *Android* (`Platforms/Android/MediaCodecVideoStream`, `#if ANDROID`): MediaExtractor +
      MediaCodec (device decoders, nothing bundled), synchronous, YUV_420_888 `Image` → nearest-
      neighbour BT.601 RGBA on the CPU (only the sampled rows copied via the direct buffer address),
      seek with `PreviousSync` then drop frames before the target, frames < 1/30 s apart dropped.
      Containers MediaExtractor reads (mp4/mov, mkv/webm, 3gp, ts). **AVI** (MediaExtractor can't,
      and many beatmaps ship one, e.g. Mesmerizer's H.264 `vid.avi`) goes through `Osu/AviDemuxer`
      (pure C#, platform-neutral): scans the movi chunks of the first video stream (RIFF-AVIX parts,
      `rec ` lists; zero-size null chunks keep their slot), keyframes from idx1 flags or the bitstream,
      codec config as `csd-N` (H.264 SPS/PPS from avcC or the first keyframe, avcC frames converted to
      Annex B; MPEG-4/Xvid VOL header) → `video/avc` / `video/mp4v-es`. Frames are fed in decode
      order with slot times; output times come from a min-queue of pending input times (correct with
      B-frames). Verified 2026-11-26 on the A36 (Mesmerizer 1280×720 at Full). flv/wmv still don't play.
      **1080p at Full** (e.g. Cubibibibism, 1920×1080 30 fps) used to choke: the single-threaded
      conversion couldn't keep up, the video fell > 1 s behind and restarted every 1-3 s. Now the
      conversion runs in parallel row bands (≤ 4, own row buffers, per-column offsets precomputed),
      frames already late for the game clock (`IOsuVideoStream.SetClock`, fed by `OsuVideo._clockNow`)
      are decoded but not converted (at most 0.25 s in a row), and `OsuVideo` reuses frame buffers
      (`_freeBuffers`; was a new 8 MB array per frame, GC churn on both platforms; the letterbox bars
      are repainted each frame since buffers aren't zeroed anymore). A36 Debug build: ~30 fps, dips to
      ~21 fps in busy parts, no restarts.
      Compiles; not yet tested on a device.
  - **Background Dim** is a black rectangle over background + video + storyboard.
  - Freeplay details list "video", "storyboard" and "own note skin" for sets that have them
    (`OsuSet.HasVideo / HasStoryboard / HasSkin`).
- **Audio**: `AudioDecoder.LoadWithPcm` picks OGG (`OggLoader`) or MP3 (NLayer `MpegFile`,
  decoded to 16-bit PCM, resampled with `OggLoader.Resample` if MonoGame can't play the rate)
  — used by SetupLevel and the audio-recovery path. The Freeplay preview streams either
  (`ISampleSource`: `VorbisSource` / `Mp3Source`) from the map's `PreviewTime` (none → 40 %).
  MP3 decoders can differ by a few ms (encoder padding); no extra offset is applied yet.
- **Background**: `Game1.SetupOsuBackground` (end of SetupLevel) makes the texture,
  `DrawOsuBackground` draws it cover-fit at 25 % brightness before the scene, freed with the
  level textures.
- **Freeplay**: FNF / OSU!MANIA tabs (header, Q / E or click; saved as `FreeplayData.Tab`).
  `ScanOsuSets` lists `OsuLibrary.ScanSets()` (headers only, cached per folder time): one row
  per set (`Artist - Title [Diff] +n`), its path = the chosen difficulty's .osu
  (`FreeplayData.OsuDifficulty[set] = file`), so stars / preview / attempts / replays are per
  difficulty. Left / Right (or the < > arrows under the title) change the difficulty in the
  OSU!MANIA tab (they change the rate in the FNF tab). Details show "mapped by", the selector,
  and tiles TAPS / HOLDS / OSU! OD / 4K DIFFICULTIES.
  **Backgrounds as banners** (like osu!'s song select): each osu! row is drawn over its
  background (`LevelIcons.Banner`, 384x216 centre-cropped, made with the icons; no square icon
  then), darkened by a left-to-right gradient (`DrawShade`) so the title reads. The details
  hero becomes a banner too (`DrawCover` of the selected song's background — loaded one at a
  time at up to 1280 px wide by `GetDetailBackground`, the list banner meanwhile — with name,
  meta, difficulty selector and stars over it), and the background is drawn at 12 % behind the
  whole details side. (The square cut, `CoverIcon`, is still made for places that want an icon.) Favourites of osu! songs belong to the set folder. NEW is disabled; EDIT CHART is
  disabled; DELETE SONG removes the whole set folder (`LevelFiles` accepts mods/osu too).
  Each tab remembers its selection while the game runs.
- **Skins** (`OsuManiaSkin`, picked up by `NoteStyle.LoadFromFolder` / `GetAvailableStyles`
  when a notestyles folder has `mania-note*` images or a skin.ini, and no skin.json): skin.ini
  `[Mania] Keys: 4` → `NoteImage{i}`, `NoteImage{i}H/L/T` (0-based), default names
  `mania-note{1|2}{,H,L,T}` with columns 1,2,2,1; `name@2x` preferred, frame `-0` of animations.
  Everything is resized to one width (note = 95 % of the lane). It becomes a normal atlas
  `NoteStyle`: notes = note images; receptors are generated from the note (idle dim, press,
  confirm bright) because mania key images are the key area under the column; the hold texture
  is the 8-column layout (body = L stretched, cap = T → H → note), flipped vertically for FNF
  upscroll (the renderer flips it back for downscroll = osu!'s orientation). Not used: key
  images, stage art, lighting, column widths/colours, NoteBodyStyle repeat, hit images.
- **Bug-fix pass (2026-11-21)**:
  - *Orientation*: osu! skins are drawn for downscroll. `OsuManiaSkin` flips the note images
    (and the receptors made from them) when `!NoteRenderer.Downscroll`. The tail column is not
    flipped any more (it was reversed); the body column still is. `NoteStyle.OsuSkinFolder` /
    `OsuBuiltForDownscroll` record this, and the style is reloaded when the scroll direction changes.
  - *Live settings*: `Game1.StyleSync.cs` `SyncChosenStyles()` (every frame, from Update's
    finally) reloads the note style or splash when their names change, reapplies
    `LevelNoteStyle()` to the renderers, and calls `CountdownOverlay.EnsureLoaded`.
    `UpdateOsuExtras` loads or disposes the storyboard and video, and switches SV, when the
    toggles change mid-song. The Settings countdown list rescans at most every 2 s.
  - *Storyboard*: `LoadExact` (no padding to multiples of 4, which shifted origins). A
    non-widescreen storyboard is scissor-clipped to the 4:3 area (`ClipRaster`).
  - *Video*: before its start time (e.g. a restart's countdown) the stream is stopped and reset.
    The decoder is pre-rolled from `max(0, t)` with alpha 0, then fades in.
  - *Import*: each .osu is parsed in its own try/catch, so non-mania difficulties no longer
    abort the set. .osz/.osk files dropped loose into mods/osu are imported by `ScanSets`
    (`ImportLooseArchives`). Freeplay's `OnLevelsImported` routes .osz/.osk from the Android
    picker to `OsuLibrary.Import`. The SAF folder import copies `mods/osu` too.
  - *Android paths*: `CaseInsensitivePaths` (HasFileWithExtension / Find) is used for splash,
    note-style and countdown listing and loading (the Android filesystem is case-sensitive).
  - *Android Freeplay*: a tap selects only on release without a drag; a drag scrolls the list.
    The drag-and-drop footer has no hit area on Android, and its texts say "TAP TO IMPORT".
  - Verified on a real phone (Galaxy A36, Android 17, via adb, 2026-11-21): Lagtrain storyboard
    (its gray full-screen Overlay sprite and its own strumline are intended: the map draws its
    notes in the storyboard), live storyboard toggle, loose .osz in mods/osu, picker import of
    .osz, B vs footer, drag-scroll, Test countdown/splash/font with upper-case file names.
  - Found while testing on the phone, also fixed:
    - Game Over buttons ignored touches (it read only the mouse): Android now clicks on finger
      lift (`_gameOverTouchDown/_gameOverTouchPos` in Game1). Before, losing soft-locked Android.
    - Freeplay (Android): hover-select is off; the first tap on a row selects it, a tap on the
      selected row plays it (`HandlePanelClick`); a drag never changes the selection.
    - UI sounds and online menu music were silent on Android: TitleContainer paths had an
      "Assets/" prefix, but APK assets live at the root (`sounds/ui/x.wav`). Both paths are tried.
    - `NoteSplashStyle.LoadFromFolder` matched file names exactly (listing was fixed, loading
      wasn't): now case-insensitive, also splash.json.
    - The chosen UI font was never saved (every start went back to MilkyNice, on all platforms):
      `Settings.UiFontFile` (file name in mods/fonts) + `Game1.ApplyUiFont`, applied right after
      `Settings.Load`.
    - Log spam: TouchControls "Pause button visible" every frame; countdown rescan every 2 s.
  - ~~Still not touch-friendly on Android: pause menu, Settings list/tabs~~ fixed 2026-11-23 (§13
    "Touch UI"). A .osz rejected as non-mania stays in mods/osu and is re-read
    on every refresh.
  - Android testing recipe: adb at %LOCALAPPDATA%\Android\Sdk\platform-tools; the debug APK
    installs as com.funkymakergame.player next to the official app; mods at
    /sdcard/Android/data/com.funkymakergame.player/files/mods; settings via
    `adb shell run-as com.funkymakergame.player cat files/Documents/settings.json`;
    `adb exec-out screencap -p`, `adb shell input tap/swipe` (screen 2340x1080 landscape;
    D-pad down ≈ (225,1020), A ≈ (2243,877), B ≈ (2080,862), Q/E ≈ (90,113)/(2250,113)).
- **Not done**: avi/flv/wmv videos on Android, storyboard T (trigger) groups, sample-set "sliding" sounds,
  osu! online play, other key counts, a global osu! offset.

## 15. Persistence summary

| File | Location | Owner |
|---|---|---|
| `settings.json` | `%AppData%\FunkyMakerPlayer\` (desktop); app storage (Android) | `Settings` |
| `multiplayer_settings.json` | `%AppData%\FunkyMakerPlayer\` (desktop); app personal folder (Android) | `MultiplayerSettings` |
| `leaderboard_data.json`, `match_history.json` | same MP storage dir (legacy copies next to the exe are migrated) | `LeaderboardManager` |
| `official_cache/*.fmc` | same MP storage dir | `LevelCache` |
| `chartonly_cache/*.fnmm` | same MP storage dir (outside `mods/levels/`) | `RoomClient` (online FNF Chart-Only mode, §12) |
| `level_hash_cache.json` | same MP storage dir | `LocalLevelIndex` (fork, §12.1) |
| `multiplayer_settings.<profile>.json` | MP storage dir | `FUNKYMAKER_MP_PROFILE` test profiles (fork, §12.0) |
| `mods_location.txt` | `%AppData%\FunkyMakerPlayer\` (desktop) | `ModsStorage` (fork, §13) |
| `replays/*.fmr` | MP storage dir | `ReplayStore` (fork, §12.4) |
| `mods/osu/<set>/` | mods root | extracted osu! beatmap sets (fork, §14.2) |
| `freeplay_data.json` | MP storage dir | `FreeplayData` (fork, §10.1, §8.7): per-level favourite / plays / last played / attempts (keyed by path relative to the levels folder, lower-case), last selected, sort, favourites filter, song rate + Slower / Faster rates, enemy mode |
| `mods/levels/Received/` | levels folder | room level downloads (fork, §12.1) |
| `hud_settings.json` | `<exe dir>\Assets\data\` via `GetAppBaseDirectory()` (desktop, fork fix for single-file builds); Android: `SpecialFolder.Personal` (app files/Documents, next to settings.json; was the read-only `/Assets/data` until 2026-11-23) | `HudSettings` |
| `update.log` | `<exe dir>` | updater scripts |
| `crash/crash_*.log`, `crash.log` | `<exe dir>\crash\` | `Program.HandleCrash`, `CrashLogger` |
| `crashes/` | Android external storage | `PlatformPaths.GetCrashLogsPath` |
| `version.txt` | next to exe | build copy; read by `Updater` |
| `mods/**` | next to exe / Android app external files | user content, preserved by updater |

---

## 16. Crash logging, updater, Discord RPC

### Crash logging

- `Program.HandleCrash` writes a timestamped log plus `crash.log` and shows a
  platform dialog (Windows MessageBox, Linux zenity/kdialog, console fallback).
- `CrashLogger.Initialize` hooks `AppDomain.CurrentDomain.UnhandledException`;
  on Android it shows a dialog once, on desktop it writes a file.
- `Game1.HandleGameplayError` handles level-load errors and logs via
  `CrashLogger` without necessarily crashing.

### Updater

- Since 0.4.7 the updater **does not block startup**. `Game1.Initialize`
  (Release builds only, `#if !DEBUG`) runs `Updater.CheckForUpdatesAsync()` in
  the background; release API:
  `https://api.github.com/repos/Thisisfenix/thisisfenix.github.io/releases`.
- Desktop: a hit calls `FreeplayMenu.SetAvailableUpdate(tag, version, url,
  changelog)`, which opens a modal on the main menu (Left/Right/Up/Down choose,
  Enter confirm, Esc = later). "Later" leaves a corner badge; clicking it or
  pressing `U` re-opens the modal. "Update now" calls
  `Updater.DownloadAndApplyUpdateAsync`, which reports progress to the modal.
- Windows: if `Assets/updater/FunkyMakerUpdater.exe` exists it is launched with
  `<downloadUrl> <pid> <version>`; otherwise the zip is downloaded and extracted
  to `%TEMP%\FMPUpdate_*` and a generated `.bat` (robocopy, xcopy fallback)
  copies it over the install **excluding `mods`**, logs to `update.log`, and
  restarts the game.
- Linux: extracts to `<exe dir>/.fmp_update_*` and runs a generated `update.sh`
  (via `setsid` when available) using `rsync`/`cp`, excluding `mods`, setting
  `DOTNET_BUNDLE_EXTRACT_BASE_DIR=<exe dir>/.fmp_runtime`, then relaunches.
- Android: native dialog that opens the releases page (§13).
- The old blocking `CheckAndUpdate()` path is still in `Updater.cs` but is no
  longer called.
- `FunkyMakerUpdater/` is a standalone source project (target
  `FunkyMakerPlayer_win.zip`); there is **no built exe** and no
  `Assets/updater/` in the repo.

### Discord RPC

- Desktop only (`#if !ANDROID`), client id `1476660744673169489`.
- `DiscordRpc.Initialize/Update/UpdateScore` update presence; updates are
  rate-limited and de-duplicated; there is a dedicated multiplayer lobby /
  matchmaking state and `ClearParty()`. `Game1.RefreshActiveDiscordPresence`
  is called on screen changes. Android stubs are empty.

---

## 17. Controls reference

### Main menu / Freeplay

| Key | Action |
|---|---|
| Up/Down | navigate |
| Enter | select / open level |
| Esc | open Settings (root) / close panel (level panel) |
| R | refresh level list (panel) |
| I or O | import level file (panel) |
| N | new chart (panel) |
| Ctrl+F or F3 | search levels (panel; Enter / Esc stop typing) |
| F / Shift+F | favourite the selected level / favourites only (panel) |
| Tab | next sort order (panel) |
| Shift+R | random level (panel) |
| C or Menu key / right click / "..." | options menu of the row (panel; also attempts window and REPLAYS) |
| M | modifiers window (panel) |
| H | attempts window of the selected level (panel) |
| Left / Right | song rate -/+ 0.05x (panel) |
| PgUp / PgDn / Home / End | page / ends of the list (panel) |
| U | re-open update prompt (root, when an update is available) |
| 7 | Editor Hub (root) / Chart Editor for selected level (panel) |
| Mouse | hover/click cards and rows, wheel scrolls the list |

### Gameplay

| Key | Action |
|---|---|
| D/F/J/K (default, rebindable) | lanes left/down/up/right |
| Esc | pause (solo) / non-pausing overlay (multiplayer) |
| P | pause (solo only) |
| Ctrl+Left / Ctrl+Right | timeskip backward/forward by `TimeskipSeconds` (Timeskip setting not OFF) |
| T / Shift+T (hold repeats) | timeskip forward/backward, only if `T` is not a lane key |
| PageUp / PageDown | visual scroll speed |
| `-` / `+` (also numpad) | master volume (global hotkey) |
| 7 | open Chart Editor (solo) / return to editor during playtest |

### Settings

(Android: tap tabs / rows, `< >` change values (hold repeats), drag scrolls, back gesture = Esc.)

| Key | Action |
|---|---|
| Q / E | previous/next tab |
| Up/Down | navigate items |
| Left/Right | adjust focused value, otherwise switch tab |
| Enter / Space | toggle / run action / focus an adjustable row |
| Esc | back |

### Chart Editor

See §11.

### HUD Editor

| Key | Action |
|---|---|
| Esc | save and go back |
| S | save |
| R | reset defaults (positions + sizes + visibility) |
| Drag corner handle (selected) | resize element |
| `[` / `]`, mouse wheel over selected | shrink / grow element |
| G | toggle grid |
| H | toggle instructions |
| A | toggle all elements |
| V / Space / Delete | toggle selected element |
| Right-click | select + toggle element |
| Mouse drag | move element |
| Arrows | nudge 5 px (Left Shift = 1 px) |
| Tab | cycle elements |

---

## 18. Known caveats, stale docs, and TODOs

Status key: **[fixed]** resolved in the current tree; **[open]** still applies
(suggestion included); **[by design]** intentional, no change planned.

1. **[fixed in fork]** **Root solution.** `FunkyMakerPlayer.sln` no longer
   references the missing `_inspect_tool\inspect.csproj`; it contains only the
   game project. Upstream still has the reference — re-apply after syncs.
2. **[fixed in fork]** **Ignore rules.** `.gitignore` has
   `!FunkyMakerPlayer/events/**` and `!DefaultMods/**` exceptions, ignores
   `FunkyMakerPlayer_win/`, and originally ignored `Context.md` (local-only on
   purpose); **updated 2026-11-22:** `Context.md` was removed from `.gitignore`
   (`!Context.md`) so it is tracked in git and pushed to the repository. Keep
   these exceptions when merging upstream.
3. **[fixed]** **Unpacked level folders.** Upstream (0.4.7) loads any folder with
   a `data.json` inside (recursive `FindLevelDir`); Freeplay lists folders with
   `meta.json` or `data.json`. The fork keeps dots in displayed folder names.
4. **[open]** **Psych Engine support removed.** `data/` folders without a
   `data.json` are still rejected; the fork's `StartLoadingLevel` catch turns
   the `PSYCH_ENGINE_MOD` marker into a friendly message. Implementing Psych
   import is not planned.
5. **[fixed]** **Stale bundled READMEs.**
   - `FunkyMakerPlayer_win\README.txt` referenced `settings.ini`; now
     `settings.json`.
   - `FunkyMakerPlayer_win\mods\README.txt` and `mods\levels\README.txt`
     advertised `.fmm`/`.zip` and `Inst.ogg`; now `.FNMM`/`.fnmm`/`.rar` and
     `music.ogg`.
   - `FunkyMakerPlayer_win\mods\notesplashes\README.txt` required `splash.json` +
     a plain horizontal `splash.png`; it now documents the required PNG+XML atlas
     pair and optional `splash.json`.
   - (An earlier revision of this doc attributed the `settings.ini` reference to
     `mods\README.txt`; it is actually in `FunkyMakerPlayer_win\README.txt`.)
6. **[fixed in fork]** **Skins misused as gameplay types.** Upstream treated
   skins as effects (skin 1 instakill when hit without an event, skin 3 heal,
   editor labels "Damage/Instakill/Heal", `IsDamage`/`IsInstakill`/`IsHeal`).
   Now skins are visual only (`NoteSkins`; `NoteProps.VisualSkin`,
   `IsNegative`, `IsPositive`), skin 3 is gone, the hardcoded fallbacks were
   removed, and the Chart Editor offers presets that link real action packs
   (§11). `NoteStyle` tints are `TintNegative`/`TintPositive` (JSON
   `tintNegative`/`tintPositive`; legacy `tintDamage`/`tintInstakill` still
   read, `tintHeal` ignored). Expect merge conflicts in `NoteRenderer`,
   `ChartEditor`, `NoteStyle`, `FnmmModels` and `Game1.SetupLevel` on upstream
   syncs.
7. **[resolved upstream]** **Timeskip.** Upstream 0.4.7 supports both
   Ctrl+Left/Right and `T`/Shift+T (only when `T` is not a lane key); the
   setting moved to EXPERIMENTAL and its description says so (marked
   "deprecated").
8. **[doc error, fixed]** **Action-pack index 0.** An earlier revision claimed
   index 0 never fires. In the code all note triggers use `>= 0`, and
   FunkyMaker writes `-1` for "no event", so index 0 fires normally.
9. **[fixed]** **Chart Editor `.rar` save.** `ChartEditor.SaveLevel` (upstream)
   changes any non-`.fnmm`/`.zip` target to `.fnmm`, and `FnmmSaver.Save`
   (fork) also redirects `.rar` to a sibling `.fnmm`; the original `.rar` is
   left intact. Loading sniffs magic bytes, so older misnamed saves still load.
10. **[open]** **Updater assets missing.** No `Assets/updater/FunkyMakerUpdater.exe`
    and no built updater; Windows falls back to the batch method. The old
    blocking `Updater.CheckAndUpdate()` is now dead code. **Suggestion:** ship
    the updater under `Assets/updater/` or drop the exe branch, and delete
    `CheckAndUpdate()`.
11. **[by design]** **Obfuscation not automatic.** `confuser.crproj`/`obfuscar.xml`
    exist; the Obfuscar MSBuild target stays commented out. **Suggestion:** wire
    the target up or remove the configs so builds are not assumed obfuscated.
12. **[by design]** **Disabled systems.** `Managers/AudioManager.cs` and
    `GameStateManager` remain commented out/disabled; audio uses `OggLoader` +
    `SoundEffectInstance` with PCM seeking. WAV files have no seek support.
    **Suggestion:** delete the dead files or keep this note.
13. **[by design]** **`FunkyMakerServerRepo` is an empty gitlink** (submodule
    pointer, no `.gitmodules`), so it cannot be checked out; multiplayer
    server source is external. **Suggestion:** add a proper `.gitmodules`
    entry or remove the gitlink.
14. **[fixed]** **Android instructions path mismatch** (see §13).
    `ModsHelpScreen`, `PlatformPaths.InitializeModDirectories`, and
    `GetCustomContentInstructions` now explain that the app imports from a
    user-picked folder containing a `mods` subfolder instead of reading
    `Documents → FunkyMakerPlayerPublicMods → mods` directly.
15. **[fixed in fork]** **`FreeplayMenu.ConsumeMenuAction()`** is now what
    `Game1` uses to read main-menu actions; the dead `MenuAction == 1` branch
    was removed.
16. **[by design]** **Legacy code excluded**: `Legacy/**` is not compiled; do not
    rely on it.
17. **[open]** **Version duplication.** Version lives in csproj (`0.4.8`),
    `version.txt`, Android `ApplicationDisplayVersion`, and the Android
    `ApplicationVersion`/`AndroidVersionCode` (`6`) must be bumped by hand;
    `app.manifest` `assemblyIdentity` is `1.0.0.0` (manifest identity,
    separate). **Suggestion:** derive all of them from one MSBuild property,
    and follow §20 when releasing.
18. **[fixed in fork]** **`hud_settings.json` in single-file builds.** On
    desktop `HudSettings` now uses `PlatformPaths.GetAppBaseDirectory()`
    instead of `AppContext.BaseDirectory` (the temp extraction folder in
    single-file publishes). (2026-11-23) Android: it used `AppContext.BaseDirectory` = "/",
    so `/Assets/data` was read-only and every save failed ("Save failed: Read-only file
    system"): HUD toggles and the HUD editor layout never persisted. Now
    `SpecialFolder.Personal` (next to settings.json).
19. **[fixed in fork]** **Linux file picker.** `PickDesktopFiles` falls back to
    zenity/kdialog on non-Windows desktops; without either it returns nothing.
20. **[by design]** **Multiplayer pause.** Esc in a match opens an overlay but the
    song keeps running (both players stay in sync); only `P` is blocked.
21. **[by design]** **`CorneredEventHandler.LoadHardJsonChart`** is unused dead
    code; it used to assign `Skin = laneId` (now `0`).
22. **[fixed in fork]** **FMM health scale.** FunkyMaker's max health is 30
    (= 100%). `EventProcessor` used to divide `PlayerHealth` value/minHealth by
    100; it now divides by `NoteSkins.FmmMaxHealth` (30).
23. **[open]** **Default health behavior vs FunkyMaker.** Start health `0.5`,
    hit `+0.023` and miss `-0.0475` (`Game1.SetupLevel`) are FNF values; it is
    not verified whether FunkyMaker starts at half health or changes health on
    plain hits/misses. Confirm against FMM before changing.
24. **[open]** **Protocol 4 (Cloudflare Workers) untested in-game** (§12.z). Rooms were played for
    real on the protocol 3 Render server (Mirror / Teams rounds with 2–3 players are in the match
    history). The Worker is deployed and passes 44 unit tests + `scripts/e2e.mjs`, and the client
    builds, but nobody has played on it with the real game yet: check connect / reconnect, room
    list, big level lists (fragmentation), chunked uploads + downloads (FNF, osu! `.osz`, chartonly),
    a full match, results, leaderboard / RECENT MATCHES and replays (use `FUNKYMAKER_MP_PROFILE` for
    a second window). The client changes live on branch `cloudflare-workers` until merged.
25. **[open]** **Package advisories (pre-existing):** `SixLabors.ImageSharp` 3.1.12 (moderate +
    high) is flagged by NuGet at build time (`SharpCompress` 0.39.0 was too; upstream bumped it to 0.48.1).

---

## 19. Where to edit common things

| Goal | File(s) |
|---|---|
| Add/modify FNMM JSON schema | `FnmmModels.cs` (+ `FnmmLoader.cs`, `FnmmSaver.cs`, `EventProcessor.cs`) |
| Change load/error behavior | `FnmmLoader.cs`, `FnmmConverter.cs`, `Game1.StartLoadingLevel`, `Game1.HandleGameplayError`, `UI/ErrorOverlay.cs` |
| Scene textures / atlases / scene draw | `FnmmSceneRenderer.cs`, `AtlasPacker.cs`, `SpriteAnimator.cs` |
| Note speed/judgement/health/holds | `NoteRenderer.cs`, `Game1.SetupLevel` callbacks |
| Note skins / avoid-notes / presets | `NoteSkins.cs`, `NoteRenderer.cs`, `NoteStyle.cs`, `Editors/Chart/ChartEditor.cs` (presets) |
| FMM-faithful note system | `FmmNoteSystem.cs` (loader, baking, renderer + strum animation), `NoteRenderer.cs` `_fmm` branches |
| Scene events/action packs | `EventProcessor.cs`, `FnmmModels.cs` |
| Camera behavior | `Managers/CameraManager.cs`, `Game1.BuildCameraMatrix` |
| Main menu / level list / update prompt | `FreeplayMenu.cs`, `Game1` menu dispatch |
| osu! beatmaps / skins | `Osu/*.cs`, `Game1.Osu.cs`, `FreeplayMenu.Osu.cs`, `AudioDecoder.cs` |
| Freeplay list, details, preview, icons, modifiers, bests | `FreeplayMenu.*.cs`, `FreeplayData.cs`, `Game1.Freeplay.cs` |
| Settings items/persistence | `Managers/SettingsManager.cs` (`Build*Entries`), `Settings.cs`, `Game1` statics |
| HUD layout/visuals | `HudSettings.cs`, `HudEditor.cs`, `MainSettingsMenu.cs`, `UI/GameplayHudOverlay.cs`, `UI/*.cs` |
| Window focus / input gating | `Game1.CanAcceptInput`, `OnActivated`/`OnDeactivated` |
| UI scaling | `UI/UiScale.cs` |
| UI sound effects | `UI/UiSfx.cs` (mapping/priorities), `Game1.UiSfx.cs` (screen diffs), the menu's own `Update` / action methods |
| Pause/menu flows | `PauseMenu.cs`, `Game1.UpdateInternal` |
| Mod paths/Android import | `PlatformPaths.cs`, `Platforms/Android/*` |
| Default note style / countdown / font | `DefaultMods/` (files), `DefaultMods.cs` (install + fallback) |
| Chart editing / new chart / playtest | `Editors/Chart/*`, `Editors/EditorPicker.cs`, `Game1.BeginEditorPlaytest` / `ReturnFromPlaytest` |
| Character editor / Sparrow & Spritemap import / online local equip | `Editors/Character/*`, `Editors/Chart/ChartEditor.Characters.cs`, `Multiplayer/MultiplayerSettings.cs`, `Multiplayer/MultiplayerMenu*.cs`, `Game1.LevelLoad.cs`, `PlatformPaths.cs` |
| Multiplayer protocol | `Multiplayer/Rooms/RoomProtocol.cs`, `WsHubConnection.cs`, `NetworkManager.cs`, `NetworkPackets.cs`; server `FunkyMakerTransferServer/worker/src/protocol.ts` + `hub.ts` (keep in sync) |
| Online server (rooms, transfers, leaderboard, settings) | `FunkyMakerTransferServer/worker/` (`src/registry.ts`, `leaderboard-book.ts`, `transfers.ts`, `wrangler.toml` `[vars]`), §12.z |
| Leaderboard / match history UI | `Multiplayer/MultiplayerMenu.cs` (`DrawLeaderboardModern`), `MultiplayerMenu.MatchHistory.cs`, `Leaderboard/*` |
| Official level cache / usernames | `Multiplayer/LevelCache.cs`, `LevelDownloader.cs`, `UserDirectoryClient.cs` |
| Room level sharing | `Multiplayer/Rooms/RoomClient.cs`, `Multiplayer/Transfer/*`, server `worker/src/transfers.ts` |
| Build/package/signing | `FunkyMakerPlayer.csproj` |
| Updater | `Updater.cs` (`CheckForUpdatesAsync`, `DownloadAndApplyUpdateAsync`), `FreeplayMenu.SetAvailableUpdate`, `Game1.Initialize`, `FunkyMakerUpdater/Program.cs` |
| Controls | `NoteRenderer.PlayerKeys`, `SettingsManager`, `KeyBindingOverlay.cs`, `Game1.UpdateInternal` |
| This document | `Context.md` (mandatory with any of the above) |

---

## 20. Verification checklist

After any change:

- [ ] `dotnet build FunkyMakerPlayer\FunkyMakerPlayer.csproj -c Debug -f net10.0 /p:TargetFrameworks=net10.0`
      succeeds (or Android workload is installed).
- [ ] `dotnet run --project FunkyMakerPlayer\FunkyMakerPlayer.csproj -f net10.0 /p:TargetFrameworks=net10.0`
      starts and reaches the main menu.
- [ ] At least one bundled/custom level loads and plays; scene, notes, events,
      countdown, pause, restart, and exit work.
- [ ] Settings open/change/persist across a restart.
- [ ] Chart Editor opens, saves (backup appears under `mods/levels/Backups/`),
      the saved level loads, and Enter-playtest + `7` returns to the editor.
- [ ] After syncing with upstream: the root `.sln` has no `_inspect_tool`, and this file's
      version/§3/§10/§11/§17 match the code.
- [ ] If touching FNMM format or paths, verify both ZIP `.FNMM` and legacy
      `.rar` (if available).
- [ ] If touching Android, verify `dotnet publish ... -f net10.0-android`
      and signing env vars.
- [ ] `Context.md` updated for architecture/format/controls/build changes.
