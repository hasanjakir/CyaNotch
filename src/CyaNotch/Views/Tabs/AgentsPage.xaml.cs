using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CyaNotch.Services;

namespace CyaNotch.Views.Tabs;

/// <summary>Agents tab: which coding agent sessions need you, are working, or are done.</summary>
public partial class AgentsPage : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _selected;

    public AgentsPage()
    {
        InitializeComponent();
        CustomHint.Text = $"POST http://127.0.0.1:{AgentService.Port}/agent/MyAgent  {{\"state\":\"working\",\"message\":\"Building…\"}}";
        _tick.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Refresh(); _tick.Start(); } else _tick.Stop(); };
        App.Agents.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
    }

    private void Refresh()
    {
        var all = App.Agents.Sessions;
        List.ItemsSource = all;
        Empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var need = all.Count(s => s.State == AgentState.NeedsYou);
        var work = all.Count(s => s.State == AgentState.Working);
        Summary.Text = all.Count == 0 ? "" : $"{need} need you · {work} working · {all.Count - need - work} done";

        var sel = all.FirstOrDefault(s => s.Id == _selected);
        if (sel != null) List.SelectedItem = sel;
        Timeline.Visibility = sel != null ? Visibility.Visible : Visibility.Collapsed;
        Setup.Visibility = sel == null ? Visibility.Visible : Visibility.Collapsed;
        if (sel != null)
        {
            TimelineTitle.Text = $"{sel.Agent} · {sel.Folder}";
            Steps.ItemsSource = sel.Steps.AsEnumerable().Reverse().Select(x => new { Time = x.When.ToString("HH:mm"), x.What }).ToList();
        }

        var hooked = AgentService.ClaudeHooksInstalled();
        ConnectBtn.Visibility = hooked ? Visibility.Collapsed : Visibility.Visible;
        ConnectState.Text = hooked ? "Claude Code is connected. New sessions report each step." : "";
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var s = List.SelectedItem as AgentSession;
        if (s?.Id == _selected) return;
        _selected = s?.Id;
        Refresh();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AgentService.InstallClaudeHooks();
            ConnectState.Text = "Connected. Restart any open Claude Code sessions to pick it up.";
            ConnectBtn.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ConnectState.Text = "Couldn't update Claude Code's settings: " + ex.Message;
        }
    }
}
