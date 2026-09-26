using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Quality;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Platform;

// The game's NetSingleplayerGameService, member for member, except that NetId is the recorded player's.
//
// A real singleplayer run has NetId 1. The .mcr writer anonymises every player id with a random value
// (IdAnonymizer, Rng.Chaotic), so the tape's player is e.g. 1480334801, and so is the playerId inside every
// recorded checkpoint. RunManager.Launch sets LocalContext.NetId from the net service, and the game's
// singleplayer service hard-codes 1: with it, LocalContext.GetMe finds nobody and the turn loop dies at the first
// end turn. Giving the service the recorded id keeps both the game's singleplayer branches (every one of them
// tests Type, not NetId) and the recorded checkpoint hashes.
sealed class SingleplayerNetService(ulong netId) : INetGameService
{
    bool _isLoading;

    public ulong NetId => netId;
    public NetGameType Type => NetGameType.Singleplayer;
    public PlatformType Platform => PlatformType.None;
    public bool IsConnected => true;
    public bool IsGameLoading => _isLoading;
    public PeerVersionInfo LocalVersion { get; } = PeerVersionInfo.LocalDefault();

#pragma warning disable CS0067 // never raised, as in the game's singleplayer service
    public event Action<NetErrorInfo>? Disconnected;
#pragma warning restore CS0067

    public void SendMessage<T>(T message, ulong playerId) where T : INetMessage { }
    public void SendMessage<T>(T message) where T : INetMessage { }
    public void RegisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage { }
    public void UnregisterMessageHandler<T>(MessageHandlerDelegate<T> handler) where T : INetMessage { }
    public void Update() { }
    public void Disconnect(NetError reason, bool now = false) { }
    public ConnectionStats GetStatsForPeer(ulong peerId) => throw new NotImplementedException();
    public void SetGameLoading(bool isLoading) => _isLoading = isLoading;
    public void SetBufferMessages(bool bufferMessages) { }
    public string? GetRawLobbyIdentifier() => null;
}
