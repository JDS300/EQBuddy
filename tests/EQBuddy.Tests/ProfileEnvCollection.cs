namespace EQBuddy.Tests;

/// <summary>
/// Serialises every test class that swaps EQBUDDY_APPDATA.
///
/// That variable is process-global and xUnit runs test classes in parallel, so two classes
/// pointing it at their own temp profile will read each other's settings.json. It surfaced as
/// a once-in-eight-runs failure in ChipScaleMigrationTests.LoadDoesNotRunTheMigration - a
/// migration appearing to have run when a neighbouring class had simply moved the profile out
/// from under it. Flaky tests train you to rerun rather than read, so it is worth the lost
/// parallelism to make this impossible.
/// </summary>
[CollectionDefinition("profile-env", DisableParallelization = true)]
public sealed class ProfileEnvCollection;
