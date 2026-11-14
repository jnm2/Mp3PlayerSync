# Working on MP3 Player Sync

## Settled design decisions

Before any UI or behavior change, read [docs/design-decisions.md](docs/design-decisions.md). It lists every decision
the user has settled, each with the test that checks it.

- Don't contradict an entry without the user's explicit OK.
- When the user settles something new, or changes an entry, update the list in the same change, with its check.
- If a check of a decision fails, fix the code. Don't weaken or remove the check.

## Branches and pull requests

Only the user merges to `main` or creates tags.

- Work in a worktree of your own, on a branch from `origin/main` named with underscores (e.g. `release_gate`).
- Commit in small steps with self-explanatory messages. Refactorings go in commits of their own, without changes to
  behavior.
- Push your branch and open a pull request to `main` (`gh pr create`). CI builds and tests every pull request; the user
  reviews and merges.

## Running the app and tests

The UI must never take focus from the user's desktop (TEST-1). Run it only through the end-to-end test on a hidden
desktop, never with `dotnet run` or `winapp run` on the user's desktop.

```powershell
dotnet build Mp3PlayerSync.slnx
dotnet test tests\Mp3PlayerSync.Tests
pwsh tests\Invoke-EndToEnd.ps1
dotnet run --project tests\Mp3PlayerSync.EndToEnd
```

The end-to-end tests are moving from PowerShell (`Invoke-EndToEnd.ps1`) to C# (`tests\Mp3PlayerSync.EndToEnd`, TUnit
with Shouldly, driven through FlaUI). The C# project is its own launcher: run as above, it runs its tests on a hidden
desktop, with the same exit codes and pass text as `Invoke-EndToEnd.ps1`. Its `--only` takes run names, and `--help`
lists its options. The user can also run and debug its tests from Visual Studio's Test Explorer, which runs them on
their own desktop (TEST-3). Agents never do that, and never run them through `dotnet test`, which skips them: agents
use only the launcher.

The `Live` run (`--only Live`, TEST-4) is the weekly live check on the real exportify.net and Spotify, which
`.github\workflows\live-check.yml` runs. It's explicit, so it never runs as part of the other runs. Without
`SPOTIFY_TEST_USERNAME` and `SPOTIFY_TEST_PASSWORD`, it's a dry run that stops at Spotify's sign-in page and reports
itself as inconclusive.

Agents never pass `-Desktop Current` to `Invoke-EndToEnd.ps1` (or `Test-PublishedExe.ps1`), or `--desktop Current` to
the C# launcher, on the user's PC: it runs the app on their own desktop, covering and taking focus from their windows.
It's for CI, or for a person who runs it on purpose.

The end-to-end run passes when it prints `All end-to-end checks passed`. It exits with 1 when a check failed, and with 2
when nothing failed but some checks were INCONCLUSIVE (they couldn't see what they check), which is not a pass.

The unit tests use [TUnit](https://tunit.dev) on Microsoft.Testing.Platform, which runs them in parallel, each in a new
instance of its class. Give each test its own temporary folder, and mark one that can't share a resource with
`[NotInParallel]`, saying why. Assert with [Shouldly](https://docs.shouldly.org), not TUnit's awaited `Assert.That`, so
a test is only `async` when it awaits the code it tests. Use the most specific assertion, so a failure says what went
wrong: `ShouldBeEmpty`, `ShouldHaveSingleItem()` and `ShouldNotBeNull()` (whose results the next checks use, rather than
`Single()`, `!` or `?.`), and `ShouldAllBe` rather than a loop. Check one thing per statement; use
`ShouldSatisfyAllConditions` only where seeing every failure at once helps, such as many fields of one parsed record.
`ShouldBe` compares collections in order (`ignoreOrder: true` for any order); `ShouldContain`, `ShouldStartWith` and
`ShouldEndWith` on strings ignore case unless given `Case.Sensitive`. To run some of the tests, filter by class:

```powershell
dotnet test --project tests\Mp3PlayerSync.Tests --treenode-filter "/*/*/PlayersTests/*"
```

See [src/Mp3PlayerSync/AGENTS.md](src/Mp3PlayerSync/AGENTS.md) for WinUI tooling.
