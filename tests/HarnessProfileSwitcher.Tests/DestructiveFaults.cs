using System;
using System.IO;
using HarnessProfileSwitcher.Core;

namespace HarnessProfileSwitcher.Tests
{
    internal sealed class PostEffectFaultException : IOException
    {
        public PostEffectFaultException(string message)
            : base(message)
        {
        }
    }

    internal sealed class ThrowAfterFileMove : IVerifiedFileMover
    {
        private readonly IVerifiedFileMover inner;
        private readonly int failureMove;
        private int moveCount;

        public ThrowAfterFileMove(int failureMove)
            : this(new VerifiedFileMover(), failureMove)
        {
        }

        public ThrowAfterFileMove(IVerifiedFileMover inner, int failureMove)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (failureMove < 1) throw new ArgumentOutOfRangeException("failureMove");
            this.inner = inner;
            this.failureMove = failureMove;
        }

        public FileFingerprint Fingerprint(string path)
        {
            return inner.Fingerprint(path);
        }

        public void MoveFile(string sourcePath, string destinationPath)
        {
            inner.MoveFile(sourcePath, destinationPath);
            moveCount++;
            if (moveCount == failureMove)
            {
                throw new PostEffectFaultException("Injected failure after verified file move " + moveCount + ".");
            }
        }
    }

    internal sealed class ThrowAfterDirectoryMove : IVerifiedDirectoryMover
    {
        private readonly IVerifiedDirectoryMover inner;
        private readonly int failureMove;
        private int moveCount;

        public ThrowAfterDirectoryMove(int failureMove)
            : this(new VerifiedDirectoryMover(), failureMove)
        {
        }

        public ThrowAfterDirectoryMove(IVerifiedDirectoryMover inner, int failureMove)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (failureMove < 1) throw new ArgumentOutOfRangeException("failureMove");
            this.inner = inner;
            this.failureMove = failureMove;
        }

        public DirectoryFingerprint Fingerprint(string path)
        {
            return inner.Fingerprint(path);
        }

        public void MoveDirectory(string sourcePath, string destinationPath)
        {
            inner.MoveDirectory(sourcePath, destinationPath);
            moveCount++;
            if (moveCount == failureMove)
            {
                throw new PostEffectFaultException("Injected failure after verified directory move " + moveCount + ".");
            }
        }
    }

    internal sealed class ThrowAfterJunctionMutation : IJunctionManager
    {
        private readonly IJunctionManager inner;
        private readonly int failureMutation;
        private int mutationCount;

        public ThrowAfterJunctionMutation(IJunctionManager inner, int failureMutation)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (failureMutation < 1) throw new ArgumentOutOfRangeException("failureMutation");
            this.inner = inner;
            this.failureMutation = failureMutation;
        }

        public bool Exists(string path)
        {
            return inner.Exists(path);
        }

        public string ResolveTarget(string path)
        {
            return inner.ResolveTarget(path);
        }

        public void Create(string junctionPath, string targetPath)
        {
            inner.Create(junctionPath, targetPath);
            AfterMutation();
        }

        public void Move(string sourcePath, string destinationPath)
        {
            inner.Move(sourcePath, destinationPath);
            AfterMutation();
        }

        private void AfterMutation()
        {
            mutationCount++;
            if (mutationCount == failureMutation)
            {
                throw new PostEffectFaultException("Injected failure after junction mutation " + mutationCount + ".");
            }
        }
    }

    internal sealed class ThrowAfterLedgerWriteSecurity : IRuntimeSecurity
    {
        private readonly IRuntimeSecurity inner;
        private readonly string ledgerPath;
        private bool thrown;

        public ThrowAfterLedgerWriteSecurity(string ledgerPath)
            : this(new NoOpRuntimeSecurity(), ledgerPath)
        {
        }

        public ThrowAfterLedgerWriteSecurity(IRuntimeSecurity inner, string ledgerPath)
        {
            if (inner == null) throw new ArgumentNullException("inner");
            if (string.IsNullOrWhiteSpace(ledgerPath)) throw new ArgumentException("Ledger path is required.", "ledgerPath");
            this.inner = inner;
            this.ledgerPath = Path.GetFullPath(ledgerPath);
        }

        public void EnsureProtectedDirectory(string path)
        {
            inner.EnsureProtectedDirectory(path);
        }

        public void ProtectFile(string path)
        {
            inner.ProtectFile(path);
            if (!thrown && string.Equals(Path.GetFullPath(path), ledgerPath, StringComparison.OrdinalIgnoreCase))
            {
                thrown = true;
                throw new PostEffectFaultException("Injected failure after the ledger reached disk.");
            }
        }

        public void ProtectTree(string path)
        {
            inner.ProtectTree(path);
        }

        public RuntimeAclAudit AuditDirectory(string path)
        {
            return inner.AuditDirectory(path);
        }

        public RuntimeAclAudit AuditFile(string path)
        {
            return inner.AuditFile(path);
        }
    }
}
