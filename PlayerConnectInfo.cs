using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using IksAdminApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace PlayerConnectInfo;

/// <summary>
/// Сообщения о заходе и выходе игроков.
///
/// Главная сложность — MultiAddonManager. Чтобы отдать игроку воркшоп-аддоны,
/// он гоняет клиента по кругу: клиент подключается, получает предложение скачать
/// аддон, отваливается, качает, подключается снова — и так на каждый аддон.
/// Каждый круг порождает обычные player_disconnect и OnClientAuthorized, поэтому
/// наивный плагин пишет «подключился/отключился» по пять раз подряд.
///
/// Здесь это гасится двумя способами:
///   1. Отключения с причиной 54 (LOOPSHUTDOWN) и 53 (RECONNECTION) игнорируются —
///      именно их выдаёт цикл докачки.
///   2. Игрок объявляется один раз: пока он числится подключённым, повторные
///      OnClientAuthorized молчат. Уход объявляется с задержкой SettleSeconds и
///      отменяется, если игрок вернулся.
/// </summary>
public class PlayerConnectInfo : BasePlugin, IPluginConfig<PlayerConnectInfoConfig>
{
    public override string ModuleName => "PlayerConnectInfo";
    public override string ModuleVersion => "1.6.3";
    public override string ModuleAuthor => "Nip0s";
    public override string ModuleDescription => "Сообщения о заходе/выходе, устойчивые к переподключениям MultiAddonManager";

    public PlayerConnectInfoConfig Config { get; set; } = new();
    public void OnConfigParsed(PlayerConnectInfoConfig config) => Config = config;

    private sealed class Tracked
    {
        /// <summary>Написали «подключается» — клиент начал заход.</summary>
        public bool AnnouncedConnecting;

        /// <summary>Написали «подключился» — игрок доехал до сервера (ConnectFull).</summary>
        public bool AnnouncedJoined;

        /// <summary>Отложенное объявление об уходе. Отменяется, если игрок вернулся.</summary>
        public Timer? PendingLeave;

        public string Name = "";

        /// <summary>Админ сервера: о нём в чат не пишем.</summary>
        public bool Silent;
        public DateTime LastSeen = DateTime.UtcNow;
    }

    private readonly Dictionary<ulong, Tracked> _tracked = new();

    private static readonly PluginCapability<IIksAdminApi> IksCapability = new("iksadmin:core");
    private IIksAdminApi? _iks;

    /// <summary>Админы посреди автохайда: их смена команды не объявляется в чате.</summary>
    private readonly HashSet<ulong> _hiding = new();

    /// <summary>
    /// Админы, которые зашли и ещё не выбрали команду. Меню выбора команды
    /// открывается, когда клиент догрузился, и закрыть его без выбора нельзя.
    /// Поэтому прячем не сразу, а по первому выбору: переход блокируем и
    /// вместо него скрываем.
    /// </summary>
    private readonly HashSet<ulong> _pendingHide = new();
    private Timer? _cleanupTimer;

    /// <summary>
    /// Цветовые метки для файлов перевода — в JSON нельзя положить управляющий
    /// символ, поэтому в тексте пишем {Green}, а здесь подставляем настоящий код.
    /// </summary>
    private static readonly Dictionary<string, string> _colors = new()
    {
        ["{Default}"] = ChatColors.Default.ToString(),
        ["{Green}"] = ChatColors.Green.ToString(),
        ["{Red}"] = ChatColors.Red.ToString(),
        ["{LightBlue}"] = ChatColors.LightBlue.ToString(),
        ["{Orange}"] = ChatColors.Orange.ToString(),
        ["{Yellow}"] = ChatColors.Yellow.ToString(),
        ["{White}"] = ChatColors.White.ToString(),
        ["{Grey}"] = ChatColors.Grey.ToString(),
    };

    private string L(string key, params object[] args)
    {
        var sb = new StringBuilder(Localizer[key, args]);
        foreach (var (code, color) in _colors)
            sb.Replace(code, color);
        return sb.ToString();
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnClientAuthorized>(OnClientAuthorized);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam, HookMode.Pre);
        AddCommandListener("jointeam", OnJoinTeam, HookMode.Pre);

