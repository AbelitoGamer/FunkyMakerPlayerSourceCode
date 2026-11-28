#if !ANDROID
namespace FunkyMakerPlayer.Multiplayer
{
    public partial class MultiplayerMenu
    {
        /// <summary>
        /// Developer harness (Game1.DevShot.cs, <c>FMP_OPEN=online:&lt;screen&gt;</c>): opens an online
        /// screen that works without a server, to check its layout.
        /// </summary>
        internal void DevOpen(string what)
        {
            switch (what)
            {
                case "create": OpenRoomSettings(RoomSettingsMode.Create); break;
                case "browser": TransitionToState(MenuState.RoomBrowser); break;
                case "settings": TransitionToState(MenuState.Settings); break;
                case "top": _leaderboardTab = 0; TransitionToState(MenuState.Leaderboard); break;
                case "recent": _leaderboardTab = 1; TransitionToState(MenuState.Leaderboard); break;
                case "stats": _leaderboardTab = 2; TransitionToState(MenuState.Leaderboard); break;
                case "rooms": Room.DevFill(); TransitionToState(MenuState.RoomBrowser); break;
                case "room": Room.DevFill(); EnterWaitingRoom(); _awaitingRoomState = 0f; break;
                case "songs": Room.DevFill(songSelect: true); EnterWaitingRoom(); _awaitingRoomState = 0f; OpenSongSelect(); break;
                case "detail":
                    _leaderboardTab = 1;
                    TransitionToState(MenuState.Leaderboard);
                    DevOpenFirstMatchDetail();
                    break;
            }
        }

        private void DevOpenFirstMatchDetail()
        {
            var m = LeaderboardManager.Instance.GetRecentMatches(50);
            if (m.Count > 0) _matchDetail = m[0];
        }
    }
}
#endif
