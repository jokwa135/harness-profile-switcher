using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace HarnessProfileSwitcher.Core
{
    public sealed class OperationLease : IDisposable
    {
        private readonly Mutex mutex;
        private readonly FileStream lockStream;
        private bool ownsMutex;

        private OperationLease(Mutex mutex, FileStream lockStream, bool ownsMutex)
        {
            this.mutex = mutex;
            this.lockStream = lockStream;
            this.ownsMutex = ownsMutex;
        }

        public static OperationLease Acquire(EnginePaths paths)
        {
            Directory.CreateDirectory(paths.StateDirectory);
            string mutexName = @"Local\HarnessProfileSwitcher-" + HashName(paths.RuntimeRoot);
            Mutex mutex = new Mutex(false, mutexName);
            bool acquired = false;
            try
            {
                try
                {
                    acquired = mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    throw new InvalidOperationException("Another Harness Profile Switcher operation is already running.");
                }

                FileStream stream = new FileStream(
                    paths.OperationLockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
                return new OperationLease(mutex, stream, true);
            }
            catch
            {
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
                mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            lockStream.Dispose();
            if (ownsMutex)
            {
                mutex.ReleaseMutex();
                ownsMutex = false;
            }
            mutex.Dispose();
        }

        private static string HashName(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
                return BitConverter.ToString(hash, 0, 12).Replace("-", string.Empty);
            }
        }
    }
}
