using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SixLabors.ImageSharp.Processing;
using FunkyMakerPlayer.Managers;  // ← NUEVO: Import de managers
using FunkyMakerPlayer.UI;  // ← NUEVO: Import de UI menus
using FunkyMakerPlayer.Multiplayer;  // ← NUEVO: Import de multiplayer
using MonoGame.Extended;  // ← MonoGame.Extended for OrthographicCamera

namespace FunkyMakerPlayer;

/// <summary>Discord Rich Presence and display names (moved out of Game1.cs).</summary>
public partial class Game1
{
    private string GetCurrentSongDisplayName(string? fallbackPath = null)
    {
        if (_isMultiplayerMatch)
        {
            if (_level != null && !string.IsNullOrWhiteSpace(_level.LevelName) &&
                !_level.LevelName.StartsWith("funkymaker_temp_", StringComparison.OrdinalIgnoreCase))
                return _level.LevelName;
            if (!string.IsNullOrWhiteSpace(_multiplayerLevelName))
                return _multiplayerLevelName!;
        }
        if (_level != null && !string.IsNullOrWhiteSpace(_level.LevelName))
            return SanitizeLevelDisplayName(_level.LevelName);
        string? path = !string.IsNullOrWhiteSpace(fallbackPath) ? fallbackPath : FnmmPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            string fromPath = SanitizeLevelDisplayName(Path.GetFileNameWithoutExtension(path));
            if (!string.IsNullOrWhiteSpace(fromPath))
                return fromPath;
        }
        return "Unknown Level";
    }

    private string? GetResolvedOpponentDisplayName()
    {
        string? candidate = _opponentName;
        string resolved = Multiplayer.UserDirectoryClient.Instance.ResolveUsername(_opponentPlayerId, candidate);
        if (!Multiplayer.UserDirectoryClient.IsGenericUsername(resolved))
        {
            if (Multiplayer.UserDirectoryClient.IsGenericUsername(_opponentName) ||
                !string.Equals(_opponentName, resolved, StringComparison.Ordinal))
            {
                _opponentName = resolved;
            }
            return resolved;
        }
        return null;
    }

    private void RefreshActiveDiscordPresence(bool immediate = false)
    {
        if (_isMultiplayerMatch && (_level != null || _loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing))
        {
            string? resolvedOpponent = GetResolvedOpponentDisplayName();
            string levelName = GetCurrentSongDisplayName();
            if (!string.IsNullOrEmpty(_multiplayerMatchId))
                DiscordRpc.SetParty(_multiplayerMatchId, 2, 2);

            if (_loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
            {
                DiscordRpc.SetLoadingMultiplayer(levelName, resolvedOpponent);
            }
            else
            {
                _multiplayerMatchStartUtc ??= DateTimeOffset.UtcNow;
                DiscordRpc.SetPlayingMultiplayer(levelName, resolvedOpponent, _multiplayerMatchStartUtc, immediate: immediate);
            }
            return;
        }

        if (_inMultiplayer && _multiplayerMenu != null)
        {
            var mpInfo = _multiplayerMenu.GetDiscordPresenceInfo();
            if (!string.IsNullOrEmpty(mpInfo.PartyId) && mpInfo.PartySize > 0)
                DiscordRpc.SetParty(mpInfo.PartyId, mpInfo.PartySize, Math.Max(mpInfo.PartyMax, mpInfo.PartySize));
            else
                DiscordRpc.ClearParty();

            DiscordRpc.SetMultiplayerLobby(mpInfo.Details, mpInfo.State, immediate: immediate);
            return;
        }

        if (_inEditor && _activeEditor is Editors.Chart.ChartEditor ce)
        {
            _editorSessionStartUtc ??= DateTimeOffset.UtcNow;
            DiscordRpc.SetChartEditor(ce.LevelDisplayName, ce.IsPlayingAudio, _editorSessionStartUtc, immediate: immediate);
            return;
        }

        if (_inEditorPicker)
        {
            DiscordRpc.SetEditorHub("Choosing an editor");
            return;
        }

        if (_inReplays)
        {
            DiscordRpc.SetMenu();
            return;
        }

        if (_inLevelSelector)
        {
            DiscordRpc.SetEditorHub("Selecting a chart");
            return;
        }

        if (_inHudEditor)
        {
            DiscordRpc.SetEditorHub("HUD Editor");
            return;
        }

        if (_inMainSettings)
        {
            DiscordRpc.SetEditorHub(_hudEditorReturnTarget == HudEditorReturnTarget.EditorPicker ? "HUD Editor" : "Settings");
            return;
        }

        if (_level != null || _loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
        {
            string levelName = GetCurrentSongDisplayName();
            if (_loadingScreen != null || _loadState == LoadState.Loading || _loadState == LoadState.Finalizing)
            {
                DiscordRpc.SetLoading(levelName);
            }
            else
            {
                _gameplayPresenceStartUtc ??= DateTimeOffset.UtcNow;
                DiscordRpc.SetPlaying(levelName, isPlaytest: _playtestingFromEditor, startTime: _gameplayPresenceStartUtc);
            }
            return;
        }

        if (_inFreeplay && _freeplay != null)
        {
            if (_freeplay.IsPanelOpen)
                DiscordRpc.SetBrowsingMusic(_freeplay.SelectedLevelName);
            else
                DiscordRpc.SetMenu();
            return;
        }

        DiscordRpc.SetMenu();
    }
}
