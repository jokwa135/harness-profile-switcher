using System.Threading;

namespace HarnessProfileSwitcher.Tests
{
    internal static class FakeChatGptProcess
    {
        private static int Main(string[] arguments)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }
    }
}
