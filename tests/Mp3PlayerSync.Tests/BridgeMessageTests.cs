using Mp3PlayerSync.Core;

namespace Mp3PlayerSync.Tests;

// The JSON shapes here must match what ExportifyBridge.js posts and expects.
public class BridgeMessageTests
{
    [Test]
    public void Parses_playlists_message()
    {
        var message = BridgeMessage.Parse("""
            {"type":"playlists","playlists":[
              {"key":"https://open.spotify.com/collection/tracks","name":"Liked Songs","owner":"me","trackCount":12,"url":"https://open.spotify.com/collection/tracks","imageUrl":"https://exportify.net/liked_songs.jpeg"},
              {"key":"row:1","name":"Odd","owner":"","trackCount":null,"url":null}]}
            """);

        message.Type.ShouldBe(BridgeMessage.PlaylistsType);
        message.Playlists.ShouldBe([
            new PlaylistSummary("https://open.spotify.com/collection/tracks", "Liked Songs", "me", 12, "https://open.spotify.com/collection/tracks", "https://exportify.net/liked_songs.jpeg"),
            new PlaylistSummary("row:1", "Odd", "", null, null),
]);
    }

    [Test]
    public void Parses_state_message()
    {
        var message = BridgeMessage.Parse("""{"type":"state","signedIn":true,"tokenExpired":false,"signingIn":false,"playlistsReady":true,"url":"https://exportify.net/","error":""}""");

        message.Type.ShouldBe(BridgeMessage.StateType);
        message.SignedIn.ShouldBe(true);
        message.TokenExpired.ShouldBe(false);
        message.PlaylistsReady.ShouldBe(true);
    }

    [Test]
    public void Command_json_omits_unset_fields()
    {
        new BridgeCommand(BridgeCommand.Export, Key: "k").ToJson().ShouldBe("""{"cmd":"export","key":"k"}""");
    }
}
