using System;
using System.IO;
using HarnessProfileSwitcher.Application;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal static class ApplicationStateFixtureTests
    {
        private const string PackageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        private const string PackageVersion = "fixture-version";

        public static int Run()
        {
            // Fixture verification must never inspect the current user's live account state.

            string root = Path.Combine(FixtureWorkspace.PathFor("StateInspectorTests"), Guid.NewGuid().ToString("N"));
            string liveRoot = Path.Combine(root, "live");
            string webRoot = Path.Combine(liveRoot, @"AppData\Codex\web");
            EnginePaths paths = new EnginePaths(
                Path.Combine(root, "runtime"),
                Path.Combine(liveRoot, @".codex\auth.json"),
                Path.Combine(webRoot, "Codex"),
                Path.Combine(webRoot, ".hps-links"),
                Path.Combine(liveRoot, @".codex\.hps-auth"),
                Path.Combine(root, "fordeletion"));
            AtomicJsonStore store = new AtomicJsonStore();
            WindowsJunctionManager junctions = new WindowsJunctionManager();
            bool succeeded = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(paths.LiveAuthPath));
                Directory.CreateDirectory(paths.LiveBrowserLinkPath);
                File.WriteAllText(paths.LiveAuthPath, "auth-a");
                File.WriteAllText(Path.Combine(paths.LiveBrowserLinkPath, "browser.marker"), "A");
                ApplicationStateInspector inspector = new ApplicationStateInspector(paths, store, junctions);
                AssertMode(HarnessMode.Unmanaged, inspector.Inspect(), "Unmanaged state was not detected.");

                ProfileLifecycleEngine engine = new ProfileLifecycleEngine(
                    paths,
                    store,
                    new VerifiedFileMover(),
                    new VerifiedDirectoryMover(),
                    junctions,
                    new AlwaysStoppedGuard(),
                    new NoFaultInjector(),
                    PackageFamily,
                    PackageVersion);
                LifecycleResult registration = engine.RegisterFirstProfile("Primary");
                engine.MarkLaunchObserved(registration.TransactionId);
                ApplicationStateSnapshot active = inspector.Inspect();
                AssertMode(HarnessMode.Active, active, "Active state was not detected.");
                AssertTrue(active.Profiles.Count == 1 && active.Profiles[0].IsActive, "Active profile summary is incorrect.");

                LifecycleResult begin = engine.BeginOnboarding("Candidate");
                engine.MarkLaunchObserved(begin.TransactionId);
                ApplicationStateSnapshot pending = inspector.Inspect();
                AssertMode(HarnessMode.PendingOnboarding, pending, "Pending onboarding was not detected.");
                AssertTrue(!pending.PendingAuthenticationPresent, "Pending authentication was reported before OAuth.");
                File.WriteAllText(paths.LiveAuthPath, "auth-b");
                File.WriteAllText(Path.Combine(paths.ProfileBrowserPath(begin.CandidateProfileId), "oauth.marker"), "signed-in");
                AssertTrue(inspector.Inspect().PendingAuthenticationPresent, "OAuth authentication was not detected without parsing it.");

                string interruptedId = Guid.NewGuid().ToString("D");
                store.Write(paths.JournalPath(interruptedId), new TransactionJournal
                {
                    SchemaVersion = 1,
                    TransactionId = interruptedId,
                    Operation = "switch",
                    Phase = TransactionPhases.Prepared,
                    SourceProfileId = registration.ActiveProfileId,
                    TargetProfileId = begin.CandidateProfileId,
                    PackageFamily = PackageFamily,
                    PackageVersion = PackageVersion,
                    LiveAuthPath = paths.LiveAuthPath,
                    SourceAuthPath = paths.ProfileAuthPath(registration.ActiveProfileId),
                    TargetAuthPath = paths.ProfileAuthPath(begin.CandidateProfileId),
                    StagingAuthPath = paths.StagingAuthPath(interruptedId),
                    LiveBrowserLinkPath = paths.LiveBrowserLinkPath,
                    SourceInactiveLinkPath = paths.InactiveLinkPath(registration.ActiveProfileId),
                    TargetInactiveLinkPath = paths.InactiveLinkPath(begin.CandidateProfileId),
                    SourceBrowserPath = paths.ProfileBrowserPath(registration.ActiveProfileId),
                    TargetBrowserPath = paths.ProfileBrowserPath(begin.CandidateProfileId),
                    SourceAuthSha256 = new string('A', 64),
                    TargetAuthSha256 = new string('B', 64),
                    SourceAuthLength = 1,
                    TargetAuthLength = 1,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o")
                });
                ApplicationStateSnapshot recovery = inspector.Inspect();
                AssertMode(HarnessMode.RecoveryRequired, recovery, "Interrupted journal did not override normal actions.");
                AssertTrue(recovery.PendingTransaction != null && recovery.PendingTransaction.TransactionId == interruptedId, "Recovery journal summary is incorrect.");
                succeeded = true;
                return 2;
            }
            finally
            {
                if (succeeded)
                {
                    string archiveRoot = FixtureWorkspace.PathFor("Archive/state");
                    Directory.CreateDirectory(archiveRoot);
                    Directory.Move(root, Path.Combine(archiveRoot, Path.GetFileName(root)));
                }
                else
                {
                    Console.Error.WriteLine("State inspector fixture retained for diagnosis: " + root);
                }
            }
        }

        private static void AssertMode(HarnessMode expected, ApplicationStateSnapshot snapshot, string message)
        {
            if (snapshot.Mode != expected) throw new Exception(message + " Found " + snapshot.Mode + ".");
        }

        private static void AssertTrue(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private sealed class AlwaysStoppedGuard : IProcessGuard
        {
            public void EnsureStopped()
            {
            }
        }
    }
}
