using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PlayerSyncModExporter.Interop;
using PlayerSyncModExporter.Services;
using PlayerSyncModExporter.UI;

namespace PlayerSyncModExporter;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/psexport";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _commands;
    private readonly WindowSystem _windows = new("PlayerSyncModExporter");
    private readonly PlayerSyncAdapter _playerSync;
    private readonly ExportSession _session;
    private readonly MainWindow _mainWindow;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IObjectTable objects, ITargetManager targets, IPluginLog log)
    {
        _pi = pi;
        _commands = commands;
        var config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        _playerSync = new PlayerSyncAdapter(pi, log);
        var penumbra = new PenumbraAdapter(pi, log);
        _session = new ExportSession(_playerSync, penumbra, objects, log, config);
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _mainWindow = new MainWindow(_session, _playerSync, penumbra, config, () => pi.SavePluginConfig(config), targets, version);
        _windows.AddWindow(_mainWindow);

        _commands.AddHandler(Command, new CommandInfo((_, _) => _mainWindow.Toggle())
        {
            HelpMessage = "Open PlayerSync Mod Exporter.",
        });
        pi.UiBuilder.Draw += _windows.Draw;
        pi.UiBuilder.OpenMainUi += _mainWindow.Toggle;
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= _windows.Draw;
        _pi.UiBuilder.OpenMainUi -= _mainWindow.Toggle;
        _commands.RemoveHandler(Command);
        _windows.RemoveAllWindows();
        _session.Dispose();
        _playerSync.Dispose();
    }
}
