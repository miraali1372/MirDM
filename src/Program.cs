using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MirDM.Tests;

namespace MirDM;

public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args.Contains("--test", StringComparer.OrdinalIgnoreCase) || args.Contains("-t", StringComparer.OrdinalIgnoreCase) || args.Contains("--test-hetzner", StringComparer.OrdinalIgnoreCase)))
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole())
            {
                var stdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (stdOutHandle != IntPtr.Zero && stdOutHandle != new IntPtr(-1))
                {
                    var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdOutHandle, ownsHandle: false);
                    var fs = new System.IO.FileStream(safeHandle, System.IO.FileAccess.Write);
                    var writer = new System.IO.StreamWriter(fs) { AutoFlush = true };
                    Console.SetOut(writer);
                    Console.SetError(writer);
                }
            }

            bool passed;
            if (args.Contains("--test-hetzner", StringComparer.OrdinalIgnoreCase))
            {
                passed = EngineIntegrationTest.RunHetznerTestAsync().GetAwaiter().GetResult();
            }
            else
            {
                passed = EngineIntegrationTest.RunAsync().GetAwaiter().GetResult();
            }

            return passed ? 0 : 1;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
