namespace Mp3PlayerSync.Tests;

public class HostNavigationsTests
{
    private const string Home = "https://exportify.net/";
    private const string SignIn = "https://accounts.spotify.com/authorize?client_id=x";

    [Test]
    public void The_apps_navigation_is_its_own()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);

        Assert.That(navigations.IsOwn(Home), Is.True);
    }

    [Test]
    public void A_page_navigation_that_starts_first_doesnt_take_the_apps_place()
    {
        var navigations = new HostNavigations();

        // The page was already going to Spotify's sign-in when the app sent it home.
        navigations.Starting(Home);
        Assert.That(navigations.IsOwn(SignIn), Is.False);
        Assert.That(navigations.IsOwn(Home), Is.True);
    }

    [Test]
    public void Only_one_navigation_counts_as_the_apps_for_each_it_starts()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);
        navigations.IsOwn(Home);

        // Such as Exportify going home by itself after Spotify stopped accepting its token
        Assert.That(navigations.IsOwn(Home), Is.False);
    }

    [Test]
    public void A_redirect_of_the_apps_navigation_is_the_pages()
    {
        var navigations = new HostNavigations();
        navigations.Starting(Home);
        navigations.IsOwn(Home);

        Assert.That(navigations.IsOwn(SignIn), Is.False);
    }
}