        StartCleanupTimer();
        // STOP_ON_MAPCHANGE гасит таймер при смене карты — поднимаем заново.
        RegisterListener<Listeners.OnMapStart>(_ => StartCleanupTimer());

        Console.WriteLine($"[{ModuleName}] Загружен v{ModuleVersion}");
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _iks = IksCapability.Get(); }
        catch { _iks = null; }
        if (_iks == null)
            Console.WriteLine($"[{ModuleName}] IksAdmin не найден — админы объявляются как все");
    }

    /// <summary>Админ этого сервера по IksAdmin (с учётом выключенных и удалённых).</summary>
    private Admin? ServerAdmin(ulong steamId)
    {
        if (_iks == null || steamId == 0) return null;
        try { return AdminUtils.ServerAdmin(steamId.ToString()); }
        catch { return null; }
    }

    private bool IsSilent(ulong steamId) => Config.SilentAdmins && ServerAdmin(steamId) != null;

    /// <summary>
    /// Скрыть админа из таба сразу после захода. Шаги те же, что у !hide в IksAdmin
    /// (наблюдатели → «не назначен», меню выбора команды на это время выключено),
    /// но в список скрытых он попадает ПОСЛЕ перехода в «не назначен»: иначе
    /// IksAdmin на этот переход пишет всем фейковое «Игрок отключился» — при
    /// заходе это выдало бы ник.
    /// </summary>
    private void AutoHide(CCSPlayerController player, ulong steamId)
    {
        var admin = ServerAdmin(steamId);
        if (_iks == null || admin == null) return;
        if (_iks.HidenAdmins.Contains(admin)) return;

        _hiding.Add(steamId);
        Server.ExecuteCommand("sv_disable_teamselect_menu 1");
        AddTimer(1.0f, () => Server.NextWorldUpdate(() =>
        {
            if (player.IsValid) player.ChangeTeam(CsTeam.Spectator);
        }), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(1.4f, () => Server.NextWorldUpdate(() =>
        {
            if (player.IsValid) player.ChangeTeam(CsTeam.None);
        }), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(1.6f, () =>
        {
            if (!player.IsValid || _iks == null) return;
            if (!_iks.HidenAdmins.Contains(admin))
                _iks.HidenAdmins.Add(admin);
            Debug($"{player.PlayerName}: админ скрыт из таба при заходе");
        }, TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(2.0f, () => Server.NextWorldUpdate(() =>
        {
            _hiding.Remove(steamId);
            Server.ExecuteCommand("sv_disable_teamselect_menu 0");
        }), TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Первый выбор в меню после захода. «Наблюдатели» — переход пропускаем (иначе
    /// меню не закроется и через секунду снова потребует команду), а следом молча
    /// прячем, как !hide из наблюдателей. T/CT — обычная игра, хайда нет.
    /// </summary>
    private HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo command)
    {
        if (player is not { IsValid: true } || !_pendingHide.Remove(player.SteamID))
            return HookResult.Continue;
        if (!int.TryParse(command.GetArg(1), out var team) || team != (int)CsTeam.Spectator)
            return HookResult.Continue;

        var steamId = player.SteamID;
        var admin = ServerAdmin(steamId);
        if (_iks == null || admin == null || _iks.HidenAdmins.Contains(admin))
            return HookResult.Continue;

        Debug($"{player.PlayerName}: зашёл в наблюдатели — прячем");
        _hiding.Add(steamId); // переход в наблюдатели тоже не объявляем
        Server.ExecuteCommand("sv_disable_teamselect_menu 1");
        // Сразу следующим кадром: за полсекунды в наблюдателях сервер успевал
        // добрать ботов, а сам админ видел себя в табе.
        Server.NextWorldUpdate(() =>
        {
            if (!player.IsValid) return;
            player.ChangeTeam(CsTeam.None);
            // В список скрытых — уже после перехода: иначе IksAdmin на него пишет
            // всем фейковое «Игрок отключился».
            Server.NextFrame(() =>
            {
                if (player.IsValid && _iks != null && !_iks.HidenAdmins.Contains(admin))
                    _iks.HidenAdmins.Add(admin);
            });
        });
        // Меню держим выключенным дольше, чем !hide: после перехода в «не назначен»
        // клиент норовит открыть его заново.
        AddTimer(4.0f, () => Server.NextWorldUpdate(() =>
        {
            _hiding.Remove(steamId);
            Server.ExecuteCommand("sv_disable_teamselect_menu 0");
        }), TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not { IsValid: true })
            return HookResult.Continue;

        // «X переходит в наблюдатели» во время автохайда выдало бы админа.
        if (_hiding.Contains(player.SteamID))
        {
            @event.Silent = true;
            info.DontBroadcast = true;
            return HookResult.Continue;
        }

        // Сервер сам поставил админа в T/CT (таймер выбора, автобаланс) — значит
        // он играет, хайд по первому выбору больше не ждём.
        if (@event.Team is (int)CsTeam.Terrorist or (int)CsTeam.CounterTerrorist)
            _pendingHide.Remove(player.SteamID);
        return HookResult.Continue;
    }

    public override void Unload(bool hotReload)
    {
        _cleanupTimer?.Kill();
        _cleanupTimer = null;

        foreach (var t in _tracked.Values)
            t.PendingLeave?.Kill();

        _tracked.Clear();
        base.Unload(hotReload);
    }

    // ══════════════════════════════════
    //  ЗАХОД
    // ══════════════════════════════════

    private void OnClientAuthorized(int playerSlot, SteamID sid)
    {
        // SteamID берём из параметра, а НЕ из контроллера: на первом круге захода
        // контроллер ещё не заполнен и player.SteamID возвращает ноль. Раньше
        // из-за этого первый круг молча пропускался, и «подключается» появлялось
        // только со второго — после докачки первого аддона.
        ulong steamId = sid.SteamId64;
        if (steamId == 0)
        {
            Debug($"слот {playerSlot}: SteamID не пришёл, пропускаем");
            return;
        }

        var player = Utilities.GetPlayerFromSlot(playerSlot);
        if (player != null && (player.IsBot || player.IsHLTV))
            return;

        if (!_tracked.TryGetValue(steamId, out var t))
        {
            t = new Tracked();
            _tracked[steamId] = t;
        }
        t.Silent = IsSilent(steamId);

        var nameNow = player is { IsValid: true } ? player.PlayerName : null;
        if (!string.IsNullOrEmpty(nameNow)) t.Name = nameNow;
        t.LastSeen = DateTime.UtcNow;

        // Вернулся раньше, чем мы успели объявить уход — значит ухода и не было.
        if (t.PendingLeave != null)
        {
            t.PendingLeave.Kill();
            t.PendingLeave = null;
            Debug($"{t.Name}: вернулся до истечения окна, сообщение об уходе отменено");
        }

        // Уже объявлен — это очередной круг докачки аддона, молчим.
        if (t.AnnouncedConnecting)
        {
            Debug($"{t.Name}: повторный коннект (докачка аддона), не объявляем");
            return;
        }

        t.AnnouncedConnecting = true;

        // Только начало захода: клиенту ещё качать аддоны по кругу.
        // Печатаем следующим кадром — на самой авторизации ник может быть пуст.
        if (Config.ShowConnecting && !t.Silent)
        {
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayerFromSlot(playerSlot);
                var nm = p is { IsValid: true } ? p.PlayerName : null;
                if (!string.IsNullOrEmpty(nm)) t.Name = nm;

                if (string.IsNullOrEmpty(t.Name))
                {
                    Debug($"слот {playerSlot}: ник так и не появился, «подключается» не пишем");
                    return;
                }

                Server.PrintToChatAll(L("Connecting", t.Name));
            });
        }
    }

    /// <summary>
    /// Игрок доехал до сервера по-настоящему. Круги докачки аддонов до этого
    /// события не доходят, поэтому оно и служит признаком реального захода.
    /// </summary>
    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        ulong steamId = player.SteamID;
        if (steamId == 0) return HookResult.Continue;

        if (!_tracked.TryGetValue(steamId, out var t))
        {
            t = new Tracked { AnnouncedConnecting = true };
            _tracked[steamId] = t;
        }

        t.Name = player.PlayerName ?? t.Name;
        t.LastSeen = DateTime.UtcNow;
        t.Silent = IsSilent(steamId);

        // Хайд по первому выбору — на КАЖДЫЙ заход. Сообщения о реконнекте глушатся
        // (игрок «не уходил»), но меню выбора команды клиент показывает заново.
        if (Config.AutoHideAdmins && ServerAdmin(steamId) != null)
            _pendingHide.Add(steamId);

        if (t.AnnouncedJoined) return HookResult.Continue;
        t.AnnouncedJoined = true;

        Debug($"{t.Name}: доехал до сервера" + (t.Silent ? " (админ, молча)" : ""));

        if (Config.ShowConnect && !t.Silent)
            Server.PrintToChatAll(L("Connected", t.Name));



        return HookResult.Continue;
    }

    // ══════════════════════════════════
    //  ВЫХОД
    // ══════════════════════════════════

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
            return HookResult.Continue;

        ulong steamId = player.SteamID;
        if (steamId == 0) return HookResult.Continue;

        int reason = @event.Reason;
        if (!Config.ReconnectReasons.Contains(reason))
            _pendingHide.Remove(steamId);

        // Цикл докачки аддонов в MultiAddonManager — не настоящий уход.
        if (Config.ReconnectReasons.Contains(reason))
        {
            Debug($"{player.PlayerName}: отключение с причиной {reason} (переподключение), игнорируем");
            return HookResult.Continue;
        }

        if (!_tracked.TryGetValue(steamId, out var t) || !t.AnnouncedJoined)
        {
            // Не доехал до сервера — значит и не уходил. Молчим.
            _tracked.Remove(steamId);
            return HookResult.Continue;
        }

        t.Name = player.PlayerName ?? t.Name;

        // Явное расставание — ждать нечего, объявляем сразу.
        if (Config.ImmediateLeaveReasons.Contains(reason))
        {
            t.PendingLeave?.Kill();
            t.PendingLeave = null;
            _tracked.Remove(steamId);

            Debug($"{t.Name}: отключение с причиной {reason} (явный уход), объявляем сразу");

            if (Config.ShowDisconnect && !t.Silent)
                Server.PrintToChatAll(L("Disconnect", t.Name));

            return HookResult.Continue;
        }

        // Причина неизвестная — возможно, это докачка аддона. Ждём окно.
        t.PendingLeave?.Kill();
        t.PendingLeave = AddTimer(Math.Max(1, Config.SettleSeconds), () =>
        {
            if (!_tracked.TryGetValue(steamId, out var cur) || cur.PendingLeave == null)
                return;

            cur.PendingLeave = null;
            _tracked.Remove(steamId);

            if (Config.ShowDisconnect && !cur.Silent)
                Server.PrintToChatAll(L("Disconnect", cur.Name));
        });

        Debug($"{t.Name}: отключение с причиной {reason}, объявим через {Config.SettleSeconds}с, если не вернётся");
        return HookResult.Continue;
    }

    // ══════════════════════════════════
    //  УБОРКА
    // ══════════════════════════════════

    private void StartCleanupTimer()
    {
        _cleanupTimer?.Kill();
        _cleanupTimer = AddTimer(60f, Cleanup, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    /// <summary>
    /// Забываем тех, кто отвалился на докачке аддона и не вернулся. Сообщения не
    /// пишем: такой игрок до сервера фактически и не доехал.
    /// </summary>
    private void Cleanup()
    {
        var online = new HashSet<ulong>();
        try
        {
            foreach (var p in Utilities.GetPlayers())
            {
                if (p is { IsValid: true, IsBot: false, IsHLTV: false } && p.SteamID != 0)
                    online.Add(p.SteamID);
            }
        }
        catch
        {
            return; // на старте карты список может быть недоступен
        }

        var now = DateTime.UtcNow;
        var stale = new List<ulong>();

        foreach (var (steamId, t) in _tracked)
        {
            if (online.Contains(steamId))
            {
                t.LastSeen = now;
                continue;
            }

            if (t.PendingLeave != null) continue; // за него уже отвечает свой таймер

            if ((now - t.LastSeen).TotalMinutes >= Math.Max(1, Config.ForgetAfterMinutes))
                stale.Add(steamId);
        }

        foreach (var steamId in stale)
        {
            Debug($"{_tracked[steamId].Name}: не вернулся после переподключения, забываем без сообщения");
            _tracked.Remove(steamId);
        }
    }

    private void Debug(string message)
    {
        if (Config.Debug)
            Console.WriteLine($"[{ModuleName}] {message}");
    }
}
