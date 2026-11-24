using System;
using System.Linq;
using System.Windows.Forms;

namespace Pdb.Install
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Contains("--silent")) return Setup.RunSilent(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Wizard());
            return 0;
        }
    }
}
