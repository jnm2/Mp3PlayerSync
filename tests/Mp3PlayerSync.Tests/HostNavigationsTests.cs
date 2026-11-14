namespace Mp3PlayerSync.Tests;

public class HostNavigationsTests
{
    private const string Home = "https://exportify.net/";
    private const string SignIn = "https://accounts.spotify.com/authorize?client_id=x";

    [Test]
    public async Task The_apps_navigation_is_its_own()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);

        await Assert.That(navigations.IsOwn(Home)).IsTrue();
    }

    [Test]
    public async Task A_page_navigation_that_starts_first_doesnt_take_the_apps_place()
    {
        var navigations = new HostNavigations();

        // The page was already going to Spotify's sign-in when the app sent it home.
        navigations.Starting(Home);
        await Assert.That(navigations.IsOwn(SignIn)).IsFalse();
        await Assert.That(navigations.IsOwn(Home)).IsTrue();
    }

    [Test]
    public async Task Only_one_navigation_counts_as_the_apps_for_each_it_starts()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);
        navigations.IsOwn(Home);

        // Such as Exportify going home by itself after Spotify stopped accepting its token
        await Assert.That(navigations.IsOwn(Home)).IsFalse();
    }

    [Test]
    public async Task A_redirect_of_the_apps_navigation_is_the_pages()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);
        navigations.IsOwn(Home);

        await Assert.That(navigations.IsOwn(SignIn)).IsFalse();
    }
}
