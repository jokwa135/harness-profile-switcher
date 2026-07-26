using System;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Application
{
    public sealed class DesktopInspection
    {
        public ApplicationStateSnapshot State { get; set; }
        public string PackageVersion { get; set; }
        public int PackageProcessCount { get; set; }
        public int MainProcessCount { get; set; }
    }

    public sealed class OperationContext
    {
        public ChatGptInstallation Installation { get; set; }
        public IPackageProcessInventory Inventory { get; set; }
        public ChatGptProcessController ProcessController { get; set; }
        public ChatGptLauncher Launcher { get; set; }
    }

    public sealed class ApplicationServices
    {
        private readonly EnginePaths paths;
        private readonly AtomicJsonStore store;
        private readonly WindowsJunctionManager junctions;
        private readonly IChatGptInstallationDiscovery discovery;
        private readonly IRuntimeSecurity runtimeSecurity;
        private readonly ISecureDiagnosticLogger diagnostics;

        public ApplicationServices()
            : this(ProductionEnvironment.CreatePaths(), new PowerShellPackageDiscovery())
        {
        }

        public ApplicationServices(EnginePaths paths, IChatGptInstallationDiscovery discovery)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (discovery == null) throw new ArgumentNullException("discovery");
            this.paths = paths;
            this.discovery = discovery;
            store = new AtomicJsonStore();
            junctions = new WindowsJunctionManager();
            runtimeSecurity = new WindowsRuntimeSecurity(paths.RuntimeRoot, paths.DeletionRoot);
            diagnostics = new SecureDiagnosticLogger(paths, runtimeSecurity);
        }

        public ApplicationServices(
            EnginePaths paths,
            IChatGptInstallationDiscovery discovery,
            IRuntimeSecurity runtimeSecurity,
            ISecureDiagnosticLogger diagnostics)
        {
            if (paths == null) throw new ArgumentNullException("paths");
            if (discovery == null) throw new ArgumentNullException("discovery");
            if (runtimeSecurity == null) throw new ArgumentNullException("runtimeSecurity");
            if (diagnostics == null) throw new ArgumentNullException("diagnostics");
            this.paths = paths;
            this.discovery = discovery;
            this.runtimeSecurity = runtimeSecurity;
            this.diagnostics = diagnostics;
            store = new AtomicJsonStore();
            junctions = new WindowsJunctionManager();
        }

        public DesktopInspection Inspect()
        {
            ChatGptInstallation installation = discovery.Discover();
            IPackageProcessInventory inventory = new WmiPackageProcessInventory(installation);
            PackageProcessSnapshot processes = inventory.Capture();
            ChatGptRuntimeCompatibility.Validate(processes, paths.LiveBrowserLinkPath);
            return new DesktopInspection
            {
                State = new ApplicationStateInspector(paths, store, junctions).Inspect(),
                PackageVersion = installation.PackageVersion,
                PackageProcessCount = processes.Processes.Count,
                MainProcessCount = processes.MainProcesses.Count
            };
        }

        public OperationContext CreateOperationContext()
        {
            ChatGptInstallation installation = discovery.Discover();
            IPackageProcessInventory inventory = new WmiPackageProcessInventory(installation);
            ChatGptRuntimeCompatibility.Validate(inventory.Capture(), paths.LiveBrowserLinkPath);
            ChatGptProcessController controller = new ChatGptProcessController(installation, inventory);
            return new OperationContext
            {
                Installation = installation,
                Inventory = inventory,
                ProcessController = controller,
                Launcher = new ChatGptLauncher(installation, inventory, new ExplorerAppActivator())
            };
        }

        public void RegisterFirstAndRelaunch(OperationContext context, string displayName)
        {
            ProfileLifecycleEngine engine = CreateLifecycleEngine(context);
            ExecuteAndRelaunch(
                context,
                DiagnosticOperations.RegisterFirst,
                delegate { return engine.RegisterFirstProfile(displayName).TransactionId; },
                engine.MarkLaunchObserved);
        }

        public void BeginOnboardingAndRelaunch(OperationContext context, string displayName)
        {
            ProfileLifecycleEngine engine = CreateLifecycleEngine(context);
            ExecuteAndRelaunch(
                context,
                DiagnosticOperations.BeginOnboarding,
                delegate { return engine.BeginOnboarding(displayName).TransactionId; },
                engine.MarkLaunchObserved);
        }

        public void FinalizeOnboardingAndRelaunch(OperationContext context)
        {
            ProfileLifecycleEngine engine = CreateLifecycleEngine(context);
            ExecuteAndRelaunch(
                context,
                DiagnosticOperations.FinalizeOnboarding,
                delegate { return engine.FinalizeOnboarding().TransactionId; },
                engine.MarkLaunchObserved);
        }

        public void CancelOnboardingAndRelaunch(OperationContext context)
        {
            ProfileLifecycleEngine engine = CreateLifecycleEngine(context);
            ExecuteAndRelaunch(
                context,
                DiagnosticOperations.CancelOnboarding,
                delegate { return engine.CancelOnboarding().TransactionId; },
                engine.MarkLaunchObserved);
        }

        public void SwitchAndRelaunch(OperationContext context, string targetProfileId)
        {
            SwitchEngine engine = CreateSwitchEngine(context);
            ExecuteAndRelaunch(
                context,
                DiagnosticOperations.Switch,
                delegate { return engine.SwitchTo(targetProfileId).TransactionId; },
                engine.MarkLaunchObserved);
        }

        public void RecoverAndRelaunch(OperationContext context, PendingTransactionSummary pending)
        {
            if (pending == null) throw new ArgumentNullException("pending");
            RequireContext(context);
            string packageVersion = context.Installation.PackageVersion;
            string transactionId = null;
            diagnostics.OperationStarted(DiagnosticOperations.Recover, packageVersion);
            try
            {
                if (string.Equals(pending.Operation, "switch", StringComparison.Ordinal))
                {
                    SwitchEngine switchEngine = CreateSwitchEngine(context);
                    RecoveryResult recovery = switchEngine.RecoverPending();
                    transactionId = recovery.TransactionId;
                    if (string.Equals(recovery.Result, "NoPendingTransaction", StringComparison.Ordinal))
                    {
                        diagnostics.OperationNoop(DiagnosticOperations.Recover, packageVersion);
                        return;
                    }
                    context.Launcher.LaunchAndWait(TimeSpan.FromSeconds(30));
                    if (string.Equals(recovery.Result, "RolledForward", StringComparison.Ordinal))
                    {
                        switchEngine.MarkLaunchObserved(recovery.TransactionId);
                    }
                }
                else
                {
                    ProfileLifecycleEngine lifecycleEngine = CreateLifecycleEngine(context);
                    LifecycleRecoveryResult lifecycleRecovery = lifecycleEngine.RecoverPending();
                    transactionId = lifecycleRecovery.TransactionId;
                    if (string.Equals(lifecycleRecovery.Result, "NoPendingTransaction", StringComparison.Ordinal))
                    {
                        diagnostics.OperationNoop(DiagnosticOperations.Recover, packageVersion);
                        return;
                    }
                    context.Launcher.LaunchAndWait(TimeSpan.FromSeconds(30));
                    if (string.Equals(lifecycleRecovery.Result, "RolledForward", StringComparison.Ordinal) ||
                        string.Equals(lifecycleRecovery.Result, "RolledForwardPendingOnboarding", StringComparison.Ordinal))
                    {
                        lifecycleEngine.MarkLaunchObserved(lifecycleRecovery.TransactionId);
                    }
                }
            }
            catch (Exception exception)
            {
                TryLogFailure(DiagnosticOperations.Recover, packageVersion, transactionId, exception);
                throw;
            }
            WriteCompletionOrThrow(DiagnosticOperations.Recover, packageVersion, transactionId);
        }

        private SwitchEngine CreateSwitchEngine(OperationContext context)
        {
            RequireContext(context);
            return new SwitchEngine(
                paths,
                store,
                new VerifiedFileMover(),
                junctions,
                context.ProcessController,
                new NoFaultInjector(),
                context.Installation.PackageFamily,
                context.Installation.PackageVersion,
                runtimeSecurity);
        }

        private ProfileLifecycleEngine CreateLifecycleEngine(OperationContext context)
        {
            RequireContext(context);
            return new ProfileLifecycleEngine(
                paths,
                store,
                new VerifiedFileMover(),
                new VerifiedDirectoryMover(),
                junctions,
                context.ProcessController,
                new NoFaultInjector(),
                context.Installation.PackageFamily,
                context.Installation.PackageVersion,
                runtimeSecurity);
        }

        private void ExecuteAndRelaunch(
            OperationContext context,
            string operation,
            Func<string> mutate,
            Action<string> markLaunchObserved)
        {
            RequireContext(context);
            if (mutate == null) throw new ArgumentNullException("mutate");
            if (markLaunchObserved == null) throw new ArgumentNullException("markLaunchObserved");
            string packageVersion = context.Installation.PackageVersion;
            string transactionId = null;
            diagnostics.OperationStarted(operation, packageVersion);
            try
            {
                transactionId = mutate();
                context.Launcher.LaunchAndWait(TimeSpan.FromSeconds(30));
                markLaunchObserved(transactionId);
            }
            catch (Exception exception)
            {
                TryLogFailure(operation, packageVersion, transactionId, exception);
                throw;
            }
            WriteCompletionOrThrow(operation, packageVersion, transactionId);
        }

        private void WriteCompletionOrThrow(string operation, string packageVersion, string transactionId)
        {
            try
            {
                diagnostics.OperationSucceeded(operation, packageVersion, transactionId);
            }
            catch (Exception exception)
            {
                throw new PostOperationDiagnosticException(exception);
            }
        }

        private void TryLogFailure(string operation, string packageVersion, string transactionId, Exception operationFailure)
        {
            try
            {
                diagnostics.OperationFailed(operation, packageVersion, transactionId, operationFailure);
            }
            catch (Exception diagnosticFailure)
            {
                operationFailure.Data["HarnessProfileSwitcherDiagnosticFailure"] = DiagnosticErrorClassifier.Classify(diagnosticFailure);
            }
        }

        private static void RequireContext(OperationContext context)
        {
            if (context == null || context.Installation == null ||
                context.ProcessController == null || context.Launcher == null)
            {
                throw new ArgumentException("A complete operation context is required.", "context");
            }
        }
    }
}
