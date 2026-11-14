using LLMBench.Core;
using LLMBench.Core.Runs;

namespace LLMBench.Gui;

internal sealed class MainForm : Form
{
    private readonly Workspace _ws;
    private readonly RunsPage _runs;
    private readonly CatalogPage _catalogs;
    private readonly PromptsPage _prompts;
    private readonly LeaderboardPage _leaderboard;
    private readonly ComparePage _compare;
    private readonly TabControl _tabs;
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm(Workspace ws)
    {
        _ws = ws;
        Text = $"LLMBench {BenchRunner.Version} — {ws.Root}";
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Bounds = new Rectangle(area.Left + area.Width * 2 / 100, area.Top + area.Height * 2 / 100, area.Width * 96 / 100, area.Height * 96 / 100);
        MinimumSize = new Size(1200, 750);
        KeyPreview = true;

        var tabs = _tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(16, 6), Font = new Font("Segoe UI", 10.5f) };
        _runs = new RunsPage(ws, SetStatus) { Dock = DockStyle.Fill };
        _catalogs = new CatalogPage(ws) { Dock = DockStyle.Fill };
        _prompts = new PromptsPage(ws) { Dock = DockStyle.Fill };
        _compare = new ComparePage(ws) { Dock = DockStyle.Fill };
        _leaderboard = new LeaderboardPage(ws, ShowRun, ShowCompare) { Dock = DockStyle.Fill };

        tabs.TabPages.Add(Page("Runs", _runs));
        tabs.TabPages.Add(Page("Leaderboard", _leaderboard));
        tabs.TabPages.Add(Page("Compare", _compare));
        tabs.TabPages.Add(Page("Catalogs", _catalogs));
        tabs.TabPages.Add(Page("System prompts", _prompts));
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab?.Text == "Runs") _runs.RefreshFileLists();
            if (tabs.SelectedTab?.Text == "Leaderboard" && !_suppressReload) _leaderboard.Reload();
            if (tabs.SelectedTab?.Text == "Compare") _compare.RefreshRuns();
        };

        var strip = new StatusStrip();
        strip.Items.Add(_status);
        strip.Items.Add(new ToolStripStatusLabel($"Workspace: {ws.Root}"));

        Controls.Add(tabs);
        Controls.Add(strip);
        SetStatus("Ready.");

        FormClosing += (_, e) =>
        {
            if (!_runs.IsRunning) return;
            var r = MessageBox.Show(this, "A run is in progress. Stop it and exit?", "LLMBench", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            _runs.StopAndWait();
        };
    }

    private static TabPage Page(string title, Control content)
    {
        var p = new TabPage(title) { Padding = new Padding(4), Font = new Font("Segoe UI", 9.75f) };
        p.Controls.Add(content);
        return p;
    }

    private void SetStatus(string text) => _status.Text = text;

    private bool _suppressReload;

    private void ShowRun(string name)
    {
        _tabs.SelectedIndex = 0;
        _runs.SelectRun(name);
    }

    private void ShowCompare(string a, string b)
    {
        _suppressReload = true;
        _tabs.SelectedTab = _tabs.TabPages.Cast<TabPage>().First(t => t.Text == "Compare");
        _suppressReload = false;
        _compare.CompareRuns(a, b);
    }
}
