#if !ANDROID
using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer.Multiplayer.Rooms
{
    public sealed partial class RoomClient
    {
        /// <summary>
        /// Developer harness only (Game1.DevShot.cs, <c>FMP_OPEN=online:room / online:rooms</c>): a made-up
        /// room list and room, to check the online screens' layout without the server.
        /// </summary>
        internal void DevFill(bool songSelect = false)
        {
            var names = new[] { ("AnnaHolOl", 300), ("AbelitoGamer", 100), ("AntonioMA", 220), ("Just Fenix", 60) };
            // Discord's default avatars (protocol 5 pictures); the local player keeps the fallback disc.
            static string Av(int k) => $"https://cdn.discordapp.com/embed/avatars/{k % 6}.png";
            Rooms = new List<RoomSummary>();
            RoomsLoaded = true;
            for (int i = 0; i < 6; i++)
            {
                var r = new RoomSummary
                {
                    Id = "r" + i, Name = i == 0 ? "AnnaHolOl's room" : $"Test room number {i + 1} with a long name",
                    HasPassword = i % 3 == 1, Mode = i % 2 == 0 ? Protocol.ModeMirror : Protocol.ModeTeams,
                    Phase = i == 2 ? Protocol.PhasePlaying : Protocol.PhaseLobby, SongName = i == 2 ? "Under Construction [Player]" : "",
                    SongStars = 4.2f, HostName = names[i % names.Length].Item1, PlayerCount = 2 + i % 3, MaxPlayers = 8,
                };
                for (int k = 0; k < r.PlayerCount; k++)
                    r.Players.Add(new RoomPlayerSummary { Id = "p" + k, Username = names[k % names.Length].Item1, ColorHue = names[k % names.Length].Item2, Avatar = k == 1 ? "" : Av(k) });
                Rooms.Add(r);
            }

            var me = MultiplayerSettings.Instance.PlayerId;
            var st = new RoomStatePacket
            {
                RoomId = "r0", Name = "AnnaHolOl's room", Phase = Protocol.PhaseSongSelected, HostId = "p0", YouId = me,
                TurnPlayerId = "p0",
                Settings = new RoomSettings { Mode = Protocol.ModeMirror, SongSelection = Protocol.SelectTurns, MaxPlayers = 8, AllowGuestLocalLevels = true, SongTypes = Protocol.SongTypesBoth },
                Song = new SongInfo { Name = "RofosHristo, Leebert - Under Construction [Player]", Stars = 4.6f, PickedBy = "p0", Size = 12_400_000, Duration = 151 },
                History = new List<RoundResult>(),
            };
            st.Members.Add(new RoomMember { Id = "p0", Username = "AnnaHolOl", ColorHue = 300, Avatar = Av(0), IsHost = true, Status = Protocol.StatusReady, LevelStatus = Protocol.LevelReady, Vote = 0 });
            st.Members.Add(new RoomMember { Id = me, Username = MultiplayerSettings.Instance.Username, ColorHue = MultiplayerSettings.Instance.PlayerColorHue, Status = Protocol.StatusNotReady, LevelStatus = Protocol.LevelDownloading, LevelProgress = 0.4f });
            st.Members.Add(new RoomMember { Id = "p2", Username = "AntonioMA", ColorHue = 220, Avatar = Av(2), Status = Protocol.StatusReady, LevelStatus = Protocol.LevelReady, Vote = 1 });
            st.Members.Add(new RoomMember { Id = "p3", Username = "Just Fenix", ColorHue = 60, Avatar = Av(3), Spectate = true, Status = Protocol.StatusSpectating });
            for (int i = 0; i < 3; i++)
            {
                var round = new RoundResult { MatchId = "m" + i, Mode = Protocol.ModeMirror, Song = new SongInfo { Name = i == 0 ? "Purin" : "FARUCO - Oshikatsu Angel [Normal]" }, FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
                round.Players.Add(new PlayerResult { PlayerId = "p0", Username = "AnnaHolOl", ColorHue = 300, Avatar = Av(0), Score = 316242, Accuracy = 99.1f, MaxCombo = 700, Place = 1 });
                round.Players.Add(new PlayerResult { PlayerId = me, Username = MultiplayerSettings.Instance.Username, ColorHue = 100, Score = 231452, Accuracy = 97.4f, MaxCombo = 412, Misses = 4, Place = 2 });
                st.History.Add(round);
            }
            if (songSelect)
            {
                // Your turn, nothing picked yet, and other players' levels to pick from.
                st.Song = null;
                st.Phase = Protocol.PhaseLobby;
                st.TurnPlayerId = me;
                st.Settings.SongTypes = Protocol.SongTypesFnf;
                var rnd = new Random(7);
                string Hash() { var b = new byte[32]; rnd.NextBytes(b); return Convert.ToHexString(b).ToLowerInvariant(); }
                st.Levels = new List<RoomLevel>
                {
                    new() { Hash = Hash(), Name = "Shared Song", FileName = "Shared Song.fnmm", Size = 18_000_000, Stars = 3.1f, Holders = new() { "p0", "p2", "p3" }, Pickable = true, Duration = 140 },
                    new() { Hash = Hash(), Name = "Anna Only", FileName = "Anna Only.fnmm", Size = 9_500_000, Stars = 5.4f, Holders = new() { "p0" }, Pickable = true, Duration = 95 },
                    new() { Hash = Hash(), Name = "Antonio and Fenix", FileName = "Antonio and Fenix.fnmm", Size = 41_000_000, Stars = 2.2f, Holders = new() { "p2", "p3" }, Pickable = true, Duration = 200 },
                };
            }
            State = st;
            Chat.Clear();
            Chat.Add(new ChatMessage { System = true, Text = "AntonioMA joined the room." });
            Chat.Add(new ChatMessage { PlayerId = "p0", Username = "AnnaHolOl", ColorHue = 300, Avatar = Av(0), Text = "gg! one more?" });
            Chat.Add(new ChatMessage { PlayerId = me, Username = MultiplayerSettings.Instance.Username, ColorHue = 100, Text = "sure, pick something hard" });
        }
    }
}
#endif
