using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

// The JSON shapes here must match what ExportifyBridge.js posts and expects.
public class BridgeMessageTests
{
    [Test]
    public async Task Parses_playlists_message()
    {
        var message = BridgeMessage.Parse("""
            {"type":"playlists","playlists":[
              {"key":"https://open.spotify.com/collection/tracks","name":"Liked Songs","owner":"me","trackCount":12,"url":"https://open.spotify.com/collection/tracks","imageUrl":"https://exportify.net/liked_songs.jpeg"},
              {"key":"row:1","name":"Odd","owner":"","trackCount":null,"url":null}]}
            """);

        await Assert.That(message.Type).IsEqualTo(BridgeMessage.PlaylistsType);
        await Assert.That(message.Playlists).IsEquivalentTo(new[]
        {
            new PlaylistSummary("https://open.spotify.com/collection/tracks", "Liked Songs", "me", 12, "https://open.spotify.com/collection/tracks", "https://exportify.net/liked_songs.jpeg"),
            new PlaylistSummary("row:1", "Odd", "", null, null),
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Parses_state_message()
    {
        var message = BridgeMessage.Parse("""{"type":"state","signedIn":true,"tokenExpired":false,"signingIn":false,"playlistsReady":true,"url":"https://exportify.net/","error":""}""");

        await Assert.That(message is { Type: BridgeMessage.StateType, SignedIn: true, TokenExpired: false, PlaylistsReady: true }).IsTrue();
    }

    [Test]
    public async Task Command_json_omits_unset_fields()
    {
        await Assert.That(new BridgeCommand(BridgeCommand.Export, Key: "k").ToJson()).IsEqualTo("""{"cmd":"export","key":"k"}""");
    }
}
