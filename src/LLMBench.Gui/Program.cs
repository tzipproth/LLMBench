using System.Globalization;
using LLMBench.Core;

namespace LLMBench.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Numbers are always shown and parsed with '.' (concept 8.2: invariant culture everywhere).
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        ApplicationConfiguration.Initialize();

        Workspace? ws = null;
        try
        {
            ws = Workspace.Find(args.Length > 0 ? args[0] : null);
        }
        catch (BenchException)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select the LLMBench folder (contains 'catalogs' and 'prompts')" };
            while (ws == null && dlg.ShowDialog() == DialogResult.OK)
            {
                try { ws = new Workspace(dlg.SelectedPath); }
                catch (BenchException ex) { MessageBox.Show(ex.Message, "LLMBench"); }
            }
        }
        if (ws == null) return;
        Application.Run(new MainForm(ws));
    }
}
