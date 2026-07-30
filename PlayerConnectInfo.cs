using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace PlayerConnectInfo;

public class PlayerConnectInfo : BasePlugin
{
    public override string ModuleName => "PlayerConnectInfo";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "Nip0s";
    public override string ModuleDescription => "Показывает информацию при заходе/выходе игрока с anti-spam";

    private readonly Dictionary<ulong, DateTime> _lastConnectAnnounce = new();
    private readonly Dictionary<ulong, DateTime> _lastDisconnectAnnounce = new();
    private const double ANTI_SPAM_SECONDS = 5.0;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        Console.WriteLine("[PlayerConnectInfo] Plugin loaded!");
    }

    private void OnClientAuthorized(int playerSlot, SteamID sid)
    {
        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return;

        ulong steamId = player.SteamID;

        // Anti-spam
        if (_lastConnectAnnounce.TryGetValue(steamId, out var lastTime))
        {
            if ((DateTime.UtcNow - lastTime).TotalSeconds < ANTI_SPAM_SECONDS)
                return;
        }

        _lastConnectAnnounce[steamId] = DateTime.UtcNow;

        string name = player.PlayerName;
        Server.PrintToChatAll($" {ChatColors.Green}●{ChatColors.Default} {ChatColors.LightBlue}{name}{ChatColors.Default} подключился к серверу");
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        ulong steamId = player.SteamID;

        // Anti-spam
        if (_lastDisconnectAnnounce.TryGetValue(steamId, out var lastTime))
        {
            if ((DateTime.UtcNow - lastTime).TotalSeconds < ANTI_SPAM_SECONDS)
                return HookResult.Continue;
        }

        _lastDisconnectAnnounce[steamId] = DateTime.UtcNow;

        string name = player.PlayerName;
        Server.PrintToChatAll($" {ChatColors.Red}●{ChatColors.Default} {ChatColors.LightBlue}{name}{ChatColors.Default} отключился от сервера");

        return HookResult.Continue;
    }
}
