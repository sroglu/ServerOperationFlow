using System;

namespace PFound.ServerOperationFlow.Core.Tests
{
    /// <summary>
    /// Tiny standalone assertion kit for the engine-free ServerOperation suite — no NUnit, so the pure Core
    /// compiles and runs under mono/csc without Unity, mirroring the Commerce / RemoteGameConfig runners.
    /// </summary>
    internal static class TestKit
    {
        public static int Passed;
        public static int Failed;

        public static void Check(bool condition, string name)
        {
            if (condition) { Passed++; }
            else { Failed++; Console.WriteLine("  FAIL: " + name); }
        }

        public static int Summary(string label)
        {
            Console.WriteLine("--------------------------------------------------");
            Console.WriteLine(label + ": passed=" + Passed + " failed=" + Failed);
            return Failed == 0 ? 0 : 1;
        }
    }
}
